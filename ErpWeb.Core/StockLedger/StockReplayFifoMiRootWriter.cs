using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoMiWriteResult(
    int RootHistoryId, IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Writes bounded, single-line non-lot MI and Scrap FIFO replay inside a prepared
/// root. The caller still owns replay completion, sealed reconciliation and
/// transaction commit, including any dependent-module corrections.
/// </summary>
public static class StockReplayFifoMiRootWriter
{
    public static async Task<StockReplayFifoMiWriteResult> InsertAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.Candidate.SignedDelta >= 0m
            || permit.Operation != "INSERT" || permit.PlanHash != plan.PlanHash
            || permit.RootPostingId != context.Posting.Id
            || context.Posting.SourceModule != "INVENTORY"
            || !StockReplayFifoInventoryIssueKinds.IsSupported(
                context.Posting.SourceDocumentType)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "PRIMARY")
            throw Invalid("FIFO MI insertion needs its prepared issue permit.");
        var changed = ChangedIssues(plan.Projection);
        var required = new List<int>(changed.Count + 1);
        var line = 1;
        foreach (var issue in changed)
        {
            var replacements = issue.DepletesPool
                ? await StockReplayFifoIssueRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key, line, line + 1,
                    cancellationToken)
                : await StockReplayFifoTransferRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key, line, line + 1,
                    cancellationToken: cancellationToken);
            if (replacements.Count == 0)
                throw Invalid("A changed FIFO issue produced no replacement generation.");
            required.Add(HistoryId(issue.Key));
            line += 2;
        }
        var bound = await StockReplayFifoIssuePhysicalWriter.AppendAsync(
            context, plan, permit, batchNo, cancellationToken);
        await StockReplayFifoIssueValuationWriter.AppendAsync(
            context, plan, permit, bound.HistoryId, line, cancellationToken);
        required.Add(bound.HistoryId);
        await StockReplayFifoStateWriter.ApplyIssueInsertionAsync(
            context, plan, permit, cancellationToken);
        return new(bound.HistoryId, required);
    }

    public static async Task<StockReplayFifoMiWriteResult> RemoveAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.RemovedActiveEvent.SignedDelta >= 0m
            || permit.Operation != "REMOVE" || permit.PlanHash != plan.PlanHash
            || permit.RootPostingId != context.Posting.Id
            || context.Posting.SourceModule != "INVENTORY"
            || !StockReplayFifoInventoryIssueKinds.IsSupported(
                context.Posting.SourceDocumentType)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "REVERSAL")
            throw Invalid("FIFO MI removal needs its prepared issue permit.");
        var changed = ChangedIssues(plan.Projection);
        var required = new List<int>(changed.Count);
        var line = 2;
        foreach (var issue in changed)
        {
            var replacements = issue.DepletesPool
                ? await StockReplayFifoIssueRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key, line, line + 1,
                    cancellationToken)
                : await StockReplayFifoTransferRepricingWriter.AppendAsync(
                    context, plan, permit, issue.Key, line, line + 1,
                    cancellationToken);
            if (replacements.Count == 0)
                throw Invalid("A changed FIFO issue produced no replacement generation.");
            required.Add(HistoryId(issue.Key));
            line += 2;
        }
        var reversalHistoryId = await StockReplayFifoIssueRemovalPhysicalWriter
            .AppendAsync(context, plan, permit, batchNo, cancellationToken);
        await StockReplayFifoIssueRemovalValuationWriter.AppendAsync(
            context, plan, permit, reversalHistoryId, 1, cancellationToken);
        await StockReplayFifoStateWriter.ApplyIssueRemovalAsync(
            context, plan, permit, cancellationToken);
        return new(reversalHistoryId, required);
    }

    internal static IReadOnlyList<StockReplayFifoProjectedIssue> ChangedIssues(
        StockReplayFifoTailProjection projection)
    {
        var changed = new List<StockReplayFifoProjectedIssue>();
        foreach (var issue in projection.Issues.Where(i => !i.IsCandidate)
            .OrderBy(i => i.Key))
        {
            var oldShape = issue.PreviousConsumptions
                .Select(c => (c.LayerId, c.Quantity, c.Value))
                .OrderBy(x => x.LayerId).ThenBy(x => x.Quantity)
                .ThenBy(x => x.Value).ToArray();
            var newShape = issue.Allocations
                .Select(a => (a.LayerId, a.Quantity, a.Value))
                .OrderBy(x => x.LayerId).ThenBy(x => x.Quantity)
                .ThenBy(x => x.Value).ToArray();
            if (oldShape.SequenceEqual(newShape))
                continue;
            HistoryId(issue.Key);
            changed.Add(issue);
        }
        return changed;
    }

    internal static int HistoryId(StockReplayEventKey key)
    {
        if (key.LineIdentity.Length != 11
            || key.LineIdentity[0] != 'H'
            || !int.TryParse(key.LineIdentity.AsSpan(1), out var id)
            || id <= 0 || key.LineIdentity != $"H{id:D10}")
            throw Invalid("A changed FIFO issue has no original Inventory history key.");
        return id;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
