using ErpWeb.Core.Numbering;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacCountSheetPostingPlan(
    StockCountSheetPhysicalPlan PhysicalSheet,
    IReadOnlyList<StockReplayWacCountSheetInsertionPlan> Items,
    string PlanHash);

/// <summary>
/// Issued under one unsealed root before the count changes any pile, basis,
/// history, or cost state. It covers every counted line on a WAC-only sheet.
/// </summary>
public sealed class StockReplayWacPreparedCountSheet
{
    internal StockReplayWacPreparedCountSheet(
        StockReplayWacCountSheetPostingPlan plan,
        StockCountSheetWritePermit countPermit,
        IReadOnlyDictionary<string, StockReplayWacWritePermit> itemPermits)
    {
        Plan = plan;
        CountPermit = countPermit;
        ItemPermits = itemPermits;
    }

    public StockReplayWacCountSheetPostingPlan Plan { get; }
    public StockCountSheetWritePermit CountPermit { get; }
    public IReadOnlyDictionary<string, StockReplayWacWritePermit> ItemPermits { get; }
}

public sealed record StockReplayWacCountSheetPostResult(
    StockCountInitialHistoryResult History,
    IReadOnlyList<StockValuationFact> InitialFacts,
    IReadOnlyList<int> RepricedHistoryIds,
    IReadOnlyList<int> RepricedCountLineIds,
    IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Appends the WAC portion of an initial historical count under a caller-owned
/// transaction. The caller must reconcile dependent modules and invoke replay
/// completion before committing; unsupported tails fail closed.
/// </summary>
public static class StockReplayWacCountSheetPostingWriter
{
    public static async Task<StockReplayWacCountSheetPostingPlan> BuildAsync(
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
            throw Invalid("The WAC count sheet has no unique counted piles.");
        var pileItems = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToArrayAsync(cancellationToken);
        if (pileItems.Length != pileIds.Length
            || pileItems.Any(p => string.IsNullOrWhiteSpace(p.ICode)))
            throw Invalid("The WAC count sheet has a missing or invalid item pile.");
        var items = new List<StockReplayWacCountSheetInsertionPlan>();
        foreach (var itemCode in pileItems.Select(p => p.ICode)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var item = await StockReplayWacCountSheetInsertionPlanner.BuildAsync(
                db, sheet, itemCode, frozenBases, cancellationToken);
            StockReplayWacTailRepricingWriter.ValidatePreparedCountSheetTail(item);
            items.Add(item);
        }
        var plan = new StockReplayWacCountSheetPostingPlan(sheet, items.ToArray(),
            Hash(sheet, items));
        ValidatePreparedPlan(plan);
        await ValidateDependentCostTailAsync(db, plan, cancellationToken);
        return plan;
    }

    public static void ValidatePreparedPlan(
        StockReplayWacCountSheetPostingPlan plan)
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
            throw Stale("The prepared WAC count sheet changed.");
        var expected = sheet.Lines.Select(p =>
            (p.CountLineId, p.BalLocId, p.PlanHash))
            .OrderBy(p => p.CountLineId).ToArray();
        var actual = plan.Items.SelectMany(p => p.CountedPiles)
            .Select(p => (p.CountLineId, p.BalLocId, p.PlanHash))
            .OrderBy(p => p.CountLineId).ToArray();
        if (!actual.SequenceEqual(expected))
            throw Stale("The WAC count plan does not cover every counted line exactly once.");
        foreach (var item in plan.Items)
        {
            StockReplayWacCountSheetInsertionPlanner.ValidatePreparedPlan(item);
            StockReplayWacTailRepricingWriter.ValidatePreparedCountSheetTail(item);
        }
    }

    public static async Task<StockReplayWacPreparedCountSheet> PrepareWithinRootAsync(
        StockPostingContext context,
        StockReplayWacCountSheetPostingPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidatePreparedPlan(plan);
        await ValidateDependentCostTailAsync(context.Db, plan,
            cancellationToken);
        var countPermit = await StockReplayPhysicalPlanner
            .PrepareCountSheetWithinRootAsync(context, plan.PhysicalSheet,
                cancellationToken);
        var itemPermits = new Dictionary<string, StockReplayWacWritePermit>(
            StringComparer.Ordinal);
        foreach (var item in plan.Items)
            itemPermits.Add(item.ItemCode,
                await StockReplayWacCountSheetInsertionPlanner
                    .PrepareWithinRootAsync(context, item, cancellationToken));
        return new StockReplayWacPreparedCountSheet(plan, countPermit,
            itemPermits);
    }

    public static async Task<StockReplayWacCountSheetPostResult> AppendWithinRootAsync(
        StockPostingContext context,
        StockReplayWacPreparedCountSheet prepared,
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
        await ValidateDependentCostTailAsync(context.Db, prepared.Plan,
            cancellationToken);
        var sheet = prepared.Plan.PhysicalSheet;
        var countPermit = prepared.CountPermit;
        if (context.Db.Database.CurrentTransaction is null
            || countPermit.RootPostingId != context.Posting.Id
            || countPermit.PostingSequence != context.Posting.PostingSequence
            || countPermit.LedgerEpochId != context.Epoch.Id
            || countPermit.PlanHash != sheet.PlanHash
            || countPermit.Lines.Length != sheet.Lines.Count
            || prepared.ItemPermits.Count != prepared.Plan.Items.Count
            || prepared.Plan.Items.Any(p =>
                !prepared.ItemPermits.TryGetValue(p.ItemCode, out var permit)
                || permit.RootPostingId != context.Posting.Id
                || permit.RootPostingSequence != context.Posting.PostingSequence
                || permit.LedgerEpochId != context.Epoch.Id
                || permit.PlanHash != p.PlanHash))
            throw Invalid("The WAC count sheet has no complete prepared root.");

        var positiveIds = countPermit.Lines
            .Where(p => p.PhysicalQty > 0m)
            .Select(p => p.LineId).Order().ToArray();
        var bases = frozenBases.ToArray();
        if (!bases.Select(b => b.StockCountLineId).Order()
                .SequenceEqual(positiveIds)
            || bases.Any(b => b.CompanyCode != context.CompanyCode
                || b.BranchCode != context.BranchCode
                || !StockCountAnchorCommandFactory.ValidBasis(b))
            || !StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, bases))
            throw Invalid("The WAC count sheet lacks its exact frozen cost bases.");

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
            throw Invalid("The WAC count piles changed after root preparation.");
        var physicalByPile = sheet.Lines.ToDictionary(p => p.BalLocId);
        foreach (var line in countPermit.Lines)
        {
            var pile = piles[line.BalLocId];
            var physical = physicalByPile[line.BalLocId];
            pile.StdQty = line.FinalQuantity;
            pile.TransDate = physical.LaterEvents.Count == 0
                ? physical.Candidate.Key.EffectiveAt
                : physical.LaterEvents.MaxBy(e => e.Key)!.Key.EffectiveAt;
        }
        await StockCountRepostBasisWriter.PersistOrVerifyAsync(db,
            countPermit, bases, cancellationToken);

        var history = await StockCountInitialHistoryWriter.AppendAsync(
            context, countPermit, numbers, cancellationToken);
        var initialFacts = new List<StockValuationFact>();
        var repricedHistoryIds = new HashSet<int>();
        var repricedCountLineIds = new HashSet<int>();
        var nextRootLine = checked(history.Generations.Count(g =>
            g.AdjustmentHistoryId.HasValue) + 1);
        foreach (var item in prepared.Plan.Items)
        {
            var permit = prepared.ItemPermits[item.ItemCode];
            initialFacts.AddRange(await StockReplayWacInitialCountSheetValuationWriter
                .AppendAsync(context, countPermit, permit, item, cancellationToken));
            var tail = await StockReplayWacTailRepricingWriter
                .AppendPreparedCountSheetTailAsync(context, permit, item,
                    nextRootLine, cancellationToken);
            nextRootLine = checked(nextRootLine + item.Tail.LaterEvents.Count * 2);
            repricedHistoryIds.UnionWith(tail.RepricedHistoryIds);
            repricedCountLineIds.UnionWith(tail.RepricedCountLineIds);
            await StockReplayWacCostStateWriter.ApplyAsync(context, permit,
                cancellationToken);
        }
        var required = history.Generations
            .Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).ToHashSet();
        required.UnionWith(repricedHistoryIds);
        if (repricedCountLineIds.Count > 0)
        {
            var rows = await db.StockCountReplayGenerations.AsNoTracking()
                .Where(g => g.CompanyCode == context.CompanyCode
                    && g.BranchCode == context.BranchCode
                    && repricedCountLineIds.Contains(g.StockCountLineId))
                .ToArrayAsync(cancellationToken);
            var active = StockCountGenerationResolver.ResolveActive(rows);
            if (active.Count != repricedCountLineIds.Count
                || active.Values.Any(g => g.IsRemoval
                    || g.AdjustmentHistoryId is null))
                throw Invalid("A repriced count descendant has no active ADJ history.");
            required.UnionWith(active.Values.Select(g =>
                g.AdjustmentHistoryId!.Value));
        }
        return new StockReplayWacCountSheetPostResult(history, initialFacts,
            repricedHistoryIds.Order().ToArray(),
            repricedCountLineIds.Order().ToArray(), required.Order().ToArray());
    }

    private static async Task ValidateDependentCostTailAsync(
        AppDbContext db,
        StockReplayWacCountSheetPostingPlan plan,
        CancellationToken cancellationToken)
    {
        // A changed issue amount can be written to Inventory valuation here,
        // but Sales, Procurement and Production also own derived cost records.
        // Until those correction writers share this root, only Inventory-owned
        // MI and Scrap issues may be repriced by an initial count.
        var changedIssueIds = plan.Items.SelectMany(item =>
        {
            var steps = item.Projection.LaterSteps.ToDictionary(s => s.EventId);
            return item.Tail.LaterEvents
                .Where(e => e.EventId > 0 && e.EventId <= int.MaxValue
                    && e.OldSignedQuantity < 0m
                    && steps.TryGetValue(e.EventId, out var step)
                    && step.SignedValue != e.OldSignedValue)
                .Select(e => checked((int)e.EventId));
        }).Distinct().ToArray();
        if (changedIssueIds.Length == 0)
            return;

        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => changedIssueIds.Contains(h.Id)
                && h.CompanyCode == plan.PhysicalSheet.CompanyCode
                && h.BranchCode == plan.PhysicalSheet.BranchCode)
            .Select(h => new { h.Id, h.TrxType, h.StockPostingId })
            .ToArrayAsync(cancellationToken);
        var ownerIds = histories.Where(h => h.StockPostingId.HasValue)
            .Select(h => h.StockPostingId!.Value).Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (histories.Length != changedIssueIds.Length
            || histories.Any(h => h.TrxType is not
                (IvTrxTypes.MiscellaneousIssue or IvTrxTypes.Scrap)
                || h.StockPostingId is not long ownerId
                || !owners.TryGetValue(ownerId, out var owner)
                || owner.CompanyCode != plan.PhysicalSheet.CompanyCode
                || owner.BranchCode != plan.PhysicalSheet.BranchCode
                || owner.SourceModule != "INVENTORY"))
            throw new StockLedgerException(new StockLedgerError(
                StockLedgerErrorCodes.ValuationRequired,
                "The WAC count would change a later Sales, Procurement, or Production cost without a dependent correction writer."));
    }

    private static string Hash(StockCountSheetPhysicalPlan sheet,
        IReadOnlyCollection<StockReplayWacCountSheetInsertionPlan> items) =>
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
