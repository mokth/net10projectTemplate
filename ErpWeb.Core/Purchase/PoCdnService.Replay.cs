using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoCdnService
{
    private sealed record CdnIssueReplayPlan(
        string Fingerprint, long EpochId, long Watermark, string InvoiceNo,
        string PoNo, short PoRelNo, short PoLineNo, long? OriginalPostingId, long? OriginalFactId,
        PurchaseCostAdjustmentRequest Adjustment, PoInvoiceService.InvoiceIssueReplay Replay);

    private async Task<CdnIssueReplayPlan?> PreviewCdnIssueReplayAsync(
        UserContext write, string docNo, bool posting, CancellationToken ct)
    {
        if (!_openPeriodBackdatedReplayEnabled || _branchStockLock is null || _cdnCostAdjustments is null
            || _stockCoordinator is null)
            return null;
        try
        {
            await using var preview = await _dbFactory.CreateDbContextAsync(ct);
            var cdn = await preview.PoCdns.AsNoTracking().Include(c => c.Details).SingleOrDefaultAsync(c =>
                c.CompanyCode == write.CompanyCode && c.BranchCode == write.BranchCode && c.DocNo == docNo, ct);
            if (cdn is null || string.IsNullOrWhiteSpace(cdn.InvNo))
                return null;
            var invoice = await preview.PoInvoices.AsNoTracking().Include(i => i.Details).SingleOrDefaultAsync(i =>
                i.CompanyCode == write.CompanyCode && i.BranchCode == write.BranchCode && i.DocNo == cdn.InvNo, ct);
            return invoice is null ? null : await PlanCdnIssueReplayAsync(preview, cdn, invoice, posting, ct);
        }
        catch (StockLedgerException) { return null; }
        catch (ArgumentException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static async Task<bool> LockCdnReplaySourcesAsync(
        AppDbContext db, UserContext write, string docNo, CdnIssueReplayPlan plan, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer() || db.Database.CurrentTransaction is null)
            return false;
        // Other purchase paths may hold a document lock before requesting the
        // branch lock. Do not wait on those locks after acquiring the branch.
        var invoices = await db.PoInvoices.FromSqlInterpolated($@"SELECT * FROM dbo.POInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {plan.InvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var invoiceLines = await db.PoInvoiceDetails.FromSqlInterpolated($@"SELECT * FROM dbo.POInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {plan.InvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var notes = await db.PoCdns.FromSqlInterpolated($@"SELECT * FROM dbo.PoCdn WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {docNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var lines = await db.PoCdnDetails.FromSqlInterpolated($@"SELECT * FROM dbo.PoCdnDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode} AND DocNo = {docNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var orders = await db.PoOrders.FromSqlInterpolated($@"SELECT * FROM dbo.POOrder WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode}
                AND PONo = {plan.PoNo} AND PORelNo = {plan.PoRelNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var orderLines = await db.PoOrderDetails.FromSqlInterpolated($@"SELECT * FROM dbo.PODetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {write.CompanyCode} AND BranchCode = {write.BranchCode}
                AND PONo = {plan.PoNo} AND PORelNo = {plan.PoRelNo} AND Line = {plan.PoLineNo}")
            .AsNoTracking().ToArrayAsync(ct);
        return invoices.Length == 1 && invoiceLines.Length > 0 && notes.Length == 1
            && lines.Length == 1 && orders.Length == 1 && orderLines.Length == 1;
    }

    // The first route supports a single financial CN/DN and one later ordinary
    // issue. All other dependencies continue through the existing safety guard.
    private async Task<CdnIssueReplayPlan?> PlanCdnIssueReplayAsync(
        AppDbContext db, PoCdn cdn, PoInvoice invoice, bool posting, CancellationToken ct)
    {
        if (_cdnCostAdjustments is null || cdn.ReturnStock || cdn.DeletedAtUtc is not null
            || cdn.Status != (posting ? PoCdnStatuses.New : PoCdnStatuses.Posted)
            || cdn.Details.Count != 1 || invoice.Status != PoInvoiceStatuses.Posted
            || invoice.Type != PoInvoiceTypes.Invoice || invoice.DeletedAtUtc is not null
            || invoice.DocNo != cdn.InvNo || invoice.DocDate > cdn.DocDate)
            return null;
        var line = cdn.Details.Single();
        if (string.IsNullOrWhiteSpace(line.ICode) || line.InvLineNo is not > 0
            || line.IsStockReturn || line.StdQty <= 0m || line.NetAmount <= 0m
            || invoice.Details.Count(d => d.Line == line.InvLineNo) != 1)
            return null;
        var company = cdn.CompanyCode;
        var branch = cdn.BranchCode;
        var item = line.ICode.Trim();
        if (posting && !await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.CompanyCode == company
                && h.BranchCode == branch && h.ICode == item && h.TrxType == IvTrxTypes.MiscellaneousIssue
                && h.EntryRole == "ORIGINAL" && h.TrxDtTime > cdn.DocDate, ct))
            return null;
        var invoiceLine = invoice.Details.Single(d => d.Line == line.InvLineNo);
        if (invoiceLine.ICode != item || invoiceLine.PoNo is null || invoiceLine.PoRelNo is null
            || invoiceLine.PoLineNo is null)
            return null;
        var epochs = await db.StockLedgerEpochs.AsNoTracking().Where(e => e.CompanyCode == company
            && e.BranchCode == branch && e.Status == StockLedgerEpochStatuses.Active && e.Version == 2).ToArrayAsync(ct);
        if (epochs.Length != 1 || epochs[0].EffectiveFrom.Date > cdn.DocDate.Date
            || await _costMethods.ResolveAsync(db, company, branch, invoice.DocDate, ct) != StockCostMethods.MovingAverage
            || await _costMethods.ResolveAsync(db, company, branch, cdn.DocDate, ct) != StockCostMethods.MovingAverage)
            return null;
        var watermark = await db.StockPostingBranchSequences.AsNoTracking().Where(s => s.CompanyCode == company
            && s.BranchCode == branch).Select(s => (long?)s.LastSequence).SingleOrDefaultAsync(ct) ?? 0;
        var latest = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.SealedAtUtc != null).MaxAsync(p => (long?)p.PostingSequence, ct) ?? 0;
        if (watermark < latest || watermark == long.MaxValue)
            return null;
        var sourceSnapshot = new
        {
            cdn.DocNo, cdn.Type, cdn.DocDate, cdn.Currency, cdn.CurrRate, cdn.CostingRevision,
            cdn.InvNo, cdn.ReturnStock, cdn.ReasonCode,
            Lines = cdn.Details.OrderBy(x => x.Line).Select(x => new
            {
                x.Line, x.InvLineNo, x.ICode, x.StdQty, x.NetAmount, x.PoNo, x.PoRelNo, x.PoLineNo, x.IsStockReturn
            }).ToArray(),
            ReferencedInvoiceRevision = invoice.CostingRevision
        };
        StockPosting? original = null;
        PurchaseCostAdjustment? priorAdjustment = null;
        StockValuationFact? priorFact = null;
        PurchaseReceiptSettlementResult result;
        if (posting)
        {
            result = await _cdnCostAdjustments.BuildAsync(db, cdn, invoice, StockCostMethods.MovingAverage, ct);
        }
        else
        {
            var roots = await db.StockPostings.AsNoTracking().Where(p => p.CompanyCode == company
                && p.BranchCode == branch && p.SourceModule == "PROCUREMENT" && p.SourceDocumentType == "PO_CDN"
                && p.SourceDocumentId == cdn.DocNo && p.DocumentRevision == cdn.CostingRevision
                && p.PostingRole == "PRIMARY" && p.SealedAtUtc != null).ToArrayAsync(ct);
            if (roots.Length != 1 || roots[0].CommandType != "PROCUREMENT_PO_CDN_PRIMARY"
                || roots[0].LedgerEpochId != epochs[0].Id || roots[0].EffectiveAt != cdn.DocDate
                || roots[0].ReversesPostingId is not null
                || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == roots[0].Id, ct)
                || StockPostingFingerprint.Create(new { Kind = "CDN_REPLAY_SOURCE" }, sourceSnapshot).SourceSnapshotHash
                    != roots[0].SourceSnapshotHash
                || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(roots[0].SourceSnapshotJson))
                    != roots[0].SourceSnapshotHash)
                return null;
            original = roots[0];
            var adjustments = await db.PurchaseCostAdjustments.AsNoTracking().Where(a => a.StockPostingId == original.Id)
                .ToArrayAsync(ct);
            if (adjustments.Length != 1 || adjustments[0].SourceDocumentType != "PO_CDN"
                || adjustments[0].SourceDocumentNo != cdn.DocNo || adjustments[0].SourceCostingRevision != cdn.CostingRevision
                || adjustments[0].ItemCode != item || adjustments[0].CostMethod != StockCostMethods.MovingAverage
                || adjustments[0].ReversesAdjustmentId is not null || adjustments[0].InventoryAdjustmentFactId is not long factId
                || adjustments[0].InventoryAdjustmentAmount == 0m || adjustments[0].ConsumedVarianceAmount != 0m
                || adjustments[0].InventoryAdjustmentAmount != adjustments[0].TotalAdjustmentAmount
                || await db.PurchaseCostAdjustments.AsNoTracking().AnyAsync(a => a.ReversesAdjustmentId == adjustments[0].Id, ct))
                return null;
            priorAdjustment = adjustments[0];
            priorFact = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f => f.Id == factId, ct);
            if (priorFact is null || priorFact.StockPostingId != original.Id || priorFact.CompanyCode != company
                || priorFact.BranchCode != branch || priorFact.LedgerEpochId != epochs[0].Id
                || priorFact.SourceDocumentType != "PO_CDN" || priorFact.SourceDocumentNo != cdn.DocNo
                || priorFact.ItemCode != item || priorFact.InventoryHistoryId is not null || priorFact.BaseQty != 0m
                || priorFact.CostMethod != StockCostMethods.MovingAverage || priorFact.SignedValue != priorAdjustment.InventoryAdjustmentAmount
                || priorFact.OriginalValuationFactId is not > 0 || priorFact.ReversesValuationFactId is not null
                || priorFact.ValuationStatus != StockValuationStatuses.Valued
                || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId == priorFact.Id, ct))
                return null;
            result = await _cdnCostAdjustments.ReverseAsync(db, cdn, original.Id, "REPLAY_PLAN", ct);
        }
        if (!result.Succeeded || result.Adjustments.Count != 1)
            return null;
        var adjustment = result.Adjustments.Single();
        var value = adjustment.ValueAdjustment;
        if (adjustment.SourceDocumentType != "PO_CDN" || adjustment.SourceDocumentNo != cdn.DocNo
            || adjustment.SourceCostingRevision != cdn.CostingRevision || adjustment.SourceDocumentLine != line.Line
            || adjustment.ItemCode != item || adjustment.CostMethod != StockCostMethods.MovingAverage
            || adjustment.BaseQty != line.StdQty || adjustment.TotalAdjustmentAmount == 0m
            || adjustment.InventoryAdjustmentAmount != adjustment.TotalAdjustmentAmount || adjustment.ConsumedVarianceAmount != 0m
            || value is null || value.Amount != Math.Abs(adjustment.InventoryAdjustmentAmount)
            || value.Direction * value.Amount != adjustment.InventoryAdjustmentAmount
            || adjustment.PoNo != invoiceLine.PoNo || adjustment.PoRelNo != invoiceLine.PoRelNo
            || adjustment.PoLineNo != invoiceLine.PoLineNo
            || (posting ? value.OriginalValuationFactId is not > 0 || value.ReversesValuationFactId is not null
                    || adjustment.ReversesAdjustmentId is not null
                : value.OriginalValuationFactId != priorFact!.Id || value.ReversesValuationFactId != priorFact.Id
                    || adjustment.ReversesAdjustmentId != priorAdjustment!.Id))
            return null;
        var receiptId = posting ? value.OriginalValuationFactId!.Value : priorFact!.OriginalValuationFactId!.Value;
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking().Where(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.PiDocNo == invoice.DocNo
            && s.PiCostingRevision == invoice.CostingRevision && s.PiLineNo == line.InvLineNo
            && s.ReceiptValuationFactId == receiptId && s.ReversesSettlementId == null).ToArrayAsync(ct);
        if (settlements.Length != 1 || settlements[0].ItemCode != item || settlements[0].SettledBaseQty < line.StdQty
            || settlements[0].SettledBaseQty != invoiceLine.StdQty
            || await db.PurchaseReceiptCostSettlements.AsNoTracking().CountAsync(s => s.CompanyCode == company
                && s.BranchCode == branch && s.PiDocNo == invoice.DocNo && s.PiCostingRevision == invoice.CostingRevision
                && s.PiLineNo == line.InvLineNo && s.ReversesSettlementId == null, ct) != 1
            || await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s => s.ReversesSettlementId == settlements[0].Id, ct))
            return null;
        var receipt = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f => f.Id == receiptId, ct);
        if (receipt is null || receipt.CompanyCode != company || receipt.BranchCode != branch
            || receipt.LedgerEpochId != epochs[0].Id || receipt.ItemCode != item || receipt.Direction != 1
            || receipt.InventoryHistoryId is null || receipt.CostMethod != StockCostMethods.MovingAverage
            || receipt.ValuationStatus != StockValuationStatuses.Valued || receipt.ValuationSource != StockValuationSources.ReceiptActual
            || receipt.EffectiveAt > cdn.DocDate || receipt.BaseQty != settlements[0].SettledBaseQty
            || receipt.ReversesValuationFactId is not null || receipt.OriginalValuationFactId is not null
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.ReversesValuationFactId == receipt.Id, ct)
            || !await db.StockPostings.AsNoTracking().AnyAsync(p => p.Id == receipt.StockPostingId
                && p.CompanyCode == company && p.BranchCode == branch && p.LedgerEpochId == epochs[0].Id
                && p.SourceModule == "INVENTORY" && p.SourceDocumentType == IvTrxTypes.GoodsReceive
                && p.CommandType == "IV_POST_GR" && p.PostingRole == "PRIMARY" && p.ReversesPostingId == null
                && p.EffectiveAt == receipt.EffectiveAt && p.SealedAtUtc != null, ct)
            || !await db.StockPostings.AsNoTracking().AnyAsync(p => p.Id == settlements[0].StockPostingId
                && p.CompanyCode == company && p.BranchCode == branch && p.LedgerEpochId == epochs[0].Id
                && p.SourceModule == "PROCUREMENT" && p.SourceDocumentType == "PO_INVOICE"
                && p.SourceDocumentId == invoice.DocNo && p.DocumentRevision == invoice.CostingRevision
                && p.CommandType == "PROCUREMENT_PO_INVOICE_PRIMARY" && p.PostingRole == "PRIMARY"
                && p.ReversesPostingId == null && p.EffectiveAt == invoice.DocDate && p.SealedAtUtc != null, ct)
            || !await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.Id == receipt.InventoryHistoryId
                && h.CompanyCode == company && h.BranchCode == branch && h.StockPostingId == receipt.StockPostingId
                && h.LedgerVersion == 2 && h.LedgerEpochId == epochs[0].Id && h.ICode == item && h.TrxType == IvTrxTypes.GoodsReceive
                && h.EntryRole == "ORIGINAL" && h.BatchStatus == IvBatchStatuses.Posted
                && h.TrxDtTime == receipt.EffectiveAt && h.ToStdQty == receipt.BaseQty && h.ToStdUom == receipt.BaseUom
                && h.PoNo == invoiceLine.PoNo && h.PoRelNo == invoiceLine.PoRelNo && h.PoLineNo == invoiceLine.PoLineNo, ct))
            return null;
        var receiptOwner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == receipt.StockPostingId, ct);
        var invoiceOwner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == settlements[0].StockPostingId, ct);
        var receiptHistory = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == receipt.InventoryHistoryId, ct);
        var invoiceSnapshot = new
        {
            invoice.DocNo, invoice.Type, invoice.DocDate, invoice.Currency, invoice.CurrRate, invoice.CostingRevision,
            Lines = invoice.Details.OrderBy(x => x.Line).Select(x => new
            {
                x.Line, x.ICode, x.StdQty, x.NetAmount, x.PoNo, x.PoRelNo, x.PoLineNo, x.ReferencedInvoiceLineNo
            }).ToArray()
        };
        if (StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(receiptOwner.SourceSnapshotJson)) != receiptOwner.SourceSnapshotHash
            || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(invoiceOwner.SourceSnapshotJson)) != invoiceOwner.SourceSnapshotHash
            || StockPostingFingerprint.Create(new { Kind = "CDN_INVOICE_SOURCE" }, invoiceSnapshot).SourceSnapshotHash != invoiceOwner.SourceSnapshotHash
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == invoiceOwner.Id, ct)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.ReversesHistoryId == receiptHistory.Id, ct)
            || receiptHistory.BaseUnitPrices is not decimal commercialUnit || commercialUnit < 0m
            || settlements[0].ReceiptInventoryHistoryId != receiptHistory.Id || settlements[0].ReceiptBatchNo != receiptHistory.BatchNo
            || settlements[0].ReceiptCommercialUnitCost != StockLedgerPrecision.Money(commercialUnit)
            || settlements[0].ReceiptCommercialBaseAmount != StockLedgerPrecision.Money(receipt.BaseQty * commercialUnit)
            || settlements[0].ReceiptValuationUnitCost != receipt.UnitCost || settlements[0].ReceiptValuationBaseAmount != receipt.CostAmount
            || settlements[0].AllocatedActualBaseAmount != StockLedgerPrecision.Money(invoiceLine.NetAmount * invoice.CurrRate)
            || settlements[0].CommercialVarianceAmount != StockLedgerPrecision.Money(
                settlements[0].AllocatedActualBaseAmount - settlements[0].ReceiptCommercialBaseAmount)
            || settlements[0].ValuationVarianceAmount != StockLedgerPrecision.Money(
                settlements[0].AllocatedActualBaseAmount - settlements[0].ReceiptValuationBaseAmount))
            return null;
        var states = await db.StockCostStates.AsNoTracking().Where(s => s.CompanyCode == company
            && s.BranchCode == branch && s.ItemCode == item).ToArrayAsync(ct);
        var balances = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == item).ToArrayAsync(ct);
        if (states.Length != 1 || states[0].CostMethod != StockCostMethods.MovingAverage || balances.Length != 1
            || balances[0].StdQty != states[0].OnHandBaseQty)
            return null;
        var key = new StockReplayEventKey(cdn.DocDate, StockReplayEventKey.OrdinaryClass,
            posting ? checked(watermark + 1) : original!.PostingSequence,
            posting ? "PURCHASE_CDN" : $"V{priorFact!.Id:D20}", 0);
        if (await PurchaseReplayChronologyGuard.HasLaterCanceledSaleAsync(db, company, branch, epochs[0].Id,
                key.EffectiveAt, key.OriginalPostingSequence, [item], ct))
            return null;
        var tail = posting
            ? await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch, item, key, ct)
            : await StockReplayWacFinancialTailReader.ReadLaterForCdnRemovalAsync(db, company, branch, item, key, priorFact!.Id, ct);
        if (tail.LaterEvents.Count != 1 || tail.CurrentQuantity != states[0].OnHandBaseQty
            || tail.CurrentValue != states[0].InventoryValue || tail.LaterEvents[0].EventId is not (> 0 and <= int.MaxValue)
            || tail.LaterEvents[0].Key.EffectiveAt.Year != cdn.DocDate.Year
            || tail.LaterEvents[0].Key.EffectiveAt.Month != cdn.DocDate.Month)
            return null;
        var historyId = (int)tail.LaterEvents[0].EventId;
        if (!await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.Id == historyId && h.CompanyCode == company
                && h.BranchCode == branch && h.TrxType == IvTrxTypes.MiscellaneousIssue, ct))
            return null;
        var policies = await db.StockCostPolicyRevisions.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && p.Status == StockCostPolicyStatuses.Active
            && p.EffectiveFrom <= cdn.DocDate.Date && (p.EffectiveTo == null || p.EffectiveTo > cdn.DocDate.Date))
            .Select(p => new { p.Id, p.CostMethod, p.EffectiveFrom, p.EffectiveTo, p.Status }).ToArrayAsync(ct);
        var lastDate = tail.LaterEvents[0].Key.EffectiveAt.Date;
        if (policies.Length != 1 || policies[0].EffectiveTo is DateTime end && end.Date <= lastDate
            || await db.StockCostPolicyRevisions.AsNoTracking().AnyAsync(p => p.CompanyCode == company
                && p.BranchCode == branch && p.Status == StockCostPolicyStatuses.Active && p.Id != policies[0].Id
                && p.EffectiveFrom <= lastDate && (p.EffectiveTo == null || p.EffectiveTo > cdn.DocDate.Date), ct))
            return null;
        var candidate = new StockReplayWacEvent(posting ? long.MaxValue : long.MaxValue - priorFact!.Id,
            key, 0m, posting ? 0m : priorFact!.SignedValue, 0m, posting ? 0m : priorFact!.SignedValue);
        var old = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
            candidate with { OldSignedValue = 0m, FixedSignedValue = 0m }, tail.LaterEvents);
        if (old.FinalQuantity != tail.CurrentQuantity || old.FinalValue != tail.CurrentValue
            || old.HistoricalQuantityBeforeCandidate < adjustment.BaseQty
            || old.LaterSteps.Any(step => !tail.LaterEvents.Any(e => e.EventId == step.EventId
                && e.OldSignedQuantity == step.SignedQuantity && e.OldSignedValue == step.SignedValue)))
            return null;
        IReadOnlyList<StockReplayWacStep> projectedSteps;
        decimal finalQuantity, finalValue;
        if (posting)
        {
            var projected = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
                candidate with { FixedSignedValue = adjustment.InventoryAdjustmentAmount }, tail.LaterEvents);
            projectedSteps = projected.LaterSteps;
            finalQuantity = projected.FinalQuantity;
            finalValue = projected.FinalValue;
        }
        else
        {
            var projected = StockReplayWacSimulator.SimulateRemoval(tail.CurrentQuantity, tail.CurrentValue, candidate, tail.LaterEvents);
            projectedSteps = projected.LaterSteps;
            finalQuantity = projected.FinalQuantity;
            finalValue = projected.FinalValue;
        }
        var proof = await PoInvoiceService.PlanInvoiceIssueChangesAsync(db, company, branch, item,
            epochs[0].Id, balances[0].Id, invoice.DocNo, invoiceLine.PoNo, invoiceLine.PoRelNo.Value,
            invoiceLine.PoLineNo.Value, invoiceLine.Qty, invoicePosted: true,
            tail.LaterEvents, projectedSteps, ct);
        if (proof is null || proof.Changes.Count != 1 || proof.GoodsReceipts.Count != 0
            || proof.Sales.Count != 0 || proof.Deliveries.Count != 0)
            return null;
        var replay = new PoInvoiceService.InvoiceIssueReplay(proof.Changes, proof.EvidenceHash,
            proof.GoodsReceipts, proof.Sales, proof.Deliveries, adjustment.InventoryAdjustmentAmount,
            adjustment.ConsumedVarianceAmount, balances[0].Id, balances[0].StdQty,
            tail.CurrentQuantity, tail.CurrentValue, finalQuantity, finalValue);
        var order = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == invoiceLine.PoNo && p.PoRelNo == invoiceLine.PoRelNo)
            .Select(p => new { p.RowVersion, p.Status }).SingleOrDefaultAsync(ct);
        var orderLine = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
            && p.PoNo == invoiceLine.PoNo && p.PoRelNo == invoiceLine.PoRelNo && p.Line == invoiceLine.PoLineNo)
            .Select(p => new { p.ICode, p.PurchaseUom, p.RecvQty, p.ReturnQty, p.InvoicedQty }).SingleOrDefaultAsync(ct);
        if (order is null || orderLine is null || orderLine.ICode != item)
            return null;
        var fingerprint = StockPostingFingerprint.Create(new { Kind = "PO_CDN_WAC_ISSUE_REPLAY" }, new
        {
            posting, sourceSnapshot, cdn.RowVersion, Invoice = new { invoice.DocNo, invoice.DocDate, invoice.Status,
                invoice.CostingRevision, invoice.RowVersion, invoiceLine.Line, invoiceLine.ICode, invoiceLine.StdQty,
                invoiceLine.NetAmount, invoiceLine.PoNo, invoiceLine.PoRelNo, invoiceLine.PoLineNo },
            Epoch = epochs[0].Id, watermark, Settlement = new { settlements[0].Id, settlements[0].StockPostingId,
                settlements[0].ReceiptValuationFactId, settlements[0].SettledBaseQty,
                settlements[0].ReceiptCommercialBaseAmount, settlements[0].AllocatedActualBaseAmount },
            Receipt = new { receipt.Id, receipt.StockPostingId, receipt.CostAmount, receipt.UnitCost, receipt.EffectiveAt },
            ReceiptOwner = new { receiptOwner.Id, receiptOwner.SourceSnapshotHash, receiptOwner.RequestFingerprint },
            InvoiceOwner = new { invoiceOwner.Id, invoiceOwner.SourceSnapshotHash, invoiceOwner.RequestFingerprint },
            Original = original is null ? null : new { original.Id, original.PostingSequence,
                original.SourceSnapshotHash, original.RequestFingerprint },
            PriorAdjustment = priorAdjustment?.Id, PriorFact = priorFact?.Id, policies, order, orderLine,
            Balance = new { balances[0].Id, balances[0].StdQty, balances[0].RowVersion },
            State = new { states[0].Id, states[0].OnHandBaseQty, states[0].InventoryValue,
                states[0].LastValuationFactId, states[0].LastPostingSequence, states[0].RowVersion },
            adjustment, tail.LaterEvents, projectedSteps, finalQuantity, finalValue, replay
        }).SourceSnapshotHash;
        return new(fingerprint, epochs[0].Id, watermark, invoice.DocNo, invoiceLine.PoNo,
            invoiceLine.PoRelNo.Value, invoiceLine.PoLineNo.Value, original?.Id, priorFact?.Id, adjustment, replay);
    }
}
