using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayStandardCountSheetPostingPlan(
    StockCountSheetPhysicalPlan PhysicalSheet,
    IReadOnlyList<StockReplayStandardCountSheetInsertionPlan> Items,
    string PlanHash);

public sealed class StockReplayStandardPreparedCountSheet
{
    internal StockReplayStandardPreparedCountSheet(
        StockReplayStandardCountSheetPostingPlan plan,
        StockCountSheetWritePermit countPermit,
        IReadOnlyDictionary<string, StockReplayStandardWritePermit> itemPermits,
        IReadOnlyDictionary<int, StockCountCorrectionWritePermit> correctionPermits)
    {
        Plan = plan;
        CountPermit = countPermit;
        ItemPermits = itemPermits;
        CorrectionPermits = correctionPermits;
    }

    public StockReplayStandardCountSheetPostingPlan Plan { get; }
    public StockCountSheetWritePermit CountPermit { get; }
    public IReadOnlyDictionary<string, StockReplayStandardWritePermit> ItemPermits { get; }
    public IReadOnlyDictionary<int, StockCountCorrectionWritePermit> CorrectionPermits { get; }
}

public sealed record StockReplayStandardCountSheetPostResult(
    StockCountInitialHistoryResult History,
    IReadOnlyList<StockValuationFact> InitialFacts,
    IReadOnlyList<long> RepricedRevaluationFactIds,
    IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Writes an initial Standard count sheet, changed later count generations,
/// unchanged later movements, and supported value-only revaluations.
/// </summary>
public static class StockReplayStandardCountSheetPostingWriter
{
    public static async Task<StockReplayStandardCountSheetPostingPlan> BuildAsync(
        AppDbContext db,
        StockCountSheetPhysicalPlan sheet,
        IReadOnlyCollection<StockCountCostBasis> frozenBases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(frozenBases);
        var pileIds = sheet.Lines.Select(p => p.BalLocId).ToArray();
        if (pileIds.Length == 0 || pileIds.Distinct().Count() != pileIds.Length)
            throw Invalid("The Standard count sheet has no unique counted piles.");
        var pileItems = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToArrayAsync(cancellationToken);
        if (pileItems.Length != pileIds.Length
            || pileItems.Any(p => string.IsNullOrWhiteSpace(p.ICode)))
            throw Invalid("The Standard count sheet has a missing item pile.");
        var items = new List<StockReplayStandardCountSheetInsertionPlan>();
        foreach (var itemCode in pileItems.Select(p => p.ICode)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var item = await StockReplayStandardCountSheetInsertionPlanner.BuildAsync(
                db, sheet, itemCode, frozenBases, cancellationToken);
            ValidateSupportedTail(item);
            items.Add(item);
        }
        var plan = new StockReplayStandardCountSheetPostingPlan(
            sheet, items.ToArray(), Hash(sheet, items));
        ValidatePreparedPlan(plan);
        return plan;
    }

    public static void ValidatePreparedPlan(
        StockReplayStandardCountSheetPostingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sheet = plan.PhysicalSheet;
        if (sheet.Lines.Count == 0 || plan.Items.Count == 0
            || plan.Items.Select(p => p.ItemCode).Distinct(StringComparer.Ordinal)
                .Count() != plan.Items.Count
            || plan.Items.Any(p => p.PhysicalSheet.PlanHash != sheet.PlanHash
                || p.PhysicalSheet.CountHeaderId != sheet.CountHeaderId
                || p.PhysicalSheet.CompanyCode != sheet.CompanyCode
                || p.PhysicalSheet.BranchCode != sheet.BranchCode)
            || !StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(sheet, plan.Items)))
            throw Stale("The prepared Standard count sheet changed.");
        var expected = sheet.Lines.Select(p =>
            (p.CountLineId, p.BalLocId, p.PlanHash))
            .OrderBy(p => p.CountLineId).ToArray();
        var actual = plan.Items.SelectMany(p => p.CountedPiles)
            .Select(p => (p.CountLineId, p.BalLocId, p.PlanHash))
            .OrderBy(p => p.CountLineId).ToArray();
        if (!actual.SequenceEqual(expected))
            throw Stale("The Standard plan does not cover every count line exactly once.");
        foreach (var item in plan.Items)
        {
            StockReplayStandardCountSheetInsertionPlanner.ValidatePreparedPlan(item);
            ValidateSupportedTail(item);
        }
    }

    public static async Task<StockReplayStandardPreparedCountSheet> PrepareWithinRootAsync(
        StockPostingContext context,
        StockReplayStandardCountSheetPostingPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidatePreparedPlan(plan);
        context.CostMethod = StockCostMethods.Standard;
        var countPermit = await StockReplayPhysicalPlanner
            .PrepareCountSheetWithinRootAsync(context, plan.PhysicalSheet,
                cancellationToken);
        var itemPermits = new Dictionary<string, StockReplayStandardWritePermit>(
            StringComparer.Ordinal);
        var correctionPermits = new Dictionary<int, StockCountCorrectionWritePermit>();
        foreach (var item in plan.Items)
        {
            itemPermits.Add(item.ItemCode,
                await StockReplayStandardCountSheetInsertionPlanner
                    .PrepareWithinRootAsync(context, item, cancellationToken));
            foreach (var pile in item.CountedPiles)
                correctionPermits.Add(pile.BalLocId,
                    await StockReplayPhysicalPlanner
                        .PrepareCountCorrectionsWithinRootAsync(context, pile,
                            cancellationToken));
        }
        return new StockReplayStandardPreparedCountSheet(
            plan, countPermit, itemPermits, correctionPermits);
    }

    public static async Task<StockReplayStandardCountSheetPostResult> AppendWithinRootAsync(
        StockPostingContext context,
        StockReplayStandardPreparedCountSheet prepared,
        IReadOnlyCollection<StockCountCostBasis> frozenBases,
        IRunningNumberService numbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(frozenBases);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        ValidatePreparedPlan(prepared.Plan);
        var sheet = prepared.Plan.PhysicalSheet;
        var countPermit = prepared.CountPermit;
        if (context.Db.Database.CurrentTransaction is null
            || context.CostMethod != StockCostMethods.Standard
            || countPermit.RootPostingId != context.Posting.Id
            || countPermit.PostingSequence != context.Posting.PostingSequence
            || countPermit.LedgerEpochId != context.Epoch.Id
            || countPermit.PlanHash != sheet.PlanHash
            || countPermit.Lines.Length != sheet.Lines.Count
            || prepared.ItemPermits.Count != prepared.Plan.Items.Count
            || prepared.CorrectionPermits.Count != sheet.Lines.Count
            || prepared.Plan.Items.Any(p =>
                !prepared.ItemPermits.TryGetValue(p.ItemCode, out var permit)
                || permit.RootPostingId != context.Posting.Id
                || permit.RootPostingSequence != context.Posting.PostingSequence
                || permit.LedgerEpochId != context.Epoch.Id
                || permit.PlanHash != p.PlanHash)
            || sheet.Lines.Any(p =>
                !prepared.CorrectionPermits.TryGetValue(p.BalLocId,
                    out var correction)
                || correction.RootPostingId != context.Posting.Id
                || correction.LedgerEpochId != context.Epoch.Id
                || correction.PlanHash != p.PlanHash
                || correction.FinalQuantity != p.Projection.FinalQuantity))
            throw Invalid("The Standard count sheet has no complete prepared root.");
        var positiveIds = countPermit.Lines.Where(p => p.PhysicalQty > 0m)
            .Select(p => p.LineId).Order().ToArray();
        var bases = frozenBases.ToArray();
        if (!bases.Select(b => b.StockCountLineId).Order()
                .SequenceEqual(positiveIds)
            || bases.Any(b => b.CompanyCode != context.CompanyCode
                || b.BranchCode != context.BranchCode
                || b.CostBasisKind != StockCountCostBasisKinds.StandardRevision
                || !StockCountAnchorCommandFactory.ValidBasis(b))
            || !StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, bases))
            throw Invalid("The Standard sheet lacks its exact frozen revisions.");
        var db = context.Db;
        var pileIds = countPermit.Lines.Select(p => p.BalLocId).ToArray();
        var piles = await db.IvBalLocs
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && pileIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != pileIds.Length
            || sheet.Lines.Any(p => !piles.TryGetValue(p.BalLocId, out var pile)
                || pile.StdQty != p.CurrentQuantity))
            throw Invalid("The Standard count piles changed after root preparation.");
        var byPile = sheet.Lines.ToDictionary(p => p.BalLocId);
        foreach (var line in countPermit.Lines)
        {
            var pile = piles[line.BalLocId];
            var physical = byPile[line.BalLocId];
            pile.StdQty = line.FinalQuantity;
            pile.TransDate = physical.LaterEvents.Count == 0
                ? physical.Candidate.Key.EffectiveAt
                : physical.LaterEvents.MaxBy(e => e.Key)!.Key.EffectiveAt;
        }
        await StockCountRepostBasisWriter.PersistOrVerifyAsync(db,
            countPermit, bases, cancellationToken);
        var history = await StockCountInitialHistoryWriter.AppendAsync(
            context, countPermit, numbers, cancellationToken);
        var facts = new List<StockValuationFact>();
        var repriced = new List<long>();
        var correctedHistoryIds = new List<int>();
        var nextRootLine = checked(history.Generations.Count(g =>
            g.AdjustmentHistoryId.HasValue) + 1);
        foreach (var item in prepared.Plan.Items)
        {
            var permit = prepared.ItemPermits[item.ItemCode];
            facts.AddRange(await StockReplayStandardInitialCountSheetValuationWriter
                .AppendAsync(context, countPermit, permit, item, cancellationToken));
            foreach (var pile in item.CountedPiles)
            {
                var correction = prepared.CorrectionPermits[pile.BalLocId];
                if (correction.Lines.IsDefaultOrEmpty)
                    continue;
                var positions = correction.Lines.Select((line, index) =>
                    new StockCountCorrectionPostingLines(line.LineId,
                        checked(nextRootLine + index * 2),
                        checked(nextRootLine + index * 2 + 1))).ToArray();
                var generations = await StockCountCorrectionHistoryWriter
                    .AppendAsync(context, correction, positions, numbers,
                        cancellationToken);
                correctedHistoryIds.AddRange(generations
                    .Where(g => g.AdjustmentHistoryId.HasValue)
                    .Select(g => g.AdjustmentHistoryId!.Value));
                foreach (var position in positions)
                    await StockReplayStandardCountCorrectionValuationWriter
                        .AppendAsync(context, permit, item, correction,
                            position.CountLineId,
                            position.ReversalPostingLineNo,
                            position.ReplacementPostingLineNo,
                            cancellationToken);
                nextRootLine = checked(nextRootLine + positions.Length * 2);
            }
            foreach (var revaluation in item.Tail.LaterEvents
                .Where(e => e.Kind == StockReplayStandardEventKinds.Revaluation)
                .OrderBy(e => e.Key))
            {
                var projected = item.Projection.LaterSteps.Single(s =>
                    s.EventId == revaluation.EventId);
                if (projected.SignedValue == revaluation.OldSignedValue)
                    continue;
                var originalFactId = long.MaxValue - revaluation.EventId;
                var correction = await StockReplayStandardRevaluationRepricingWriter
                    .AppendAsync(context, permit, item, originalFactId,
                        nextRootLine, checked(nextRootLine + 1),
                        cancellationToken);
                if (correction is null)
                    throw Invalid($"Standard revaluation fact {originalFactId} was not corrected.");
                repriced.Add(originalFactId);
                nextRootLine = checked(nextRootLine + 2);
            }
            await StockReplayStandardCostStateWriter.ApplyAsync(
                context, permit, cancellationToken);
        }
        var required = history.Generations
            .Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value)
            .Concat(correctedHistoryIds)
            .Order().ToArray();
        return new StockReplayStandardCountSheetPostResult(
            history, facts, repriced, required);
    }

    private static void ValidateSupportedTail(
        StockReplayStandardCountSheetInsertionPlan item)
    {
        var steps = item.Projection.LaterSteps
            .ToDictionary(s => s.EventId);
        if (item.Tail.LaterEvents.Any(e =>
                !steps.TryGetValue(e.EventId, out var step)
                || e.Kind switch
                {
                    StockReplayStandardEventKinds.Movement =>
                        step.SignedQuantity != e.OldSignedQuantity
                        || step.SignedValue != e.OldSignedValue,
                    StockReplayStandardEventKinds.Revaluation =>
                        step.SignedQuantity != 0m,
                    StockReplayStandardEventKinds.Count =>
                        step.SignedValue != StockLedgerPrecision.Money(
                            step.SignedQuantity * e.UnitCost),
                    _ => true
                }))
            throw Invalid($"Item {item.ItemCode} has an unsupported later Standard effect.");
    }

    private static string Hash(StockCountSheetPhysicalPlan sheet,
        IReadOnlyCollection<StockReplayStandardCountSheetInsertionPlan> items) =>
        StockPostingFingerprint.Create(new
        {
            sheet.PlanHash,
            items = items.OrderBy(p => p.ItemCode, StringComparer.Ordinal)
                .Select(p => new { p.ItemCode, p.PlanHash }).ToArray()
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
