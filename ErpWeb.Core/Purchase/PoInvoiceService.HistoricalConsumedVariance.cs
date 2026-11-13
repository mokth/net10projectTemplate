using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    private sealed record ConsumedInvoicePlan(
        string Fingerprint, long EpochId, long Watermark, string ItemCode,
        string PoNo, short PoRelNo, short PoLineNo, long ReceiptFactId,
        string BaseUom, PurchaseCostAdjustmentRequest Adjustment, InvoiceIssueReplay? IssueReplay);

    private sealed record InvoiceIssueReplay(
        StockReplayWacEvent Issue, StockReplayWacStep IssueStep, long IssueOwnerId, long PriorFactId,
        decimal InventoryVariance, decimal ConsumedVariance, int BalanceId, decimal BalanceQty,
        decimal CurrentQuantity, decimal CurrentValue, decimal FinalQuantity, decimal FinalValue);

    // A price variance has no stock-value effect when the item was completely
    // consumed at invoice time. Prove that state from the complete active WAC
    // tail, before the branch lock and again inside the same locked transaction.
    private async Task<ConsumedInvoicePlan?> PlanConsumedInvoiceAsync(
        AppDbContext db, PoInvoice invoice, CancellationToken ct)
    {
        if (_receiptSettlements is null || invoice.Type != PoInvoiceTypes.Invoice
            || invoice.Status != PoInvoiceStatuses.New || invoice.Details.Count != 1
            || invoice.DeletedAtUtc is not null)
            return null;
        var line = invoice.Details.Single();
        if (string.IsNullOrWhiteSpace(line.ICode) || string.IsNullOrWhiteSpace(line.PoNo)
            || line.PoRelNo is null || line.PoLineNo is null)
            return null;
        var item = line.ICode.Trim();
        // Keep ordinary current-date invoices on their existing fast path.
        if (!await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == invoice.CompanyCode && f.BranchCode == invoice.BranchCode
                && f.ItemCode == item && f.EffectiveAt > invoice.DocDate
                && f.StockPosting!.SealedAtUtc != null, ct))
            return null;
        var epoch = await db.StockLedgerEpochs.AsNoTracking().Where(e =>
            e.CompanyCode == invoice.CompanyCode && e.BranchCode == invoice.BranchCode
            && e.Status == StockLedgerEpochStatuses.Active && e.Version == 2).ToArrayAsync(ct);
        if (epoch.Length != 1 || epoch[0].EffectiveFrom.Date > invoice.DocDate.Date)
            return null;
        var state = await db.StockCostStates.AsNoTracking().Where(s =>
            s.CompanyCode == invoice.CompanyCode && s.BranchCode == invoice.BranchCode
            && s.ItemCode == item).ToArrayAsync(ct);
        if (state.Length != 1 || state[0].CostMethod != StockCostMethods.MovingAverage
            || await _costMethods.ResolveAsync(db, invoice.CompanyCode, invoice.BranchCode,
                invoice.DocDate, ct) != StockCostMethods.MovingAverage)
            return null;
        var watermark = await db.StockPostingBranchSequences.AsNoTracking().Where(s =>
            s.CompanyCode == invoice.CompanyCode && s.BranchCode == invoice.BranchCode)
            .Select(s => (long?)s.LastSequence).SingleOrDefaultAsync(ct) ?? 0;
        var latest = await db.StockPostings.AsNoTracking().Where(p =>
            p.CompanyCode == invoice.CompanyCode && p.BranchCode == invoice.BranchCode
            && p.SealedAtUtc != null).MaxAsync(p => (long?)p.PostingSequence, ct) ?? 0;
        if (watermark < latest || watermark == long.MaxValue)
            return null;
        var policies = await db.StockCostPolicyRevisions.AsNoTracking().Where(p =>
            p.CompanyCode == invoice.CompanyCode && p.BranchCode == invoice.BranchCode
            && p.Status == StockCostPolicyStatuses.Active
            && p.EffectiveFrom <= invoice.DocDate.Date
            && (p.EffectiveTo == null || p.EffectiveTo > invoice.DocDate.Date))
            .Select(p => new { p.Id, p.CostMethod, p.EffectiveFrom, p.EffectiveTo, p.Status }).ToArrayAsync(ct);
        var order = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == invoice.CompanyCode
            && p.BranchCode == invoice.BranchCode && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo)
            .Select(p => new { p.RowVersion, p.Status }).SingleOrDefaultAsync(ct);
        var orderLine = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == invoice.CompanyCode
            && p.BranchCode == invoice.BranchCode && p.PoNo == line.PoNo && p.PoRelNo == line.PoRelNo
            && p.Line == line.PoLineNo)
            .Select(p => new { p.Line, p.ICode, p.PurchaseUom, p.RecvQty, p.ReturnQty, p.InvoicedQty })
            .SingleOrDefaultAsync(ct);
        if (policies.Length != 1 || order is null || orderLine is null)
            return null;

        PurchaseReceiptSettlementResult settlement;
        var before = db.ChangeTracker.Entries<PurchaseReceiptCostSettlement>()
            .Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToHashSet();
        try
        {
            // This is a read-only preview: the transient settlement rows are
            // detached below and never saved by the planner.
            settlement = await _receiptSettlements.AllocateInvoiceAsync(db, invoice,
                StockCostMethods.MovingAverage, 1, "REPLAY_PLAN", ct);
        }
        finally
        {
            foreach (var entry in db.ChangeTracker.Entries<PurchaseReceiptCostSettlement>()
                         .Where(e => e.State == EntityState.Added && !before.Contains(e.Entity)).ToArray())
                entry.State = EntityState.Detached;
        }
        if (!settlement.Succeeded || settlement.Settlements.Count != 1 || settlement.Adjustments.Count != 1)
            return null;
        var allocation = settlement.Settlements[0];
        var adjustment = settlement.Adjustments[0];
        if (adjustment.TotalAdjustmentAmount == 0m || adjustment.ItemCode != item
            || adjustment.CostMethod != StockCostMethods.MovingAverage
            || adjustment.SourceDocumentLine != line.Line || adjustment.BaseQty != line.StdQty
            || allocation.SettledBaseQty != line.StdQty || allocation.ReceiptValuationFactId <= 0)
            return null;
        var receipt = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.Id == allocation.ReceiptValuationFactId && f.CompanyCode == invoice.CompanyCode
            && f.BranchCode == invoice.BranchCode && f.ItemCode == item
            && f.LedgerEpochId == epoch[0].Id && f.StockPosting!.SealedAtUtc != null)
            .Select(f => new { f.Id, f.StockPostingId, f.BaseUom, f.BaseQty, f.UnitCost, f.CostAmount,
                f.BaseCostAmount, f.Direction, f.ValuationSource, f.ValuationStatus,
                f.OriginalValuationFactId, f.ReversesValuationFactId, f.BusinessDate, f.EffectiveAt,
                f.StockPosting!.PostingSequence, PostingEffectiveAt = f.StockPosting.EffectiveAt,
                PostingBusinessDate = f.StockPosting.BusinessDate, f.StockPosting.SourceModule,
                f.StockPosting.SourceDocumentType, f.StockPosting.SourceDocumentNo,
                f.StockPosting.PostingRole, f.StockPosting.CommandType,
                PostingEpochId = f.StockPosting.LedgerEpochId, f.StockPosting.ReversesPostingId,
                f.CostMethod, f.InventoryHistoryId })
            .SingleOrDefaultAsync(ct);
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.BaseUom)
            || receipt.CostMethod != StockCostMethods.MovingAverage || receipt.InventoryHistoryId is null
            || receipt.SourceModule != "INVENTORY" || receipt.SourceDocumentType != IvTrxTypes.GoodsReceive
            || receipt.PostingRole != "PRIMARY" || receipt.CommandType != "IV_POST_GR"
            || receipt.PostingEpochId != epoch[0].Id || receipt.ReversesPostingId is not null
            || receipt.PostingSequence <= 0 || receipt.Direction != 1 || receipt.BaseQty != line.StdQty
            || receipt.ValuationStatus != StockValuationStatuses.Valued
            || receipt.ValuationSource != StockValuationSources.ReceiptActual
            || receipt.OriginalValuationFactId is not null || receipt.ReversesValuationFactId is not null
            || receipt.BaseCostAmount != receipt.CostAmount || receipt.CostAmount < 0m || receipt.UnitCost < 0m
            || StockLedgerPrecision.Money(receipt.CostAmount) != receipt.CostAmount
            || StockLedgerPrecision.Money(receipt.UnitCost) != receipt.UnitCost
            || StockLedgerPrecision.Money(receipt.BaseQty * receipt.UnitCost) != receipt.CostAmount
            || allocation.ReceiptValuationBaseAmount != receipt.CostAmount
            || allocation.ReceiptValuationUnitCost != receipt.UnitCost
            || receipt.EffectiveAt > invoice.DocDate || receipt.PostingEffectiveAt != receipt.EffectiveAt
            || receipt.PostingBusinessDate != receipt.BusinessDate
            || receipt.BusinessDate.Date > invoice.DocDate.Date || receipt.PostingSequence > watermark)
            return null;
        var receiptHistory = await db.IvTrxHistories.AsNoTracking().Where(h => h.Id == receipt.InventoryHistoryId)
            .Select(h => new { h.Id, h.CompanyCode, h.BranchCode, h.ICode, h.LedgerEpochId, h.StockPostingId,
                h.LedgerVersion, h.ValuationStatus, h.ReversesHistoryId, h.BatchNo,
                h.TrxType, h.BatchStatus, h.EntryRole, h.TrxDtTime, h.ToStdQty, h.ToStdUom,
                h.PoNo, h.PoRelNo, h.PoLineNo, h.BaseUnitPrices }).SingleOrDefaultAsync(ct);
        if (receiptHistory is null || receiptHistory.CompanyCode != invoice.CompanyCode
            || receiptHistory.BranchCode != invoice.BranchCode || receiptHistory.ICode != item
            || receiptHistory.LedgerEpochId != epoch[0].Id || receiptHistory.StockPostingId != receipt.StockPostingId
            || receiptHistory.LedgerVersion != 2 || receiptHistory.ValuationStatus != StockValuationStatuses.Valued
            || receiptHistory.ReversesHistoryId is not null
            || receipt.SourceDocumentNo != receiptHistory.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || receiptHistory.TrxType != IvTrxTypes.GoodsReceive || receiptHistory.BatchStatus != IvBatchStatuses.Posted
            || receiptHistory.EntryRole != "ORIGINAL" || receiptHistory.TrxDtTime != receipt.EffectiveAt
            || receiptHistory.ToStdQty != receipt.BaseQty || receiptHistory.ToStdUom != receipt.BaseUom
            || receiptHistory.PoNo != adjustment.PoNo || receiptHistory.PoRelNo != adjustment.PoRelNo
            || receiptHistory.PoLineNo != adjustment.PoLineNo || receiptHistory.BaseUnitPrices is not decimal commercialUnit
            || commercialUnit < 0m || receipt.UnitCost != StockLedgerPrecision.Money(commercialUnit)
            || allocation.ReceiptCommercialUnitCost != StockLedgerPrecision.Money(commercialUnit)
            || allocation.ReceiptCommercialBaseAmount != StockLedgerPrecision.Money(receipt.BaseQty * commercialUnit))
            return null;

        var key = new StockReplayEventKey(invoice.DocDate, StockReplayEventKey.OrdinaryClass,
            checked(watermark + 1), "PURCHASE_INVOICE", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db,
            invoice.CompanyCode, invoice.BranchCode, item, key, ct);
        if (tail.LaterEvents.Count is 0 or > 100 || tail.LaterEvents.Any(e =>
                e.EventId is not (> 0 and <= int.MaxValue)
                || e.Key.EffectiveAt.Year != invoice.DocDate.Year
                || e.Key.EffectiveAt.Month != invoice.DocDate.Month))
            return null;
        var tailLastDate = tail.LaterEvents.Max(e => e.Key.EffectiveAt.Date);
        if (policies[0].EffectiveTo is DateTime policyEnd && policyEnd.Date <= tailLastDate
            || await db.StockCostPolicyRevisions.AsNoTracking().AnyAsync(p =>
                p.CompanyCode == invoice.CompanyCode && p.BranchCode == invoice.BranchCode
                && p.Status == StockCostPolicyStatuses.Active && p.Id != policies[0].Id
                && p.EffectiveFrom <= tailLastDate
                && (p.EffectiveTo == null || p.EffectiveTo > invoice.DocDate.Date), ct))
            return null;
        var ids = tail.LaterEvents.Select(e => (int)e.EventId).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking().Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.TrxType, h.CompanyCode, h.BranchCode, h.ICode, h.LedgerEpochId })
            .ToArrayAsync(ct);
        if (histories.Length != ids.Length || histories.Any(h =>
                h.CompanyCode != invoice.CompanyCode || h.BranchCode != invoice.BranchCode
                || h.ICode != item || h.LedgerEpochId != epoch[0].Id
                || h.TrxType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive or IvTrxTypes.MiscellaneousIssue)))
            return null;
        var candidate = new StockReplayWacEvent(long.MaxValue, key, 0m, 0m, 0m, 0m);
        var projection = StockReplayWacSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue, candidate, tail.LaterEvents);
        if (projection.FinalQuantity != tail.CurrentQuantity || projection.FinalValue != tail.CurrentValue
            || projection.LaterSteps.Any(step => !tail.LaterEvents.Any(e => e.EventId == step.EventId
                && e.OldSignedQuantity == step.SignedQuantity && e.OldSignedValue == step.SignedValue)))
            return null;
        var balances = await db.IvBalLocs.AsNoTracking().Where(b =>
            b.CompanyCode == invoice.CompanyCode && b.BranchCode == invoice.BranchCode && b.ICode == item)
            .OrderBy(b => b.Id).Select(b => new { b.Id, b.StdQty, b.RowVersion }).ToArrayAsync(ct);
        InvoiceIssueReplay? issueReplay = null;
        if (projection.HistoricalQuantityBeforeCandidate != 0m || projection.HistoricalValueBeforeCandidate != 0m)
        {
            // The first price-changing route is deliberately bounded to one
            // ordinary Inventory issue on one pile. Other descendants retain
            // the existing fail-closed purchase replay guard.
            if (tail.LaterEvents.Count != 1 || histories.Length != 1 || balances.Length != 1
                || histories[0].TrxType != IvTrxTypes.MiscellaneousIssue
                || tail.LaterEvents[0].OldSignedQuantity >= 0m)
                return null;
            var issue = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.Id == ids[0], ct);
            if (issue is null || issue.StockPostingId is not long issueOwnerId
                || issue.FromBalLocId != balances[0].Id || issue.ToBalLocId is not null)
                return null;
            var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
                p.Id == issueOwnerId && p.CompanyCode == invoice.CompanyCode && p.BranchCode == invoice.BranchCode, ct);
            if (owner is null || owner.SealedAtUtc is null || owner.LedgerEpochId != epoch[0].Id
                || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != IvTrxTypes.MiscellaneousIssue
                || owner.CommandType != "IV_POST_MI" || owner.PostingRole != "PRIMARY"
                || owner.ReversesPostingId is not null || owner.EffectiveAt != issue.TrxDtTime
                || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(owner.SourceSnapshotJson))
                    != owner.SourceSnapshotHash)
                return null;
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(db,
                invoice.CompanyCode, invoice.BranchCode, [issue], ct);
            var issueFacts = active[issue.Id];
            if (issueFacts.Count != 1 || issueFacts[0].ProductionMovementId is not null
                || issueFacts[0].WorkOrderId is not null || issueFacts[0].ProductionPostingLinkId is not null)
                return null;
            var split = StockPurchaseVarianceAllocator.Allocate(adjustment.TotalAdjustmentAmount,
                adjustment.BaseQty, Math.Min(projection.HistoricalQuantityBeforeCandidate, adjustment.BaseQty),
                projection.HistoricalValueBeforeCandidate, StockCostMethods.MovingAverage);
            if (split.Inventory == 0m)
                return null;
            var repriced = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
                candidate with { FixedSignedValue = split.Inventory }, tail.LaterEvents);
            var issueStep = repriced.LaterSteps.Single();
            if (issueStep.SignedValue == tail.LaterEvents[0].OldSignedValue)
                return null;
            issueReplay = new(tail.LaterEvents[0], issueStep, issueOwnerId, issueFacts[0].Id,
                split.Inventory, split.Consumed, balances[0].Id, balances[0].StdQty,
                tail.CurrentQuantity, tail.CurrentValue, repriced.FinalQuantity, repriced.FinalValue);
        }
        var fingerprint = StockPostingFingerprint.Create(new { Kind = "PI_FULLY_CONSUMED_WAC" }, new
        {
            invoice.DocNo, invoice.DocDate, invoice.RowVersion, invoice.CostingRevision,
            invoice.Currency, invoice.CurrRate, line.Line, line.ICode, line.Qty, line.UnitPrice,
            line.SellingUom, line.StdUom, line.StdQty, line.NetAmount, line.PoNo, line.PoRelNo,
            line.PoLineNo, Epoch = epoch[0].Id, watermark,
            State = new { state[0].Id, state[0].OnHandBaseQty, state[0].InventoryValue,
                state[0].LastValuationFactId, state[0].LastPostingSequence, state[0].RowVersion },
            policies, order, orderLine, balances, receipt, receiptHistory, adjustment, tail.LaterEvents, projection,
            issueReplay
        }).SourceSnapshotHash;
        return new(fingerprint, epoch[0].Id, watermark, item, line.PoNo.Trim(),
            line.PoRelNo.Value, line.PoLineNo.Value, receipt.Id, receipt.BaseUom, adjustment, issueReplay);
    }
}
