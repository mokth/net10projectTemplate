using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Appends the original FIFO valuation generation for one replayed Inventory issue.
/// Final layer positions and item cost state are applied by the root caller.
/// </summary>
public static class StockReplayFifoIssueValuationWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit,
        int historyId, int postingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        var bound = await StockReplayFifoIssueCandidateBinder.BindAsync(
            context, plan, permit, historyId, cancellationToken);
        if (postingLineNo <= 0 || permit.Operation != "INSERT"
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.PlanHash != plan.PlanHash
            || permit.SnapshotHash != plan.Snapshot.SnapshotHash
            || permit.ItemCode != plan.ItemCode
            || bound.RootPostingId != context.Posting.Id
            || bound.PreparedPlanHash != plan.PlanHash
            || bound.HistoryId <= 0
            || bound.Key.LineIdentity != $"H{bound.HistoryId:D10}"
            || bound.Key.EffectiveAt != plan.PhysicalPlan.Candidate.Key.EffectiveAt
            || bound.Key.OriginalPostingSequence
                != plan.PhysicalPlan.Candidate.Key.OriginalPostingSequence
            || bound.FinancialProjection.FinalQuantity
                != plan.Projection.FinalQuantity
            || bound.FinancialProjection.FinalValue != plan.Projection.FinalValue)
            throw Invalid("FIFO candidate valuation needs its bound original MI event.");
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, plan, cancellationToken);
        var db = context.Db;
        if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == context.CompanyCode
                && f.BranchCode == context.BranchCode
                && (f.InventoryHistoryId == bound.HistoryId
                    || f.StockPostingId == context.Posting.Id
                    && f.PostingLineNo == postingLineNo),
                cancellationToken)
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                e.State == EntityState.Added
                && (e.Entity.InventoryHistoryId == bound.HistoryId
                    || e.Entity.StockPostingId == context.Posting.Id
                    && e.Entity.PostingLineNo == postingLineNo)))
            throw Invalid("The FIFO candidate already has valuation or its root line is occupied.");
        var history = await db.IvTrxHistories.SingleOrDefaultAsync(h =>
            h.Id == bound.HistoryId
            && h.CompanyCode == context.CompanyCode
            && h.BranchCode == context.BranchCode,
            cancellationToken);
        if (history is null || history.StockPostingId != context.Posting.Id
            || history.LedgerEpochId != context.Epoch.Id
            || history.LedgerVersion != 2 || history.EntryRole != "ORIGINAL"
            || history.TrxType != context.Posting.SourceDocumentType
            || !StockReplayFifoInventoryIssueKinds.IsSupported(history.TrxType)
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.ICode != plan.ItemCode
            || history.FromBalLocId != plan.PhysicalPlan.BalLocId
            || history.FrStdQty != -plan.PhysicalPlan.Candidate.SignedDelta
            || history.TrxDtTime != bound.Key.EffectiveAt
            || string.IsNullOrWhiteSpace(history.FrStdUom)
            || history.ValuationStatus is not null)
            throw Invalid("The bound FIFO issue history is not an unvalued original MI.");
        var issue = bound.FinancialProjection.Issues.SingleOrDefault(i =>
            i.IsCandidate && i.Key == bound.Key && i.DepletesPool);
        if (issue is null || issue.Allocations.Count == 0
            || issue.Quantity != history.FrStdQty
            || issue.Quantity != issue.Allocations.Sum(a => a.Quantity)
            || issue.Value != issue.Allocations.Sum(a => a.Value)
            || issue.Allocations.Any(a => a.LayerId <= 0
                || a.Quantity <= 0m || a.Value < 0m))
            throw Invalid("The bound FIFO candidate has no complete layer allocation.");
        var layerIds = issue.Allocations.Select(a => a.LayerId).Distinct().ToArray();
        var knownLayers = await db.StockFifoLayers.AsNoTracking()
            .Where(l => l.CompanyCode == context.CompanyCode
                && l.BranchCode == context.BranchCode
                && l.ItemCode == plan.ItemCode
                && layerIds.Contains(l.Id))
            .Select(l => l.Id).ToArrayAsync(cancellationToken);
        if (knownLayers.Length != layerIds.Length)
            throw Invalid("The projected FIFO issue references a missing layer.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw Invalid("The FIFO issue company has no base currency.");
        var now = DateTime.UtcNow;
        var sourceLine = history.TrxLineNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var facts = issue.Allocations.Select((allocation, ordinal) =>
            new StockValuationFact
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = postingLineNo,
                SplitOrdinal = ordinal,
                SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{sourceLine}:{postingLineNo}:{ordinal}",
                SourceDocumentType = context.Posting.SourceDocumentType,
                SourceDocumentId = context.Posting.SourceDocumentId,
                SourceDocumentNo = context.Posting.SourceDocumentNo,
                SourceDocumentLine = sourceLine,
                EffectiveAt = bound.Key.EffectiveAt,
                BusinessDate = context.Posting.BusinessDate,
                PeriodKey = context.Posting.PeriodKey,
                ItemCode = plan.ItemCode,
                WarehouseCode = BlankToNull(history.FrWarehouse),
                LocationCode = BlankToNull(history.FrLocation),
                LotId = history.FromLotId,
                LotNo = BlankToNull(history.FrLotNo),
                ItemStatus = BlankToNull(history.IStatus),
                BaseUom = history.FrStdUom!,
                MovementCode = StockReplayFifoInventoryIssueKinds.MovementCode(
                    history.TrxType),
                Direction = -1,
                BaseQty = allocation.Quantity,
                CostMethod = StockCostMethods.Fifo,
                UnitCost = allocation.UnitCost,
                CostAmount = allocation.Value,
                BaseCurrency = baseCurrency,
                BaseCostAmount = allocation.Value,
                ValuationSource = StockValuationSources.Fifo,
                ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = 1,
                InventoryHistoryId = history.Id,
                FromBalLocId = history.FromBalLocId,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            }).ToArray();
        db.StockValuationFacts.AddRange(facts);
        for (var i = 0; i < facts.Length; i++)
        {
            var allocation = issue.Allocations[i];
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = facts[i],
                FifoLayerId = allocation.LayerId,
                ConsumedQty = allocation.Quantity,
                ConsumedValue = allocation.Value,
                SplitOrdinal = i,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            });
        }
        history.ValuationStatus = StockValuationStatuses.Valued;
        return facts;
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
