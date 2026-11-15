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

    private sealed record InvoiceIssueChange(
        StockReplayWacEvent Issue, StockReplayWacStep IssueStep, long IssueOwnerId, long PriorFactId);

    private sealed record InvoiceIssueTailProof(IReadOnlyList<InvoiceIssueChange> Changes, string EvidenceHash,
        IReadOnlyList<InvoiceGoodsReceiptPurchaseProof> GoodsReceipts, IReadOnlyList<InvoiceDirectSaleProof> Sales,
        IReadOnlyList<InvoiceDeliverySaleProof> Deliveries);

    private sealed record InvoiceIssueReplay(
        IReadOnlyList<InvoiceIssueChange> Changes, string TailEvidenceHash,
        IReadOnlyList<InvoiceGoodsReceiptPurchaseProof> GoodsReceipts,
        IReadOnlyList<InvoiceDirectSaleProof> Sales,
        IReadOnlyList<InvoiceDeliverySaleProof> Deliveries,
        decimal InventoryVariance, decimal ConsumedVariance, int BalanceId, decimal BalanceQty,
        decimal CurrentQuantity, decimal CurrentValue, decimal FinalQuantity, decimal FinalValue);

    // Prove the historical variance and complete active WAC tail before the
    // branch lock and again inside the same locked transaction.
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
        if (tail.LaterEvents.Count is 0 or > StockReplayInvoiceIssueAudit.MaxIssueCount || tail.LaterEvents.Any(e =>
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
                || h.TrxType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive
                    or IvTrxTypes.MiscellaneousIssue or IvTrxTypes.SalesOut)))
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
            // This route accepts ordinary Inventory issues and fixed-cost
            // receipts, and at most one proven sale, on one pile.
            if (tail.LaterEvents.Count is 0 or > StockReplayInvoiceIssueAudit.MaxIssueCount || balances.Length != 1)
                return null;
            var split = StockPurchaseVarianceAllocator.Allocate(adjustment.TotalAdjustmentAmount,
                adjustment.BaseQty, Math.Min(projection.HistoricalQuantityBeforeCandidate, adjustment.BaseQty),
                projection.HistoricalValueBeforeCandidate, StockCostMethods.MovingAverage);
            if (split.Inventory == 0m)
                return null;
            var repriced = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity, tail.CurrentValue,
                candidate with { FixedSignedValue = split.Inventory }, tail.LaterEvents);
            var proof = await PlanInvoiceIssueChangesAsync(db, invoice.CompanyCode, invoice.BranchCode,
                item, epoch[0].Id, balances[0].Id, invoice.DocNo, line.PoNo, line.PoRelNo.Value,
                line.PoLineNo.Value, PoOrderCalc.RoundQty(line.Qty), invoicePosted: false,
                tail.LaterEvents, repriced.LaterSteps, ct);
            if (proof is null)
                return null;
            issueReplay = new(proof.Changes, proof.EvidenceHash, proof.GoodsReceipts, proof.Sales, proof.Deliveries,
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

    private static async Task<InvoiceIssueTailProof?> PlanInvoiceIssueChangesAsync(
        AppDbContext db, string company, string branch, string item, long epochId, int balanceId,
        string invoiceDocNo, string invoicePoNo, short invoicePoRelNo, short invoicePoLineNo,
        decimal invoiceQty, bool invoicePosted,
        IReadOnlyList<StockReplayWacEvent> events, IReadOnlyList<StockReplayWacStep> steps, CancellationToken ct)
    {
        if (events.Count is 0 or > StockReplayInvoiceIssueAudit.MaxIssueCount || steps.Count != events.Count
            || events.Any(e => e.EventId is not (> 0 and <= int.MaxValue) || e.OldSignedQuantity == 0m)
            || events.Select(e => e.EventId).Distinct().Count() != events.Count
            || steps.Select(s => s.EventId).Distinct().Count() != steps.Count)
            return null;
        var ids = events.Select(e => (int)e.EventId).ToArray();
        var issues = await db.IvTrxHistories.AsNoTracking().Where(h => ids.Contains(h.Id)
            && h.CompanyCode == company && h.BranchCode == branch).ToArrayAsync(ct);
        if (issues.Length != ids.Length || issues.Any(h => h.ICode != item || h.LedgerEpochId != epochId
                || h.LedgerVersion != 2 || h.TrxType is not (IvTrxTypes.MiscellaneousIssue or IvTrxTypes.MiscellaneousReceipt
                    or IvTrxTypes.GoodsReceive or IvTrxTypes.SalesOut)
                || h.EntryRole != "ORIGINAL" || h.BatchStatus != IvBatchStatuses.Posted
                || h.ValuationStatus != StockValuationStatuses.Valued || h.StockPostingId is null))
            return null;
        var ownerIds = issues.Select(h => h.StockPostingId!.Value).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking().Where(p => ownerIds.Contains(p.Id)
            && p.CompanyCode == company && p.BranchCode == branch).ToArrayAsync(ct);
        if (owners.Length != ownerIds.Length)
            return null;
        var issuesById = issues.ToDictionary(h => h.Id);
        var ownersById = owners.ToDictionary(p => p.Id);
        var stepsById = steps.ToDictionary(s => s.EventId);
        var salesEvents = events.Where(e => issuesById[(int)e.EventId].TrxType == IvTrxTypes.SalesOut).ToArray();
        // Removing a receipt before a sale that was corrected by this invoice
        // needs a separate reversed-sale proof. Keep that order blocked until
        // both documents can be rolled back safely.
        if (salesEvents.Length == 1 && events.Any(e =>
                issuesById[(int)e.EventId].TrxType == IvTrxTypes.MiscellaneousReceipt
                && e.Key.CompareTo(salesEvents[0].Key) < 0))
            return null;
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(db, company, branch, issues, ct);
        var changes = new List<InvoiceIssueChange>(events.Count);
        var receipts = new List<IvInventoryPostingService.WacFixedReceiptProof>();
        foreach (var movement in events)
        {
            var issue = issuesById[(int)movement.EventId];
            var owner = ownersById[issue.StockPostingId!.Value];
            var step = stepsById.GetValueOrDefault(movement.EventId);
            var facts = active[issue.Id];
            if (step is null || step.SignedQuantity != movement.OldSignedQuantity
                || owner.SealedAtUtc is null || owner.LedgerEpochId != epochId
                || owner.SourceModule != "INVENTORY" || owner.SourceDocumentType != issue.TrxType
                || owner.CommandType != $"IV_POST_{issue.TrxType}" || owner.PostingRole != "PRIMARY"
                || owner.ReversesPostingId is not null || owner.EffectiveAt != issue.TrxDtTime
                || StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(owner.SourceSnapshotJson))
                    != owner.SourceSnapshotHash || facts.Count != 1 || facts[0].ProductionMovementId is not null
                || facts[0].WorkOrderId is not null || facts[0].WorkOrderOperationId is not null
                || facts[0].ProductionPostingLinkId is not null)
                return null;
            if (movement.OldSignedQuantity > 0m)
            {
                if (issue.TrxType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                    || step.SignedValue != movement.OldSignedValue)
                    return null;
                if (issue.TrxType == IvTrxTypes.MiscellaneousReceipt
                    && (issue.BaseUnitPrices is not null || issue.UnitPrice is not decimal receiptUnit
                        || receiptUnit < 0m || StockLedgerPrecision.Money(receiptUnit) != facts[0].UnitCost))
                    return null;
                var receipt = IvInventoryPostingService.ValidateOriginalWacFixedReceipt(
                    company, branch, item, epochId, balanceId, issue, movement, owner, facts[0]);
                if (receipt is null)
                    return null;
                receipts.Add(receipt);
            }
            else if (issue.TrxType is not (IvTrxTypes.MiscellaneousIssue or IvTrxTypes.SalesOut)
                || issue.FromBalLocId != balanceId || issue.ToBalLocId is not null)
                return null;
            else if (step.SignedValue != movement.OldSignedValue)
                changes.Add(new(movement, step, owner.Id, facts[0].Id));
        }
        if (changes.Count == 0)
            return null;
        var purchaseReceipts = await ProveInvoiceLaterGoodsReceiptsAsync(db, company, branch, item,
            invoiceDocNo, invoicePoNo, invoicePoRelNo, invoicePoLineNo, invoiceQty, invoicePosted,
            issues, ownersById, active, ct);
        if (purchaseReceipts is null)
            return null;
        var sales = await ProveInvoiceDirectSalesAsync(db, company, branch, issues, ct);
        if (sales is null)
            return null;
        var deliveries = await ProveInvoiceDeliverySalesAsync(db, company, branch, issues, ct);
        if (deliveries is null || sales.Count + deliveries.Count != issues.Count(h => h.TrxType == IvTrxTypes.SalesOut))
            return null;
        var evidenceHash = StockPostingFingerprint.Create(new { Kind = "PI_WAC_ISSUE_TAIL" }, new
        {
            Owners = owners.OrderBy(p => p.Id).Select(p => new
            {
                p.Id, p.PostingSequence, p.RequestFingerprint, p.SourceSnapshotHash, p.SealedAtUtc
            }).ToArray(),
            Histories = issues.OrderBy(h => h.Id).Select(h => new
            {
                h.Id, h.StockPostingId, h.BatchNo, h.TrxType, h.TrxDtTime, h.FromBalLocId, h.ToBalLocId,
                h.FrStdQty, h.ToStdQty, FactId = active[h.Id][0].Id, FactOwnerId = active[h.Id][0].StockPostingId
            }).ToArray(),
            Receipts = receipts.OrderBy(r => r.HistoryId).ToArray(),
            PurchaseReceipts = purchaseReceipts, Sales = sales, Deliveries = deliveries
        }).SourceSnapshotHash;
        return new(changes, evidenceHash, purchaseReceipts, sales, deliveries);
    }
}
