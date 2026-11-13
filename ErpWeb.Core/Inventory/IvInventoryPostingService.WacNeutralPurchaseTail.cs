using ErpWeb.Core.Purchase;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record LaterInvoicePlan(PoInvoice Invoice, string Fingerprint, long? ValueEventId,
        StockReplayPurchaseVarianceGeneration? VarianceGeneration, StockReplayWacStep? PurchaseStep);

    // This only selects a candidate for the read-only projection. It does not
    // authorize a write; the complete invoice proof below is still mandatory.
    private static async Task<long?> FindLaterPurchaseVarianceFactAsync(
        AppDbContext db, string company, string branch, string item,
        IReadOnlyList<StockReplayPhysicalEvent> physical, CancellationToken ct)
    {
        if (physical.Count == 0 || physical.Count > 100
            || physical.Any(p => p.EventId is not (> 0 and <= int.MaxValue)))
            return null;
        var ids = physical.Select(p => (int)p.EventId).ToArray();
        var goods = await db.IvTrxHistories.AsNoTracking().Where(h => ids.Contains(h.Id)
            && h.CompanyCode == company && h.BranchCode == branch && h.ICode == item
            && h.TrxType == IvTrxTypes.GoodsReceive).Select(h => h.Id).ToArrayAsync(ct);
        if (goods.Length != 1)
            return null;
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.ItemCode == item
            && s.ReceiptInventoryHistoryId == goods[0] && s.ReversesSettlementId == null
            && !db.PurchaseReceiptCostSettlements.Any(r => r.ReversesSettlementId == s.Id))
            .ToArrayAsync(ct);
        if (settlements.Length != 1)
            return null;
        var settlement = settlements[0];
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a =>
            a.CompanyCode == company && a.BranchCode == branch && a.ItemCode == item
            && a.SourceDocumentType == "PO_INVOICE" && a.SourceDocumentNo == settlement.PiDocNo
            && a.SourceDocumentLine == settlement.PiLineNo && a.SourceCostingRevision == settlement.PiCostingRevision
            && a.StockPostingId == settlement.StockPostingId && a.ReversesAdjustmentId == null)
            .ToArrayAsync(ct);
        return adjustments.Length == 1 && adjustments[0].InventoryAdjustmentFactId is > 0
            ? adjustments[0].InventoryAdjustmentFactId : null;
    }

    private static IReadOnlyList<StockReplayWacEvent> InventoryTailForProvenInvoice(
        IReadOnlyList<StockReplayWacEvent> events, LaterInvoicePlan? plan) =>
        plan?.ValueEventId is long id ? events.Where(e => e.EventId != id).ToArray() : events;

    private static StockReplayPurchaseVarianceWritePermit? PreparePurchaseVarianceWrite(
        StockPostingContext context, LaterInvoicePlan? plan, string planHash, long watermark,
        IReadOnlyList<StockReplayWacEvent> events, IReadOnlyList<StockReplayWacStep> steps)
    {
        if (plan?.VarianceGeneration is not { } generation || plan.PurchaseStep is not { } step
            || step.SignedValue == generation.Active.InventoryAdjustmentAmount)
            return null;
        var byId = steps.ToDictionary(s => s.EventId);
        var changedHistories = events.Count(e => e.EventId is > 0 and <= int.MaxValue
            && byId.TryGetValue(e.EventId, out var projected) && projected.SignedValue != e.OldSignedValue);
        return new StockReplayPurchaseVarianceWritePermit(context, generation, step, planHash, watermark,
            events.Max(e => e.Key.EffectiveAt), changedHistories + 1);
    }

    // A miscellaneous receipt leaves GR allocation and invoice amounts unchanged.
    // Prove the single invoice and its active variance before and under the lock.
    private async Task<(LaterInvoicePlan? Plan, string? Error)> PlanLaterInvoiceAsync(
        AppDbContext db, string company, string branch, string item, long epoch, int pile,
        DateTime receiptDate, IReadOnlyList<StockReplayPhysicalEvent> physical,
        IReadOnlyList<StockReplayWacEvent> valued, IReadOnlyList<StockReplayWacStep> projectedSteps,
        decimal currentQty, decimal currentValue, long? purchaseFactId, CancellationToken ct,
        bool lockEvidence = false, string? expectedInvoiceNo = null)
    {
        var purchaseEvents = valued.Where(e => e.EventId > int.MaxValue).ToArray();
        if (purchaseFactId is long candidateFactId
            && (purchaseEvents.Length != 1 || purchaseEvents[0].EventId != long.MaxValue - candidateFactId)
            || purchaseFactId is null && purchaseEvents.Length != 0)
            return (null, "The later purchase value adjustment needs a verified settlement replay.");
        var inventoryEvents = valued.Where(e => e.EventId <= int.MaxValue).ToArray();
        var tailHash = ReversedSaleTailFingerprint(physical, inventoryEvents);
        if (tailHash is null || physical.Count == 0)
            return (null, null);
        var ids = physical.Select(p => checked((int)p.EventId)).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id)).ToArrayAsync(ct);
        if (histories.Length != ids.Length || histories.Any(h =>
                h.CompanyCode != company || h.BranchCode != branch || h.ICode != item
                || h.TrxType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive or IvTrxTypes.MiscellaneousIssue)))
            return (null, null);
        var goods = histories.Where(h => h.TrxType == IvTrxTypes.GoodsReceive).ToArray();
        if (goods.Length != 1)
            return (null, null);
        var gr = goods[0];
        if (string.IsNullOrWhiteSpace(gr.PoNo) || gr.PoRelNo is null || gr.PoLineNo is null)
            return (null, "The later goods receipt has no exact purchase-order line.");

        if (lockEvidence && db.Database.IsSqlServer())
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Purchase evidence must be locked inside the replay transaction.");
            try
            {
                // Invoice posting holds its invoice and PO locks before taking the
                // branch lock. NOWAIT avoids a deadlock while this replay owns it.
                var orders = await db.PoOrders.FromSqlInterpolated($@"
                    SELECT * FROM dbo.POOrder WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                    WHERE CompanyCode = {company} AND BranchCode = {branch}
                        AND PONo = {gr.PoNo} AND PORelNo = {gr.PoRelNo}")
                    .AsNoTracking().ToArrayAsync(ct);
                var lines = await db.PoOrderDetails.FromSqlInterpolated($@"
                    SELECT * FROM dbo.PODetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                    WHERE CompanyCode = {company} AND BranchCode = {branch}
                        AND PONo = {gr.PoNo} AND PORelNo = {gr.PoRelNo} AND Line = {gr.PoLineNo}")
                    .AsNoTracking().ToArrayAsync(ct);
                if (orders.Length != 1 || lines.Length != 1)
                    return (null, "The later purchase order changed. Retry the receipt.");
                var relatedLines = await db.PoInvoiceDetails.FromSqlInterpolated($@"
                    SELECT * FROM dbo.POInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                    WHERE CompanyCode = {company} AND BranchCode = {branch}
                        AND PONo = {gr.PoNo} AND PORelNo = {gr.PoRelNo} AND POLineNo = {gr.PoLineNo}")
                    .AsNoTracking().ToArrayAsync(ct);
                var invoiceNos = relatedLines.Select(d => d.DocNo)
                    .Concat(expectedInvoiceNo is null ? [] : new[] { expectedInvoiceNo })
                    .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                foreach (var invoiceNo in invoiceNos)
                {
                    var invoices = await db.PoInvoices.FromSqlInterpolated($@"
                        SELECT * FROM dbo.POInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                        WHERE CompanyCode = {company} AND BranchCode = {branch} AND DocNo = {invoiceNo}")
                        .AsNoTracking().ToArrayAsync(ct);
                    if (invoices.Length != 1)
                        return (null, "The later purchase invoice changed. Retry the receipt.");
                }
            }
            catch (SqlException error) when (error.Number == 1222)
            {
                return (null, "The later purchase order or invoice is busy. Retry the receipt.");
            }
        }

        var invoiceResult = await LoadSingleGrReplayInvoiceAsync(db, company, branch,
            gr.PoNo, gr.PoRelNo, gr.PoLineNo, item, gr.TrxDtTime, ct, requireAdjustmentWriter: false);
        if (invoiceResult.Error is not null)
            return (null, invoiceResult.Error);
        if (invoiceResult.Invoice is not { } invoice)
            return (null, null);

        const string unsupported = "The later purchase invoice needs a changed price adjustment or settlement; purchase-cost replay is required.";
        if (invoice.DocDate < gr.TrxDtTime || invoice.DocDate.Year != receiptDate.Year
            || invoice.DocDate.Month != receiptDate.Month || invoice.CurrRate <= 0m
            || await db.PoInvoices.AsNoTracking().AnyAsync(x => x.CompanyCode == company
                && x.BranchCode == branch && x.Type == PoInvoiceTypes.CreditNote
                && x.InvNo == invoice.DocNo, ct))
            return (null, unsupported);

        var fixedReceipts = new List<WacFixedReceiptProof>();
        foreach (var movement in valued.Where(e => e.OldSignedQuantity > 0m))
        {
            var history = histories.Single(h => h.Id == movement.EventId);
            var proof = await ProveOriginalWacFixedReceiptAsync(db, company, branch, item,
                epoch, pile, history, movement, ct);
            if (proof is null)
                return (null, unsupported);
            fixedReceipts.Add(proof);
        }
        var grProof = fixedReceipts.SingleOrDefault(p => p.HistoryId == gr.Id);
        var line = invoice.Details.Single();
        var order = await db.PoOrders.AsNoTracking().SingleOrDefaultAsync(o =>
            o.CompanyCode == company && o.BranchCode == branch && o.PoNo == gr.PoNo
            && o.PoRelNo == gr.PoRelNo, ct);
        var orderLine = await db.PoOrderDetails.AsNoTracking().SingleOrDefaultAsync(d =>
            d.CompanyCode == company && d.BranchCode == branch && d.PoNo == gr.PoNo
            && d.PoRelNo == gr.PoRelNo && d.Line == gr.PoLineNo, ct);
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch
                && (s.PiDocNo == invoice.DocNo || s.ReceiptInventoryHistoryId == gr.Id))
            .ToArrayAsync(ct);
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(a => a.CompanyCode == company && a.BranchCode == branch
                && a.SourceDocumentType == "PO_INVOICE" && a.SourceDocumentNo == invoice.DocNo)
            .ToArrayAsync(ct);
        if (grProof is null || order is null || orderLine is null || settlements.Length != 1
            || adjustments.Count(a => a.ReversesAdjustmentId is null) != 1)
            return (null, unsupported);
        var settlement = settlements[0];
        var adjustment = adjustments.Single(a => a.ReversesAdjustmentId is null);
        var children = adjustments.Where(a => a.ReversesAdjustmentId.HasValue)
            .GroupBy(a => a.ReversesAdjustmentId!.Value).ToArray();
        if (children.Any(g => g.Count() != 1))
            return (null, unsupported);
        var childByParent = children.ToDictionary(g => g.Key, g => g.Single());
        var chain = new List<PurchaseCostAdjustment> { adjustment };
        while (childByParent.TryGetValue(chain[^1].Id, out var child))
        {
            if (chain.Any(a => a.Id == child.Id) || chain.Count >= 101)
                return (null, unsupported);
            chain.Add(child);
        }
        var chainIds = chain.Select(a => a.Id).ToArray();
        if (chain.Count != adjustments.Length || await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(a =>
                a.ReversesAdjustmentId.HasValue && chainIds.Contains(a.ReversesAdjustmentId.Value)
                && !chainIds.Contains(a.Id), ct))
            return (null, unsupported);
        var roots = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.SourceModule == "PROCUREMENT"
            && p.SourceDocumentType == "PO_INVOICE" && p.SourceDocumentId == invoice.DocNo
            && p.DocumentRevision == invoice.CostingRevision && p.PostingRole == "PRIMARY")
            .ToArrayAsync(ct);
        if (roots.Length != 1)
            return (null, unsupported);
        var root = roots[0];
        var actual = StockLedgerPrecision.Money(line.NetAmount * invoice.CurrRate);
        var variance = StockLedgerPrecision.Money(actual - grProof.CostAmount);
        var rootFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.StockPostingId == root.Id).ToArrayAsync(ct);
        StockValuationFact? purchaseValue = null;
        StockReplayWacStep? purchaseStep = null;
        StockReplayPurchaseVarianceGeneration? generation = null;
        if (variance != 0m)
        {
            if (purchaseFactId is not long factId || rootFacts.Length != 1 || rootFacts[0].Id != factId
                || adjustment.InventoryAdjustmentFactId != factId || purchaseEvents.Length != 1)
                return (null, unsupported);
            purchaseValue = rootFacts[0];
            generation = await StockReplayPurchaseVarianceGenerationReader.TryReadAsync(db, chain, ct);
            if (generation is null || generation.OriginalFact.Id != factId)
                return (null, unsupported);
            purchaseStep = projectedSteps.SingleOrDefault(s => s.EventId == purchaseEvents[0].EventId);
            var oldQty = StockLedgerPrecision.Quantity(currentQty - valued.Where(e =>
                e.Key.CompareTo(purchaseEvents[0].Key) >= 0).Sum(e => e.OldSignedQuantity));
            var oldValue = StockLedgerPrecision.Money(currentValue - valued.Where(e =>
                e.Key.CompareTo(purchaseEvents[0].Key) >= 0).Sum(e => e.OldSignedValue));
            if (purchaseStep is null || oldQty < 0m || oldValue < 0m)
                return (null, unsupported);
            var oldSplit = StockPurchaseVarianceAllocator.Allocate(variance, line.StdQty,
                Math.Min(oldQty, line.StdQty), oldValue, StockCostMethods.MovingAverage);
            var newSplit = StockPurchaseVarianceAllocator.Allocate(variance, line.StdQty,
                Math.Min(purchaseStep.QuantityBefore, line.StdQty), purchaseStep.ValueBefore, StockCostMethods.MovingAverage);
            if (oldSplit.Inventory != generation.Active.InventoryAdjustmentAmount
                || oldSplit.Consumed != generation.Active.ConsumedVarianceAmount
                || newSplit.Inventory == 0m || purchaseStep.SignedQuantity != 0m
                || purchaseStep.SignedValue != newSplit.Inventory
                || purchaseEvents[0].OldSignedValue != generation.ActiveFact.SignedValue
                || purchaseEvents[0].FixedSignedValue != generation.ActiveFact.SignedValue
                || purchaseEvents[0].PurchaseVariance != new StockReplayPurchaseVariance(variance, line.StdQty)
                || purchaseValue.OriginalValuationFactId != grProof.FactId
                || purchaseValue.SourceDocumentLine != line.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || purchaseValue.PostingLineNo != line.Line || purchaseValue.SplitOrdinal != 0)
                return (null, unsupported);
        }
        else if (purchaseFactId is not null || rootFacts.Length != 0 || adjustments.Length != 1
            || adjustment.InventoryAdjustmentFactId is not null)
            return (null, unsupported);
        if (root.SourceDocumentNo != invoice.DocNo || root.CommandType != "PROCUREMENT_PO_INVOICE_PRIMARY"
            || root.LedgerEpochId != epoch || root.SealedAtUtc is null || root.EffectiveAt != invoice.DocDate
            || root.ReversesPostingId is not null
            || !MatchesPurchaseInvoiceSnapshot(root, invoice, line)
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == root.Id, ct)
            || gr.ToStdQty != line.StdQty || orderLine.InvoicedQty != line.StdQty
            || !string.Equals(line.StdUom, gr.ToStdUom, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(line.SellingUom, gr.ToStdUom, StringComparison.OrdinalIgnoreCase)
            || settlement.PiDocNo != invoice.DocNo || settlement.PiLineNo != line.Line
            || settlement.PiCostingRevision != invoice.CostingRevision || settlement.StockPostingId != root.Id
            || settlement.ReversesSettlementId is not null || settlement.PoNo != gr.PoNo
            || await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s => s.ReversesSettlementId == settlement.Id, ct)
            || settlement.PoRelNo != gr.PoRelNo || settlement.PoLineNo != gr.PoLineNo
            || settlement.ItemCode != item || settlement.ReceiptValuationFactId != grProof.FactId
            || settlement.ReceiptInventoryHistoryId != gr.Id || settlement.ReceiptBatchNo != gr.BatchNo
            || settlement.SettledBaseQty != line.StdQty || settlement.ReceiptValuationUnitCost != grProof.UnitCost
            || settlement.ReceiptValuationBaseAmount != grProof.CostAmount
            || settlement.ReceiptCommercialUnitCost != grProof.UnitCost
            || settlement.ReceiptCommercialBaseAmount != grProof.CostAmount
            || settlement.AllocatedActualBaseAmount != actual
            || settlement.CommercialVarianceAmount != variance || settlement.ValuationVarianceAmount != variance
            || adjustment.AdjustmentType != "PURCHASE_PRICE_VARIANCE" || adjustment.StockPostingId != root.Id
            || adjustment.SourceDocumentLine != line.Line || adjustment.SourceCostingRevision != invoice.CostingRevision
            || adjustment.PoNo != gr.PoNo || adjustment.PoRelNo != gr.PoRelNo || adjustment.PoLineNo != gr.PoLineNo
            || adjustment.ItemCode != item || adjustment.CostMethod != StockCostMethods.MovingAverage
            || adjustment.BaseQty != line.StdQty || adjustment.ActualBaseAmount != actual
            || adjustment.ReferenceBaseAmount != grProof.CostAmount || adjustment.CommercialReferenceAmount != grProof.CostAmount
            || adjustment.TotalAdjustmentAmount != variance
            || variance == 0m && (adjustment.InventoryAdjustmentAmount != 0m || adjustment.ConsumedVarianceAmount != 0m)
            || adjustment.ReversesAdjustmentId is not null)
            return (null, unsupported);

        var fingerprint = StockPostingFingerprint.Create(new { Kind = "WAC_MR_UNCHANGED_LATER_INVOICE" }, new
        {
            tailHash, fixedReceipts, gr.Id, gr.BatchNo, gr.PoNo, gr.PoRelNo, gr.PoLineNo,
            invoice.DocNo, invoice.DocDate, invoice.Status, invoice.Type, invoice.CostingRevision,
            invoice.Currency, invoice.CurrRate, invoice.RowVersion,
            Line = new { line.Line, line.ICode, line.Qty, line.StdQty, line.NetAmount, line.UnitPrice,
                line.SellingUom, line.StdUom, line.PoNo, line.PoRelNo, line.PoLineNo },
            Order = new { order.RowVersion, order.Status, order.FinClosed },
            OrderLine = new { orderLine.RecvQty, orderLine.ReturnQty, orderLine.ReturnQtyCn,
                orderLine.InvoicedQty, orderLine.PoQty, orderLine.PoUnitPrice, orderLine.StdUom, orderLine.PurchaseUom },
            Owner = new { root.Id, root.PostingSequence, root.RequestFingerprint, root.SourceSnapshotHash, root.SealedAtUtc },
            Settlement = new { settlement.Id, settlement.ReceiptValuationFactId, settlement.SettledBaseQty,
                settlement.ReceiptCommercialUnitCost, settlement.ReceiptCommercialBaseAmount,
                settlement.ReceiptValuationUnitCost, settlement.ReceiptValuationBaseAmount,
                settlement.AllocatedActualBaseAmount, settlement.CommercialVarianceAmount, settlement.ValuationVarianceAmount },
            Adjustment = new { adjustment.Id, adjustment.BaseQty, adjustment.ActualBaseAmount, adjustment.ReferenceBaseAmount,
                adjustment.CommercialReferenceAmount, adjustment.TotalAdjustmentAmount, adjustment.InventoryAdjustmentAmount,
                adjustment.ConsumedVarianceAmount, adjustment.InventoryAdjustmentFactId },
            PurchaseValue = purchaseValue is null ? null : new { purchaseValue.Id, purchaseValue.SignedValue,
                purchaseValue.OriginalValuationFactId, purchaseValue.EffectiveAt, purchaseValue.SourceDocumentLine },
            PurchaseStep = purchaseStep, Generation = generation?.Fingerprint
        }).SourceSnapshotHash;
        return (new(invoice, fingerprint, purchaseEvents.SingleOrDefault()?.EventId, generation, purchaseStep), null);
    }

    private static bool MatchesPurchaseInvoiceSnapshot(StockPosting root, PoInvoice invoice, PoInvoiceDetail line)
    {
        try
        {
            if (StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(root.SourceSnapshotJson)) != root.SourceSnapshotHash)
                return false;
            using var snapshot = JsonDocument.Parse(root.SourceSnapshotJson);
            var header = snapshot.RootElement;
            var lines = header.GetProperty("lines");
            if (lines.GetArrayLength() != 1)
                return false;
            var savedLine = lines[0];
            return header.GetProperty("docNo").GetString() == invoice.DocNo
                && header.GetProperty("type").GetString() == invoice.Type
                && header.GetProperty("docDate").GetDateTime() == invoice.DocDate
                && header.GetProperty("currency").GetString() == invoice.Currency
                && header.GetProperty("currRate").GetDecimal() == invoice.CurrRate
                && header.GetProperty("costingRevision").GetInt32() == invoice.CostingRevision
                && savedLine.GetProperty("line").GetInt32() == line.Line
                && savedLine.GetProperty("iCode").GetString() == line.ICode
                && savedLine.GetProperty("stdQty").GetDecimal() == line.StdQty
                && savedLine.GetProperty("netAmount").GetDecimal() == line.NetAmount
                && savedLine.GetProperty("poNo").GetString() == line.PoNo
                && savedLine.GetProperty("poRelNo").GetInt32() == line.PoRelNo
                && savedLine.GetProperty("poLineNo").GetInt32() == line.PoLineNo
                && savedLine.GetProperty("referencedInvoiceLineNo").ValueKind == JsonValueKind.Null
                && line.ReferencedInvoiceLineNo is null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }
}
