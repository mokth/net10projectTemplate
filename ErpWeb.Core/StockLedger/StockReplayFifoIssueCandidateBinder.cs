using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Binds the provisional, pre-lock FIFO issue to the single Inventory
/// history actually inserted by its root. Call after the physical post has
/// been saved and before appending any descendant correction histories.
/// </summary>
public sealed record StockReplayFifoBoundIssueCandidate(
    int HistoryId, StockReplayEventKey Key,
    StockReplayPhysicalResult PhysicalProjection,
    StockReplayFifoTailProjection FinancialProjection,
    long RootPostingId, string PreparedPlanHash);

public static class StockReplayFifoIssueCandidateBinder
{
    public static async Task<StockReplayFifoBoundIssueCandidate> BindAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, int historyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        var physical = plan.PhysicalPlan;
        var candidate = physical.Candidate;
        if (historyId <= 0 || candidate.EventId <= 0
            || candidate.Key.AttachedSuborder != 0
            || permit.Operation != "INSERT"
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != plan.ItemCode
            || permit.PlanHash != plan.PlanHash
            || permit.SnapshotHash != plan.Snapshot.SnapshotHash
            || permit.ChangedPileId != physical.BalLocId
            || permit.CurrentQuantity != plan.Snapshot.CurrentQuantity
            || permit.CurrentValue != plan.Snapshot.CurrentValue
            || permit.FinalQuantity != plan.Projection.FinalQuantity
            || permit.FinalValue != plan.Projection.FinalValue
            || permit.ChangedPileFinalQuantity != physical.Projection.FinalQuantity
            || context.Posting.EffectiveAt != candidate.Key.EffectiveAt
            || context.Posting.PostingSequence != candidate.Key.OriginalPostingSequence
            || context.Posting.SourceModule != "INVENTORY")
            throw Invalid("FIFO candidate binding needs its validated Inventory root permit.");

        var db = context.Db;
        var rootHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && h.StockPostingId == context.Posting.Id)
            .ToArrayAsync(cancellationToken);
        if (rootHistories.Length != 1 || rootHistories[0].Id != historyId)
            throw Invalid("FIFO candidate binding requires exactly one saved root history.");
        var history = rootHistories[0];
        if (candidate.SignedDelta >= 0m)
            throw Invalid("FIFO receipt binding requires verified receipt cost authority.");
        if (history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.DocumentRevision != context.Posting.DocumentRevision
            || history.PostingLineNo != 1
            || history.EntryRole != "ORIGINAL"
            || history.ReversesHistoryId is not null
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.ICode != plan.ItemCode
            || history.TrxDtTime != candidate.Key.EffectiveAt
            || !StockReplayFifoInventoryIssueKinds.IsSupportedFifoRoot(history.TrxType)
            || history.FromBalLocId != physical.BalLocId
            || history.FrStdQty != -candidate.SignedDelta
            || history.ToBalLocId is not null
            || history.ToStdQty is not null)
            throw Invalid("Saved Inventory candidate differs from its prepared physical leg.");

        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.BatchNo == history.BatchNo)
            .ToArrayAsync(cancellationToken);
        if (batches.Length != 1 || batches[0].BatchStatus != IvBatchStatuses.Posted
            || batches[0].TrxType != history.TrxType
            || batches[0].TrxDtTime != candidate.Key.EffectiveAt
            || context.Posting.SourceDocumentType != history.TrxType
            || context.Posting.SourceDocumentId != batches[0].Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.SourceDocumentNo != history.BatchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid("FIFO candidate has no matching posted Inventory batch.");
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batches[0].Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1 || details[0].TrxLineNo != history.TrxLineNo
            || details[0].TrxType != history.TrxType
            || details[0].ICode != plan.ItemCode
            || details[0].DocumentRevision != context.Posting.DocumentRevision
            || details[0].CompanyCode != context.CompanyCode
            || details[0].BranchCode != context.BranchCode
            || details[0].BatchNo != history.BatchNo
            || details[0].FromBalLocId != physical.BalLocId
            || details[0].FrStdQty != -candidate.SignedDelta
            || details[0].FromLotId != history.FromLotId
            || !string.Equals(details[0].FrLotNo ?? string.Empty,
                history.FrLotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || history.TrxType == IvTrxTypes.VendorReturn
                && (details[0].PoNo != history.PoNo
                    || details[0].PoRelNo != history.PoRelNo
                    || details[0].PoLineNo != history.PoLineNo
                    || details[0].FrPurQty != history.FrPurQty
                    || details[0].FrPurUom != history.FrPurUom))
            throw Invalid("FIFO candidate history differs from its staged Inventory line.");

        var pileQuantity = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.Id == physical.BalLocId)
            .Select(b => (decimal?)b.StdQty)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileQuantity != physical.Projection.FinalQuantity)
            throw Invalid("FIFO candidate pile does not hold the prepared final quantity.");

        var key = candidate.Key with { LineIdentity = $"H{historyId:D10}" };
        var boundEvent = candidate with { EventId = historyId, Key = key };
        StockReplayPhysicalResult boundPhysical;
        StockReplayFifoTailProjection boundFinancial;
        try
        {
            boundPhysical = StockReplayPhysicalSimulator.Simulate(
                physical.CurrentQuantity, boundEvent, physical.LaterEvents);
            boundFinancial = StockReplayFifoTailProjector.InsertIssue(
                plan.Snapshot, key, -candidate.SignedDelta);
        }
        catch (Exception ex) when (ex is ArgumentException or StockLedgerException)
        {
            throw Invalid("The saved FIFO candidate changes the prepared event order.");
        }
        if (boundPhysical.HistoricalQuantityBeforeCandidate
                != physical.Projection.HistoricalQuantityBeforeCandidate
            || boundPhysical.Candidate with { EventId = candidate.EventId }
                != physical.Projection.Candidate
            || !boundPhysical.LaterSteps.SequenceEqual(physical.Projection.LaterSteps)
            || boundPhysical.FinalQuantity != physical.Projection.FinalQuantity
            || boundFinancial.FinalQuantity != plan.Projection.FinalQuantity
            || boundFinancial.FinalValue != plan.Projection.FinalValue
            || !SameFinancialEffects(plan.Projection, boundFinancial))
            throw Invalid("The saved FIFO candidate changes the prepared replay projection.");
        return new(historyId, key, boundPhysical, boundFinancial,
            context.Posting.Id, plan.PlanHash);
    }

    private static bool SameFinancialEffects(
        StockReplayFifoTailProjection expected,
        StockReplayFifoTailProjection actual) =>
        expected.Issues.Count == actual.Issues.Count
        && expected.Issues.Zip(actual.Issues).All(pair =>
            pair.First.IsCandidate == pair.Second.IsCandidate
            && pair.First.DepletesPool == pair.Second.DepletesPool
            && pair.First.Quantity == pair.Second.Quantity
            && pair.First.Value == pair.Second.Value
            && (pair.First.IsCandidate || pair.First.Key == pair.Second.Key)
            && pair.First.Allocations.Select(a => (a.LayerId, a.Quantity, a.Value))
                .SequenceEqual(pair.Second.Allocations.Select(a =>
                    (a.LayerId, a.Quantity, a.Value))))
        && expected.FinalLayers.Count == actual.FinalLayers.Count
        && expected.FinalLayers.Zip(actual.FinalLayers).All(pair =>
            pair.First.LayerId == pair.Second.LayerId
            && pair.First.RemainingQuantity == pair.Second.RemainingQuantity
            && pair.First.RemainingValue == pair.Second.RemainingValue);

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
