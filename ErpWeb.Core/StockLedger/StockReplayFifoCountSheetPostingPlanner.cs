using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoCountSheetPostingPlan(
    StockCountSheetPhysicalPlan PhysicalSheet,
    IReadOnlyList<StockReplayFifoCountSheetInsertionPlan> Items,
    IReadOnlyList<StockCountCostBasis> FrozenBases,
    string PlanHash);

public sealed class StockReplayFifoPreparedCountSheet
{
    internal StockReplayFifoPreparedCountSheet(
        StockReplayFifoCountSheetPostingPlan plan,
        StockCountSheetWritePermit physicalPermit,
        IReadOnlyDictionary<string, StockReplayFifoCountSheetWritePermit> itemPermits)
    {
        Plan = plan;
        PhysicalPermit = physicalPermit;
        ItemPermits = itemPermits;
    }

    public StockReplayFifoCountSheetPostingPlan Plan { get; }
    public StockCountSheetWritePermit PhysicalPermit { get; }
    public IReadOnlyDictionary<string, StockReplayFifoCountSheetWritePermit> ItemPermits { get; }
}

/// <summary>
/// Covers every counted line on a FIFO-only sheet before the branch lock,
/// then checks the complete frozen basis set and each item under one root.
/// This is a preparation gate; it does not write Inventory or FIFO facts.
/// </summary>
public static class StockReplayFifoCountSheetPostingPlanner
{
    public static async Task<StockReplayFifoCountSheetPostingPlan> BuildAsync(
        AppDbContext db, StockCountSheetPhysicalPlan sheet,
        IReadOnlyCollection<StockCountCostBasis> frozenBases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(frozenBases);
        var pileIds = sheet.Lines.Select(p => p.BalLocId).ToArray();
        if (pileIds.Length == 0 || pileIds.Distinct().Count() != pileIds.Length)
            throw Invalid("FIFO count sheet has no unique counted piles.");
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(p => p.CompanyCode == sheet.CompanyCode
                && p.BranchCode == sheet.BranchCode
                && pileIds.Contains(p.Id))
            .Select(p => new { p.Id, p.ICode })
            .ToArrayAsync(cancellationToken);
        if (piles.Length != pileIds.Length
            || piles.Any(p => string.IsNullOrWhiteSpace(p.ICode)))
            throw Invalid("FIFO count sheet has a missing item pile.");
        var bases = frozenBases.OrderBy(b => b.StockCountLineId).ToArray();
        var items = new List<StockReplayFifoCountSheetInsertionPlan>();
        foreach (var itemCode in piles.Select(p => p.ICode)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            items.Add(await StockReplayFifoCountSheetInsertionPlanner.BuildAsync(
                db, sheet, itemCode, bases, cancellationToken));
        var plan = new StockReplayFifoCountSheetPostingPlan(sheet,
            items.ToArray(), bases, Hash(sheet, items, bases));
        ValidatePreparedPlan(plan);
        return plan;
    }

    public static void ValidatePreparedPlan(
        StockReplayFifoCountSheetPostingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sheet = plan.PhysicalSheet;
        if (sheet.Lines.Count == 0 || plan.Items.Count == 0
            || plan.Items.Select(p => p.ItemCode).Distinct(StringComparer.Ordinal)
                .Count() != plan.Items.Count
            || plan.Items.Any(p => p.PhysicalSheet.PlanHash != sheet.PlanHash
                || p.PhysicalSheet.CountHeaderId != sheet.CountHeaderId)
            || !StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(sheet, plan.Items, plan.FrozenBases)))
            throw Stale("Prepared FIFO count sheet changed.");
        var expected = sheet.Lines.Select(p =>
            (p.CountLineId, p.BalLocId, p.PlanHash))
            .OrderBy(p => p.CountLineId).ToArray();
        var actual = plan.Items.SelectMany(p => p.CountedPiles)
            .Select(p => (p.CountLineId, p.BalLocId, p.PlanHash))
            .OrderBy(p => p.CountLineId).ToArray();
        if (!actual.SequenceEqual(expected))
            throw Stale("FIFO plan does not cover every counted line once.");
        var positiveIds = sheet.Lines.Where(p => p.Candidate.AbsoluteCountQty > 0m)
            .Select(p => p.CountLineId!.Value).Order().ToArray();
        if (!plan.FrozenBases.Select(b => b.StockCountLineId).Order()
                .SequenceEqual(positiveIds)
            || plan.FrozenBases.Any(b => b.CompanyCode != sheet.CompanyCode
                || b.BranchCode != sheet.BranchCode
                || b.CostBasisKind is not
                    (StockCountCostBasisKinds.ManualApproved
                        or StockCountCostBasisKinds.ZeroCostApproved)
                || !StockCountAnchorCommandFactory.ValidBasis(b)))
            throw Stale("FIFO count sheet has changed frozen bases.");
        foreach (var item in plan.Items)
        {
            StockReplayFifoCountSheetInsertionPlanner.ValidatePreparedPlan(item);
            var relevant = plan.FrozenBases.Where(b => item.CountedPiles
                    .Any(p => p.CountLineId == b.StockCountLineId))
                .OrderBy(b => b.StockCountLineId).ToArray();
            if (relevant.Length != item.FrozenBases.Count
                || relevant.Zip(item.FrozenBases.OrderBy(b => b.StockCountLineId))
                    .Any(pair => !SameBasis(pair.First, pair.Second)))
                throw Stale("FIFO item lost a frozen count basis.");
        }
    }

