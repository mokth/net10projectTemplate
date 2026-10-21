using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayCountSheetRemovalRootPlan(
    StockPostingCommand Command,
    string CostMethod,
    StockReplayZeroCountRemovalRootPlan? Zero,
    StockReplayWacCountSheetRemovalPlan? Wac,
    StockReplayStandardCountSheetRemovalPlan? Standard,
    StockReplayFifoCountSheetRemovalPlan? Fifo);

public sealed record StockReplayCountSheetRemovalRootResult(
    IReadOnlyList<int> ReversalHistoryIds,
    IReadOnlyList<long> RemovalGenerationIds,
    IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Selects the bounded zero, WAC, Standard, or FIFO count rollback path. The caller
/// still owns the branch transaction, replay completion, and commit.
/// </summary>
public static class StockReplayCountSheetRemovalRootWriter
{
    public static async Task<StockReplayCountSheetRemovalRootPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode, int countId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var sheet = await StockReplayCountSheetRemovalPlanner.BuildAsync(
            db, companyCode, branchCode, countId, cancellationToken);
        var method = await new StockCostMethodResolver().ResolveAsync(db,
            companyCode, branchCode, sheet.EffectiveAt, cancellationToken);
        if (sheet.Lines.All(l =>
                l.Physical.RemovedActiveEvent.SignedDelta == 0m))
        {
            var zero = await StockReplayZeroCountRemovalRootWriter.BuildAsync(
                db, companyCode, branchCode, countId, cancellationToken);
            return new(zero.Command, method, zero, null, null, null);
        }
        if (method == StockCostMethods.MovingAverage)
        {
            var wac = await StockReplayWacCountSheetRemovalPlanner.BuildAsync(
                db, companyCode, branchCode, countId, cancellationToken);
            return new(wac.Command, method, null, wac, null, null);
        }
        if (method == StockCostMethods.Standard)
        {
            var standard = await StockReplayStandardCountSheetRemovalPlanner
                .BuildAsync(db, companyCode, branchCode, countId,
                    cancellationToken);
            return new(standard.Command, method, null, null, standard, null);
        }
        if (method == StockCostMethods.Fifo)
        {
            var fifo = await StockReplayFifoCountSheetRemovalPlanner.BuildAsync(
                db, companyCode, branchCode, countId, cancellationToken);
            if (fifo.Items.Count(i => i.Projection is not null) > 1
                && sheet.Lines.Count(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta != 0m) > 1)
            {
                if (fifo.Items.Any(i => i.Projection?.LaterIssues.Any(
                        StockReplayFifoCountSheetRemovalPlanner
                            .NeedsIssueRepricing) == true))
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireRepricedMultiItemAdjustments(fifo);
                else if (sheet.Lines.Any(l =>
                        l.Physical.RemovedActiveEvent.SignedDelta < 0m))
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireIndependentMultiItemSignedAdjustments(fifo);
                else
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireIndependentMultiItemPositiveAdjustments(fifo);
            }
            else if (sheet.Lines.Count(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta != 0m) == 1)
            {
                if (fifo.Items.Any(i => i.Projection?.LaterIssues.Any(
                        StockReplayFifoCountSheetRemovalPlanner
                            .NeedsIssueRepricing) == true))
                {
                    if (sheet.Lines.Any(l =>
                            l.Physical.RemovedActiveEvent.SignedDelta > 0m))
                        StockReplayFifoCountSheetRemovalPlanner
                            .RequireRepricedPositiveAdjustment(fifo);
                    else
                        StockReplayFifoCountSheetRemovalPlanner
                            .RequireRepricedNegativeAdjustment(fifo);
                }
                else
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireIsolatedAdjustment(fifo);
            }
            else if (sheet.Lines.All(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta >= 0m))
            {
                if (fifo.Items.Any(i => i.Projection?.LaterIssues.Any(
                        StockReplayFifoCountSheetRemovalPlanner
                            .NeedsIssueRepricing) == true))
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireRepricedPositiveAdjustments(fifo);
                else
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireIndependentPositiveAdjustments(fifo);
            }
            else if (sheet.Lines.Any(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta > 0m))
            {
                if (fifo.Items.Any(i => i.Projection?.LaterIssues.Any(
                        StockReplayFifoCountSheetRemovalPlanner
                            .NeedsIssueRepricing) == true))
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireRepricedMixedAdjustments(fifo);
                else
                    StockReplayFifoCountSheetRemovalPlanner
                        .RequireIndependentMixedAdjustments(fifo);
            }
            else
                StockReplayFifoCountSheetRemovalPlanner
                    .RequireIndependentNegativeAdjustments(fifo);
            return new(fifo.Command, method, null, null, null, fifo);
        }
        throw Invalid("The count has no supported rollback cost policy.");
    }

    public static async Task<StockReplayCountSheetRemovalRootResult>
        AppendWithinRootAsync(StockPostingContext context,
            StockReplayCountSheetRemovalRootPlan plan,
            IRunningNumberService numbers, string reason, DateTime now,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        if (context.Posting.RequestId != plan.Command.RequestId
            || context.Posting.RequestFingerprint
                != plan.Command.Evidence.RequestFingerprint
            || context.Posting.PostingRole != "REVERSAL"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId
                != plan.Command.SourceDocumentId
            || context.Posting.EffectiveAt != plan.Command.EffectiveAt
            || new object?[] { plan.Zero, plan.Wac, plan.Standard, plan.Fifo }
                .Count(x => x is not null) != 1)
            throw Invalid("The count removal plan does not match its root.");
        var method = await new StockCostMethodResolver().ResolveAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            context.Posting.EffectiveAt, cancellationToken);
        if (method != plan.CostMethod)
            throw Invalid("The count's event-date cost policy changed.");
        if (plan.Zero is { } zero)
        {
            var user = context.UserId.Length > 10
                ? context.UserId[..10] : context.UserId;
            var generations = await StockReplayZeroCountRemovalRootWriter
                .AppendWithinRootAsync(context, zero, user, reason, now,
                    cancellationToken);
            return new([], generations.Select(g => g.Id).ToArray(), []);
        }
        if (plan.Wac is { } wac
            && method == StockCostMethods.MovingAverage)
        {
            var result = await StockReplayWacCountSheetRemovalRootWriter
                .AppendWithinRootAsync(context, wac, numbers, reason, now,
                    cancellationToken);
            return new(result.ReversalHistoryIds,
                result.RemovalGenerationIds,
                result.RequiredActiveHistoryIds);
        }
        if (plan.Standard is { } standard
            && method == StockCostMethods.Standard)
        {
            var result = await StockReplayStandardCountSheetRemovalRootWriter
                .AppendWithinRootAsync(context, standard, numbers, reason,
                    now, cancellationToken);
            return new(result.ReversalHistoryIds,
                result.RemovalGenerationIds, []);
        }
        if (plan.Fifo is { } fifo && method == StockCostMethods.Fifo)
        {
            if (fifo.Sheet.Lines.Count(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta != 0m) == 1)
            {
                var result = await StockReplayFifoIsolatedCountRemovalWriter
                    .AppendWithinRootAsync(context, fifo, numbers, reason, now,
                        cancellationToken);
                return new([result.ReversalHistoryId],
                    result.RemovalGenerationIds,
                    result.RequiredActiveHistoryIds);
            }
            var multiple = await StockReplayFifoBoundedCountSheetRemovalWriter
                .AppendWithinRootAsync(context, fifo, numbers, reason, now,
                    cancellationToken);
            return new(multiple.ReversalHistoryIds,
                multiple.RemovalGenerationIds,
                multiple.RequiredActiveHistoryIds);
        }
        throw Invalid("The count removal has no matching financial writer.");
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
