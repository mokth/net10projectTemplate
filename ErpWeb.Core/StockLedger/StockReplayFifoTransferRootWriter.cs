using ErpWeb.Core.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoTransferWriteResult(
    int RootHistoryId, IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Writes one existing-pile, non-lot FIFO transfer under its prepared root.
/// The caller seals, reconciles and commits after dependent effects are ready.
/// </summary>
public static class StockReplayFifoTransferRootWriter
{
    public static async Task<StockReplayFifoTransferWriteResult> InsertAsync(
        StockPostingContext context,
        StockReplayFifoTransferInsertionPlan plan, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        StockReplayFifoTransferInsertionPlanner.ValidatePreparedPlan(plan);
        if (plan.DraftAuthority is null
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || context.Posting.PostingRole != "PRIMARY"
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(plan.Source)
            || StockReplayFifoTransferTailGuard.ChangesPhysicalTail(
                plan.Destination)
            || StockReplayFifoMiRootWriter.ChangedIssues(
                plan.Projection).Count != 0)
            throw Invalid("FIFO transfer insertion has changed financial descendants.");
        var physical = await StockReplayFifoTransferPhysicalWriter.AppendAsync(
            context, plan, batchNo, cancellationToken);
        await StockReplayFifoTransferValuationWriter.AppendAsync(
            context, plan, physical.HistoryId, 1, cancellationToken);
        return new(physical.HistoryId, [physical.HistoryId]);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}

internal static class StockReplayFifoTransferTailGuard
{
    internal static bool ChangesPhysicalTail(StockReplayPhysicalPlan plan) =>
        plan.LaterEvents.Count != plan.Projection.LaterSteps.Count
        || plan.LaterEvents.Zip(plan.Projection.LaterSteps)
            .Any(pair => pair.First.EventId != pair.Second.EventId
                || pair.First.SignedDelta != pair.Second.SignedDelta);

    internal static bool ChangesPhysicalTail(
        StockReplayPhysicalRemovalPlan plan) =>
        plan.LaterEvents.Count != plan.Projection.LaterSteps.Count
        || plan.LaterEvents.Zip(plan.Projection.LaterSteps)
            .Any(pair => pair.First.EventId != pair.Second.EventId
                || pair.First.SignedDelta != pair.Second.SignedDelta);
}

/// <summary>Checks the sealed two-pile transfer before the caller commits.</summary>
public static class StockReplayFifoTransferReconciliationGate
{
    public static async Task VerifySealedWithinRootAsync(
        StockPostingContext context,
        StockReplayFifoTransferRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        StockReplayFifoTransferRemovalPlanner.ValidatePreparedPlan(plan);
        if (context.Posting.SealedAtUtc is null
            || context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode)
            throw Invalid("FIFO transfer removal reconciliation needs its sealed root.");
        var piles = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && (b.Id == plan.Source.BalLocId
                    || b.Id == plan.Destination.BalLocId))
            .ToArrayAsync(cancellationToken);
        if (piles.Length != 2
            || piles.Single(b => b.Id == plan.Source.BalLocId).StdQty
                != plan.Source.Projection.FinalQuantity
            || piles.Single(b => b.Id == plan.Destination.BalLocId).StdQty
                != plan.Destination.Projection.FinalQuantity)
            throw Invalid("FIFO transfer removal piles differ from their projection.");
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            context.Db, plan.CompanyCode, plan.BranchCode,
            plan.ItemCode, cancellationToken);
        var removedId = checked((int)plan.Source.RemovedActiveEvent.EventId);
        if (snapshot.CurrentQuantity != plan.Projection.FinalQuantity
            || snapshot.CurrentValue != plan.Projection.FinalValue
            || snapshot.ActiveConsumptions.Any(c =>
                c.IssueKey.LineIdentity == $"H{removedId:D10}"))
            throw Invalid("FIFO transfer removal retained an active allocation.");
    }

    public static async Task VerifySealedWithinRootAsync(
        StockPostingContext context,
        StockReplayFifoTransferInsertionPlan plan, int historyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        StockReplayFifoTransferInsertionPlanner.ValidatePreparedPlan(plan);
        if (context.Posting.SealedAtUtc is null
            || plan.DraftAuthority is null || historyId <= 0
            || context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode)
            throw Invalid("FIFO transfer reconciliation needs its sealed root.");
        var piles = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && (b.Id == plan.Source.BalLocId
                    || b.Id == plan.Destination.BalLocId))
            .ToArrayAsync(cancellationToken);
        if (piles.Length != 2
            || piles.Single(b => b.Id == plan.Source.BalLocId).StdQty
                != plan.Source.Projection.FinalQuantity
            || piles.Single(b => b.Id == plan.Destination.BalLocId).StdQty
                != plan.Destination.Projection.FinalQuantity)
            throw Invalid("FIFO transfer physical piles differ from their projection.");
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            context.Db, plan.CompanyCode, plan.BranchCode,
            plan.ItemCode, cancellationToken);
        var candidate = plan.Projection.Issues.Single(i => i.IsCandidate);
        var actual = snapshot.ActiveConsumptions
            .Where(c => c.IssueKey.LineIdentity == $"H{historyId:D10}")
            .OrderBy(c => c.SplitOrdinal).ToArray();
        if (snapshot.CurrentQuantity != plan.Projection.FinalQuantity
            || snapshot.CurrentValue != plan.Projection.FinalValue
            || actual.Length != candidate.Allocations.Count
            || actual.Any(c => c.DepletesPool)
            || !actual.Select(c => (c.LayerId, c.Quantity, c.Value))
                .SequenceEqual(candidate.Allocations.Select(a =>
                    (a.LayerId, a.Quantity, a.Value))))
            throw Invalid("FIFO transfer sealed allocations differ from the plan.");
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
