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
    private sealed record NeutralLaterInvoicePlan(PoInvoice Invoice, string Fingerprint);

    // A miscellaneous receipt does not change the GR candidates for an invoice.
    // Preserve a single original, zero-variance settlement only after proving its
    // complete owner and value evidence before and inside the branch transaction.
    private async Task<(NeutralLaterInvoicePlan? Plan, string? Error)> PlanNeutralLaterInvoiceAsync(
        AppDbContext db, string company, string branch, string item, long epoch, int pile,
        DateTime receiptDate, IReadOnlyList<StockReplayPhysicalEvent> physical,
        IReadOnlyList<StockReplayWacEvent> valued, CancellationToken ct,
        bool lockEvidence = false, string? expectedInvoiceNo = null)
    {
        var tailHash = ReversedSaleTailFingerprint(physical, valued);
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

        const string unsupported = "The later purchase invoice is not an exact value-neutral settlement; purchase-cost replay is required.";
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
        if (grProof is null || order is null || orderLine is null || settlements.Length != 1 || adjustments.Length != 1)
            return (null, unsupported);
        var settlement = settlements[0];
        var adjustment = adjustments[0];
        var roots = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.SourceModule == "PROCUREMENT"
            && p.SourceDocumentType == "PO_INVOICE" && p.SourceDocumentId == invoice.DocNo
            && p.DocumentRevision == invoice.CostingRevision && p.PostingRole == "PRIMARY")
            .ToArrayAsync(ct);
        if (roots.Length != 1)
            return (null, unsupported);
        var root = roots[0];
        var actual = StockLedgerPrecision.Money(line.NetAmount * invoice.CurrRate);
        if (root.SourceDocumentNo != invoice.DocNo || root.CommandType != "PROCUREMENT_PO_INVOICE_PRIMARY"
            || root.LedgerEpochId != epoch || root.SealedAtUtc is null || root.EffectiveAt != invoice.DocDate
            || root.ReversesPostingId is not null
            || !MatchesNeutralInvoiceSnapshot(root, invoice, line)
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == root.Id, ct)
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.StockPostingId == root.Id, ct)
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
            || settlement.AllocatedActualBaseAmount != actual || actual != grProof.CostAmount
            || settlement.CommercialVarianceAmount != 0m || settlement.ValuationVarianceAmount != 0m
            || adjustment.AdjustmentType != "PURCHASE_PRICE_VARIANCE" || adjustment.StockPostingId != root.Id
            || adjustment.SourceDocumentLine != line.Line || adjustment.SourceCostingRevision != invoice.CostingRevision
            || adjustment.PoNo != gr.PoNo || adjustment.PoRelNo != gr.PoRelNo || adjustment.PoLineNo != gr.PoLineNo
            || adjustment.ItemCode != item || adjustment.CostMethod != StockCostMethods.MovingAverage
            || adjustment.BaseQty != line.StdQty || adjustment.ActualBaseAmount != actual
            || adjustment.ReferenceBaseAmount != actual || adjustment.CommercialReferenceAmount != actual
            || adjustment.TotalAdjustmentAmount != 0m || adjustment.InventoryAdjustmentAmount != 0m
            || adjustment.ConsumedVarianceAmount != 0m || adjustment.InventoryAdjustmentFactId is not null
            || adjustment.ReversesAdjustmentId is not null
            || await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(a => a.ReversesAdjustmentId == adjustment.Id, ct))
            return (null, unsupported);

        var fingerprint = StockPostingFingerprint.Create(new { Kind = "WAC_MR_NEUTRAL_LATER_INVOICE" }, new
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
                adjustment.ConsumedVarianceAmount, adjustment.InventoryAdjustmentFactId }
        }).SourceSnapshotHash;
        return (new(invoice, fingerprint), null);
    }

    private static bool MatchesNeutralInvoiceSnapshot(StockPosting root, PoInvoice invoice, PoInvoiceDetail line)
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
