using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Appends the exact inverse of the removed Inventory issue generation and its FIFO
/// consumptions. Descendant repricing and final state remain root work.
/// </summary>
public static class StockReplayFifoIssueRemovalValuationWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit, int reversalHistoryId,
        int postingLineNo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        var removed = plan.PhysicalPlan.RemovedActiveEvent;
        if (postingLineNo <= 0 || reversalHistoryId <= 0
            || removed.SignedDelta >= 0m
            || permit.Operation != "REMOVE"
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != plan.ItemCode
            || permit.PlanHash != plan.PlanHash
            || permit.SnapshotHash != plan.Snapshot.SnapshotHash
            || permit.RemovedHistoryId != removed.EventId
            || context.Posting.PostingRole != "REVERSAL")
            throw Invalid("FIFO issue removal valuation needs its prepared root permit.");
        var db = context.Db;
        var originalId = permit.RemovedHistoryId!.Value;
        var original = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == originalId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode, cancellationToken);
        var reversal = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == reversalHistoryId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode, cancellationToken);
        if (original is null || reversal is null
            || original.ICode != plan.ItemCode
            || original.TrxType != context.Posting.SourceDocumentType
            || !StockReplayFifoInventoryIssueKinds.IsSupported(original.TrxType)
            || original.EntryRole != "ORIGINAL"
            || original.ValuationStatus != StockValuationStatuses.Valued
            || original.FrStdQty != -removed.SignedDelta
            || original.TrxDtTime != removed.Key.EffectiveAt
            || reversal.EntryRole != "REVERSAL"
            || reversal.ReversesHistoryId != originalId
            || reversal.StockPostingId != context.Posting.Id
            || reversal.ToBalLocId != plan.PhysicalPlan.BalLocId
            || reversal.ToStdQty != -removed.SignedDelta)
            throw Invalid("The removed MI has no exact root reversal history.");
        var previous = plan.Snapshot.ActiveConsumptions
            .Where(c => c.IssueKey == removed.Key).ToArray();
        if (previous.Length == 0
            || previous.Any(c => !c.DepletesPool)
            || StockLedgerPrecision.Quantity(previous.Sum(c => c.Quantity))
                != -removed.SignedDelta
            || previous.Select(c => c.ConsumptionId).Distinct().Count()
                != previous.Length)
            throw Invalid("The removed MI has no complete active FIFO split.");
        var active = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, [original],
            cancellationToken))[originalId].OrderBy(f => f.SplitOrdinal).ToArray();
        var byFact = previous.ToDictionary(c => c.IssueFactId);
        if (active.Length != previous.Length
            || active.Any(f => !byFact.TryGetValue(f.Id, out var c)
                || f.ItemCode != plan.ItemCode
                || f.InventoryHistoryId != originalId
                || f.MovementCode !=
                    StockReplayFifoInventoryIssueKinds.MovementCode(
                        original.TrxType)
                || f.Direction != -1
                || f.CostMethod != StockCostMethods.Fifo
                || f.ValuationSource != StockValuationSources.Fifo
                || f.SplitOrdinal != c.SplitOrdinal
                || f.BaseQty != c.Quantity
                || f.CostAmount != c.Value
                || f.EffectiveAt != removed.Key.EffectiveAt))
            throw Invalid("The removed MI active FIFO facts changed after planning.");
        var ids = active.Select(f => f.Id).ToArray();
        var persisted = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => ids.Contains(c.IssueValuationFactId))
            .ToArrayAsync(cancellationToken);
        if (persisted.Length != previous.Length
            || persisted.Any(c => !previous.Any(p =>
                p.ConsumptionId == c.Id
                && p.IssueFactId == c.IssueValuationFactId
                && p.LayerId == c.FifoLayerId
                && p.Quantity == c.ConsumedQty
                && p.Value == c.ConsumedValue
                && p.SplitOrdinal == c.SplitOrdinal)))
            throw Invalid("The removed MI active consumption changed after planning.");
        var reversals = await StockReplayValuationReversalWriter.AppendAsync(
            context, originalId, active.Select((fact, ordinal) =>
                new StockReplayValuationReversalLine(fact.Id,
                    postingLineNo, ordinal, reversalHistoryId)).ToArray(),
            cancellationToken);
        var reverseById = reversals.ToDictionary(
            f => f.ReversesValuationFactId!.Value);
        var now = DateTime.UtcNow;
        foreach (var old in previous)
        {
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                IssueValuationFact = reverseById[old.IssueFactId],
                FifoLayerId = old.LayerId,
                ConsumedQty = old.Quantity,
                ConsumedValue = old.Value,
                SplitOrdinal = old.SplitOrdinal,
                ReversesConsumptionId = old.ConsumptionId,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            });
        }
        var trackedReversal = await db.IvTrxHistories.SingleAsync(
            h => h.Id == reversalHistoryId, cancellationToken);
        trackedReversal.ValuationStatus = StockValuationStatuses.Reversed;
        return reversals;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
