namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacTailRepricingResult(
    StockReplayWacWritePermit Permit,
    IReadOnlyList<int> RepricedHistoryIds,
    IReadOnlyList<int> RepricedCountLineIds);

/// <summary>
/// Applies WAC descendants from a validated insertion or removal plan in their
/// original business order. Changed count deltas require a prepared physical
/// permit and persisted correction generation. The caller writes the candidate
/// and runs all root gates.
/// </summary>
public static class StockReplayWacTailRepricingWriter
{
    /// <summary>
    /// Rejects a sheet tail needing physical count corrections or a separate
    /// receipt/settlement authority before any count-root mutation begins.
    /// </summary>
    public static void ValidatePreparedCountSheetTail(
        StockReplayWacCountSheetInsertionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        StockReplayWacCountSheetInsertionPlanner.ValidatePreparedPlan(plan);
        var candidateIds = plan.Candidates.Select(c => c.EventId).ToHashSet();
        var steps = plan.Projection.LaterSteps
            .Where(s => !candidateIds.Contains(s.EventId)).ToArray();
        ValidateTail(plan.Tail.LaterEvents, steps, null, null);
    }

    /// <summary>
    /// Reprices the sealed descendants after all initial lines for one counted
    /// item have been valued. Count candidates in the same sheet are excluded
    /// because their original facts were appended by the sheet writer.
    /// </summary>
    public static Task<StockReplayWacTailRepricingResult> AppendPreparedCountSheetTailAsync(
        StockPostingContext context,
        StockReplayWacWritePermit wacPermit,
        StockReplayWacCountSheetInsertionPlan plan,
        int firstRootPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wacPermit);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        StockReplayWacCountSheetInsertionPlanner.ValidatePreparedPlan(plan);
        if (firstRootPostingLineNo <= 0
            || wacPermit.RootPostingId != context.Posting.Id
            || wacPermit.RootPostingSequence != context.Posting.PostingSequence
            || wacPermit.LedgerEpochId != context.Epoch.Id
            || wacPermit.CompanyCode != context.CompanyCode
            || wacPermit.BranchCode != context.BranchCode
            || wacPermit.ItemCode != plan.ItemCode
            || wacPermit.PlanHash != plan.PlanHash
            || wacPermit.ChangedPiles.Count != plan.CountedPiles.Count
            || plan.CountedPiles.Any(p => !wacPermit.ChangedPiles.TryGetValue(
                p.BalLocId, out var quantity)
                || quantity != p.Projection.FinalQuantity))
            throw Invalid("The prepared count sheet WAC tail has no matching root permit.");
        var candidateIds = plan.Candidates.Select(c => c.EventId).ToHashSet();
        var steps = plan.Projection.LaterSteps
            .Where(s => !candidateIds.Contains(s.EventId)).ToArray();
        return AppendTailAsync(context, wacPermit, plan.Tail.LaterEvents,
            steps, firstRootPostingLineNo, null, null, cancellationToken);
    }

    public static async Task<StockReplayWacTailRepricingResult> AppendInsertionTailAsync(
        StockPostingContext context,
        StockReplayWacInsertionPlan plan,
        int firstRootPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        if (firstRootPostingLineNo <= 0)
            throw new ArgumentOutOfRangeException(nameof(firstRootPostingLineNo));
        var permit = await StockReplayWacInsertionPlanner.PrepareWithinRootAsync(
            context, plan, cancellationToken);
        return await AppendTailAsync(context, permit, plan.Tail.LaterEvents,
            plan.Projection.LaterSteps, firstRootPostingLineNo,
            null, null, cancellationToken);
    }

    /// <summary>
    /// Runs after the candidate and changed-count ADJ generations have been
    /// persisted. Both permits were issued before those root mutations, so
    /// replay does not try to re-read the old sealed tail at this stage.
    /// </summary>
    public static Task<StockReplayWacTailRepricingResult> AppendPreparedInsertionTailAsync(
        StockPostingContext context,
        StockReplayWacWritePermit wacPermit,
        StockReplayWacInsertionPlan plan,
        int firstRootPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wacPermit);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        StockReplayWacInsertionPlanner.ValidatePreparedPlan(plan);
        if (firstRootPostingLineNo <= 0
            || wacPermit.RootPostingId != context.Posting.Id
            || wacPermit.RootPostingSequence != context.Posting.PostingSequence
            || wacPermit.LedgerEpochId != context.Epoch.Id
            || wacPermit.CompanyCode != context.CompanyCode
            || wacPermit.BranchCode != context.BranchCode
            || wacPermit.ItemCode != plan.ItemCode
            || wacPermit.PlanHash != plan.PlanHash
            || wacPermit.ChangedPileId != plan.PhysicalPlan.BalLocId)
            throw Invalid("The prepared WAC insertion tail has no matching root permit.");
        return AppendTailAsync(context, wacPermit, plan.Tail.LaterEvents,
            plan.Projection.LaterSteps, firstRootPostingLineNo,
            null, null, cancellationToken);
    }

    public static Task<StockReplayWacTailRepricingResult> AppendPreparedInsertionTailAsync(
        StockPostingContext context,
        StockReplayWacWritePermit wacPermit,
        StockReplayWacInsertionPlan plan,
        StockCountCorrectionWritePermit countPermit,
        int firstRootPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wacPermit);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(countPermit);
        context.EnsureUnsealed();
        StockReplayWacInsertionPlanner.ValidatePreparedPlan(plan);
        if (firstRootPostingLineNo <= 0
            || wacPermit.RootPostingId != context.Posting.Id
            || countPermit.RootPostingId != context.Posting.Id
            || wacPermit.CompanyCode != context.CompanyCode
            || wacPermit.BranchCode != context.BranchCode
            || countPermit.CompanyCode != context.CompanyCode
            || countPermit.BranchCode != context.BranchCode
            || wacPermit.LedgerEpochId != context.Epoch.Id
            || countPermit.LedgerEpochId != context.Epoch.Id
            || wacPermit.ItemCode != plan.ItemCode
            || wacPermit.PlanHash != plan.PlanHash
            || countPermit.PlanHash != plan.PhysicalPlan.PlanHash
            || wacPermit.ChangedPileId != countPermit.BalLocId)
            throw Invalid("The prepared WAC and count correction permits differ.");
        return AppendTailAsync(context, wacPermit, plan.Tail.LaterEvents,
            plan.Projection.LaterSteps, firstRootPostingLineNo,
            plan, countPermit, cancellationToken);
    }

    public static async Task<StockReplayWacTailRepricingResult> AppendRemovalTailAsync(
        StockPostingContext context,
        StockReplayWacRemovalPlan plan,
        int firstRootPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        if (firstRootPostingLineNo <= 0)
            throw new ArgumentOutOfRangeException(nameof(firstRootPostingLineNo));
        var permit = await StockReplayWacRemovalPlanner.PrepareWithinRootAsync(
            context, plan, cancellationToken);
        return await AppendTailAsync(context, permit, plan.Tail.LaterEvents,
            plan.Projection.LaterSteps, firstRootPostingLineNo,
            null, null, cancellationToken);
    }

    public static void ValidatePreparedRemovalTail(
        StockReplayWacRemovalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateTail(plan.Tail.LaterEvents, plan.Projection.LaterSteps,
            null, null);
    }

    public static void ValidateProjectedCountSheetRemovalTail(
        IReadOnlyList<StockReplayWacEvent> laterEvents,
        IReadOnlyList<StockReplayWacStep> laterSteps) =>
        ValidateTail(laterEvents, laterSteps, null, null);

    /// <summary>
    /// Applies a sheet-level projection after every count adjustment has been
    /// reversed. Its complete source fingerprint was checked before mutation.
    /// </summary>
    public static Task<StockReplayWacTailRepricingResult>
        AppendPreparedCountSheetRemovalTailAsync(
            StockPostingContext context, StockReplayWacWritePermit permit,
            StockReplayWacCountSheetRemovalItem item,
            int firstRootPostingLineNo,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentNullException.ThrowIfNull(item);
        context.EnsureUnsealed();
        if (item.Repricing is not { } projection
            || firstRootPostingLineNo <= 0
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != item.ItemCode
            || permit.PlanHash != item.PlanHash
            || permit.CurrentQuantity != item.Tail.CurrentQuantity
            || permit.CurrentValue != item.Tail.CurrentValue
            || permit.FinalQuantity != projection.FinalQuantity
            || permit.FinalValue != projection.FinalValue
            || permit.ChangedPiles.Count != item.CountLineIds.Count)
            throw Invalid("The prepared WAC count removal tail has no matching root permit.");
        var removedIds = item.RemovedEvents.Select(e => e.EventId).ToHashSet();
        var later = item.Tail.LaterEvents.Where(e =>
            !removedIds.Contains(e.EventId)).ToArray();
        return AppendTailAsync(context, permit, later,
            projection.LaterSteps, firstRootPostingLineNo,
            null, null, cancellationToken);
    }

    /// <summary>
    /// Uses a removal permit validated before the root wrote its count reversal.
    /// The sealed old tail cannot be re-read after that mutation.
    /// </summary>
    public static Task<StockReplayWacTailRepricingResult> AppendPreparedRemovalTailAsync(
        StockPostingContext context, StockReplayWacWritePermit permit,
        StockReplayWacRemovalPlan plan, int firstRootPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (firstRootPostingLineNo <= 0
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != plan.ItemCode
            || permit.PlanHash != plan.PlanHash
            || permit.ChangedPileId != plan.PhysicalPlan.BalLocId)
            throw Invalid("The prepared WAC removal tail has no matching root permit.");
        return AppendTailAsync(context, permit, plan.Tail.LaterEvents,
            plan.Projection.LaterSteps, firstRootPostingLineNo,
            null, null, cancellationToken);
    }

    private static async Task<StockReplayWacTailRepricingResult> AppendTailAsync(
        StockPostingContext context,
        StockReplayWacWritePermit permit,
        IReadOnlyList<StockReplayWacEvent> laterEvents,
        IReadOnlyList<StockReplayWacStep> laterSteps,
        int firstRootPostingLineNo,
        StockReplayWacInsertionPlan? changedCountPlan,
        StockCountCorrectionWritePermit? countPermit,
        CancellationToken cancellationToken)
    {
        var steps = ValidateTail(laterEvents, laterSteps,
            changedCountPlan, countPermit);

        var histories = new List<int>();
        var countLines = new List<int>();
        var eventIndex = 0;
        foreach (var movement in laterEvents)
        {
            var reversalLine = checked(firstRootPostingLineNo + eventIndex * 2);
            var replacementLine = checked(reversalLine + 1);
            eventIndex++;
            var step = steps[movement.EventId];
            if (movement.EventId < 0)
            {
                var countLineId = ParseCountLine(movement.Key.LineIdentity);
                if (step.SignedQuantity != movement.OldSignedQuantity)
                {
                    await StockReplayWacCountCorrectionValuationWriter.AppendAsync(
                        context, permit, changedCountPlan!, countPermit!,
                        countLineId, reversalLine, replacementLine,
                        cancellationToken);
                    countLines.Add(countLineId);
                    continue;
                }
                if (step.SignedValue == movement.OldSignedValue)
                    continue;
                var replacement = await StockReplayWacCountRepricingWriter.AppendAsync(
                    context, countLineId, movement, step,
                    reversalLine, replacementLine, cancellationToken);
                if (replacement is not null)
                    countLines.Add(countLineId);
            }
            else if (movement.EventId <= int.MaxValue)
            {
                if (movement.OldSignedQuantity < 0m)
                {
                    var replacements = await StockReplayWacIssueRepricingWriter.AppendAsync(
                        context, checked((int)movement.EventId), movement, step,
                        reversalLine, replacementLine, cancellationToken);
                    if (replacements.Count > 0)
                        histories.Add(checked((int)movement.EventId));
                }
                else if (movement.OldSignedQuantity == 0m)
                {
                    var replacements = await StockReplayWacTransferRepricingWriter.AppendAsync(
                        context, checked((int)movement.EventId), movement, step,
                        reversalLine, replacementLine, cancellationToken);
                    if (replacements.Count > 0)
                        histories.Add(checked((int)movement.EventId));
                }
            }
        }
        return new(permit, histories, countLines);
    }

    private static Dictionary<long, StockReplayWacStep> ValidateTail(
        IReadOnlyList<StockReplayWacEvent> laterEvents,
        IReadOnlyList<StockReplayWacStep> laterSteps,
        StockReplayWacInsertionPlan? changedCountPlan,
        StockCountCorrectionWritePermit? countPermit)
    {
        var steps = laterSteps.ToDictionary(s => s.EventId);
        if (steps.Count != laterEvents.Count
            || laterEvents.Any(e => !steps.ContainsKey(e.EventId)))
            throw Invalid("The WAC projection does not cover every active descendant.");

        // Check unsupported quantity changes before appending any valuation
        // facts. The caller can then route those count generations separately.
        var changedCountLines = new HashSet<int>();
        foreach (var movement in laterEvents)
        {
            var step = steps[movement.EventId];
            if (step.SignedQuantity != movement.OldSignedQuantity)
            {
                if (movement.EventId >= 0 || changedCountPlan is null
                    || countPermit is null)
                    throw Invalid($"WAC event {movement.EventId} needs a physical count correction generation.");
                var countLineId = ParseCountLine(movement.Key.LineIdentity);
                if (!changedCountLines.Add(countLineId)
                    || countPermit.Lines.Count(l => l.LineId == countLineId
                        && l.PreviousGenerationId == -movement.EventId
                        && l.OldSignedAdjustmentQty == movement.OldSignedQuantity
                        && l.NewSignedAdjustmentQty == step.SignedQuantity) != 1)
                    throw Invalid($"Count line {countLineId} has no matching physical correction permit.");
            }
            if (movement.EventId > int.MaxValue
                && (step.SignedQuantity != 0m
                    || step.SignedValue != movement.OldSignedValue)
                || movement.EventId > 0 && movement.EventId <= int.MaxValue
                    && movement.OldSignedQuantity > 0m
                    && step.SignedValue != movement.OldSignedValue)
                throw Invalid($"WAC event {movement.EventId} needs separate receipt or settlement authority.");
        }
        if (countPermit is not null
            && (changedCountPlan is null
                || countPermit.Lines.Length != changedCountLines.Count))
            throw Invalid("A physical count correction has no matching WAC descendant.");

        return steps;
    }

    private static int ParseCountLine(string lineIdentity)
    {
        if (lineIdentity.Length != 11 || lineIdentity[0] != 'C'
            || !int.TryParse(lineIdentity.AsSpan(1), out var id)
            || id <= 0 || lineIdentity != $"C{id:D10}")
            throw Invalid("A WAC count descendant has no line identity.");
        return id;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.ValuationRequired, message));
}