    public static async Task<StockReplayFifoPreparedCountSheet>
        PrepareWithinRootAsync(StockPostingContext context,
            StockReplayFifoCountSheetPostingPlan plan,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidatePreparedPlan(plan);
        if (!StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, plan.FrozenBases))
            throw Stale("FIFO root has different frozen cost bases.");
        var physicalPermit = await StockReplayPhysicalPlanner
            .PrepareCountSheetWithinRootAsync(context, plan.PhysicalSheet,
                cancellationToken);
        var itemPermits = new Dictionary<string,
            StockReplayFifoCountSheetWritePermit>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
            itemPermits.Add(item.ItemCode,
                await StockReplayFifoCountSheetInsertionPlanner
                    .PrepareWithinRootAsync(context, item, cancellationToken));
        return new(plan, physicalPermit, itemPermits);
    }

    private static string Hash(StockCountSheetPhysicalPlan sheet,
        IReadOnlyCollection<StockReplayFifoCountSheetInsertionPlan> items,
        IReadOnlyCollection<StockCountCostBasis> bases) =>
        StockPostingFingerprint.Create(new
        {
            sheet.PlanHash,
            Items = items.OrderBy(p => p.ItemCode).Select(p => new
            {
                p.ItemCode, p.PlanHash
            }).ToArray(),
            Bases = bases.OrderBy(b => b.StockCountLineId).Select(b => new
            {
                b.Id, b.StockCountLineId, b.FrozenFallbackUnitCost,
                b.CostBasisKind, b.StandardCostRevisionId,
                b.CostBasisWatermark, b.CostBasisHash, b.CostEvidenceType,
                b.CostOverrideReason, b.CostApprovedBy,
                b.CostApprovedAtUtc
            }).ToArray()
        }, new { }).RequestFingerprint;

    private static bool SameBasis(StockCountCostBasis left,
        StockCountCostBasis right) =>
        left.Id == right.Id
        && left.CompanyCode == right.CompanyCode
        && left.BranchCode == right.BranchCode
        && left.StockCountLineId == right.StockCountLineId
        && left.FrozenFallbackUnitCost == right.FrozenFallbackUnitCost
        && left.CostBasisKind == right.CostBasisKind
        && left.StandardCostRevisionId == right.StandardCostRevisionId
        && left.CostBasisWatermark == right.CostBasisWatermark
        && left.CostBasisHash == right.CostBasisHash
        && left.CostEvidenceType == right.CostEvidenceType
        && left.CostOverrideReason == right.CostOverrideReason
        && left.CostApprovedBy == right.CostApprovedBy
        && left.CostApprovedAtUtc == right.CostApprovedAtUtc;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
