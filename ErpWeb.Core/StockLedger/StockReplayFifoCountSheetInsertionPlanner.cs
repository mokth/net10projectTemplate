using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoCountSheetInsertionPlan(
    StockCountSheetPhysicalPlan PhysicalSheet,
    string ItemCode,
    IReadOnlyList<StockReplayPhysicalPlan> CountedPiles,
    IReadOnlyList<StockReplayFifoCountCandidate> Candidates,
    IReadOnlyList<StockCountCostBasis> FrozenBases,
    StockReplayFifoActiveLayerSnapshot Snapshot,
    StockReplayFifoCountSheetProjection Projection,
    string PlanHash);

public sealed class StockReplayFifoCountSheetWritePermit
{
    internal StockReplayFifoCountSheetWritePermit(
        StockPostingContext context,
        StockReplayFifoCountSheetInsertionPlan plan)
    {
        RootPostingId = context.Posting.Id;
        PlanHash = plan.PlanHash;
        ItemCode = plan.ItemCode;
        SnapshotHash = plan.Snapshot.SnapshotHash;
        FinalQuantity = plan.Projection.FinalQuantity;
        FinalValue = plan.Projection.FinalValue;
    }

    public long RootPostingId { get; }
    public string PlanHash { get; }
    public string ItemCode { get; }
    public string SnapshotHash { get; }
    public decimal FinalQuantity { get; }
    public decimal FinalValue { get; }
}

/// <summary>
/// Binds one item's counted piles, immutable cost bases, and active FIFO layers
/// to a read-only insertion projection. The same evidence is checked under the
/// caller's unsealed root before any FIFO count writer may use its permit.
/// </summary>
public static class StockReplayFifoCountSheetInsertionPlanner
{
    public static async Task<StockReplayFifoCountSheetInsertionPlan> BuildAsync(
        AppDbContext db,
        StockCountSheetPhysicalPlan sheet,
        string itemCode,
        IReadOnlyCollection<StockCountCostBasis> frozenBases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(frozenBases);
        if (string.IsNullOrWhiteSpace(itemCode) || sheet.Lines.Count == 0)
            throw Invalid("A FIFO counted item and physical sheet are required.");
        var pileIds = sheet.Lines.Select(p => p.BalLocId).ToArray();
        if (pileIds.Distinct().Count() != pileIds.Length)
            throw Invalid("A FIFO count sheet has duplicate piles.");
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode, b.StdUom })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != pileIds.Length)
            throw Invalid("A FIFO count sheet has a missing tenant pile.");
        var selected = sheet.Lines.Where(p =>
                piles[p.BalLocId].ICode == itemCode)
            .OrderBy(p => p.Candidate.Key).ToArray();
        if (selected.Length == 0
            || selected.Any(p => p.CompanyCode != sheet.CompanyCode
                || p.BranchCode != sheet.BranchCode
                || p.LedgerEpochId != sheet.LedgerEpochId
                || p.CountHeaderId != sheet.CountHeaderId
                || p.CountLineId is not > 0)
            || selected.Select(p => piles[p.BalLocId].StdUom)
                .Distinct(StringComparer.Ordinal).Count() != 1
            || string.IsNullOrWhiteSpace(piles[selected[0].BalLocId].StdUom))
            throw Invalid($"Count sheet {sheet.CountHeaderId} has no consistent FIFO {itemCode} lines.");
        var selectedIds = selected.Select(p => p.CountLineId!.Value).ToHashSet();
        var positiveIds = selected.Where(p =>
                p.Candidate.AbsoluteCountQty is > 0m)
            .Select(p => p.CountLineId!.Value).Order().ToArray();
        var bases = frozenBases.Where(b => selectedIds.Contains(b.StockCountLineId))
            .OrderBy(b => b.StockCountLineId).ToArray();
        if (!bases.Select(b => b.StockCountLineId).SequenceEqual(positiveIds)
            || bases.Any(b => b.CompanyCode != sheet.CompanyCode
                || b.BranchCode != sheet.BranchCode
                || b.CostBasisKind is not
                    (StockCountCostBasisKinds.ManualApproved
                        or StockCountCostBasisKinds.ZeroCostApproved)
                || !StockCountAnchorCommandFactory.ValidBasis(b)))
            throw Invalid("FIFO count sheet has incomplete approved positive-line bases.");
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);
        var baseUom = piles[selected[0].BalLocId].StdUom;
        var candidates = selected.Select(p => new StockReplayFifoCountCandidate(
            p.Candidate.Key, p.Projection.Candidate.SignedDelta,
            basisByLine.GetValueOrDefault(p.CountLineId!.Value)
                ?.FrozenFallbackUnitCost, baseUom)).ToArray();
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            sheet.CompanyCode, sheet.BranchCode, itemCode,
            cancellationToken);
        var projection = StockReplayFifoCountSheetProjector.Insert(
            snapshot, candidates);
        var plan = new StockReplayFifoCountSheetInsertionPlan(sheet, itemCode,
            selected, candidates, bases, snapshot, projection,
            Hash(sheet, itemCode, selected, candidates, bases,
                snapshot, projection));
        ValidatePreparedPlan(plan);
        return plan;
    }

    public static void ValidatePreparedPlan(
        StockReplayFifoCountSheetInsertionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CountedPiles.Count == 0
            || plan.CountedPiles.Count != plan.Candidates.Count
            || plan.CountedPiles.Any(p =>
                !plan.PhysicalSheet.Lines.Contains(p))
            || plan.Snapshot.CompanyCode != plan.PhysicalSheet.CompanyCode
            || plan.Snapshot.BranchCode != plan.PhysicalSheet.BranchCode
            || plan.Snapshot.ItemCode != plan.ItemCode
            || plan.CountedPiles.Where(p => p.Candidate.AbsoluteCountQty > 0m)
                .Select(p => p.CountLineId!.Value).Order()
                .SequenceEqual(plan.FrozenBases.Select(b => b.StockCountLineId).Order()) is false
            || !StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.PhysicalSheet, plan.ItemCode, plan.CountedPiles,
                    plan.Candidates, plan.FrozenBases, plan.Snapshot,
                    plan.Projection)))
            throw Stale("The prepared FIFO count sheet changed.");
        var bases = plan.FrozenBases.ToDictionary(b => b.StockCountLineId);
        for (var i = 0; i < plan.CountedPiles.Count; i++)
        {
            var pile = plan.CountedPiles[i];
            var candidate = plan.Candidates[i];
            if (pile.Candidate.Key != candidate.Key
                || pile.Projection.Candidate.SignedDelta
                    != candidate.SignedAdjustmentQuantity
                || string.IsNullOrWhiteSpace(candidate.BaseUom)
                || plan.Candidates[0].BaseUom != candidate.BaseUom
                || pile.CountLineId is not int lineId
                || pile.Candidate.AbsoluteCountQty > 0m
                    && (!bases.TryGetValue(lineId, out var basis)
                        || basis.CompanyCode != plan.PhysicalSheet.CompanyCode
                        || basis.BranchCode != plan.PhysicalSheet.BranchCode
                        || basis.CostBasisKind is not
                            (StockCountCostBasisKinds.ManualApproved
                                or StockCountCostBasisKinds.ZeroCostApproved)
                        || !StockCountAnchorCommandFactory.ValidBasis(basis)
                        || basis.FrozenFallbackUnitCost
                            != candidate.FrozenFallbackUnitCost)
                || pile.Candidate.AbsoluteCountQty == 0m
                    && candidate.FrozenFallbackUnitCost is not null)
                throw Stale("The prepared FIFO count sheet line changed.");
            StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(pile);
        }
        StockReplayFifoCountSheetProjection projected;
        try
        {
            projected = StockReplayFifoCountSheetProjector.Insert(
                plan.Snapshot, plan.Candidates);
        }
        catch (StockLedgerException)
        {
            throw Stale("The prepared FIFO count sheet projection changed.");
        }
        if (!StringComparer.Ordinal.Equals(
                StockPostingFingerprint.Create(projected, new { }).RequestFingerprint,
                StockPostingFingerprint.Create(plan.Projection, new { }).RequestFingerprint))
            throw Stale("The prepared FIFO count sheet projection changed.");
    }

    public static async Task<StockReplayFifoCountSheetWritePermit>
        PrepareWithinRootAsync(StockPostingContext context,
            StockReplayFifoCountSheetInsertionPlan plan,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        var sheet = plan.PhysicalSheet;
        if (context.CompanyCode != sheet.CompanyCode
            || context.BranchCode != sheet.BranchCode
            || context.Epoch.Id != sheet.LedgerEpochId
            || context.Posting.EffectiveAt != sheet.EffectiveAt
            || context.Posting.PostingSequence != sheet.BranchWatermark + 1)
            throw Stale("FIFO count sheet belongs to another root.");
        if (await new StockCostMethodResolver().ResolveAsync(context.Db,
                sheet.CompanyCode, sheet.BranchCode, sheet.EffectiveAt,
                cancellationToken) != StockCostMethods.Fifo)
            throw Stale("FIFO count sheet policy changed after planning.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(context,
            sheet, cancellationToken);
        var pileIds = plan.CountedPiles.Select(p => p.BalLocId).ToArray();
        var currentPiles = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode, b.StdUom })
            .ToArrayAsync(cancellationToken);
        if (currentPiles.Length != pileIds.Length
            || currentPiles.Any(p => p.ICode != plan.ItemCode
                || p.StdUom != plan.Candidates[0].BaseUom))
            throw Stale("A FIFO counted pile changed after planning.");
        try
        {
            var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
                context.Db, sheet.CompanyCode, sheet.BranchCode,
                plan.ItemCode, cancellationToken);
            var projection = StockReplayFifoCountSheetProjector.Insert(
                snapshot, plan.Candidates);
            if (!StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(sheet, plan.ItemCode, plan.CountedPiles,
                    plan.Candidates, plan.FrozenBases, snapshot, projection)))
                throw Stale("FIFO count sheet cost evidence changed after planning.");
        }
        catch (StockLedgerException)
        {
            throw Stale("FIFO count sheet cost evidence changed after planning.");
        }
        return new(context, plan);
    }

    private static string Hash(StockCountSheetPhysicalPlan sheet,
        string itemCode, IReadOnlyCollection<StockReplayPhysicalPlan> piles,
        IReadOnlyCollection<StockReplayFifoCountCandidate> candidates,
        IReadOnlyCollection<StockCountCostBasis> bases,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoCountSheetProjection projection) =>
        StockPostingFingerprint.Create(new
        {
            sheet.CompanyCode, sheet.BranchCode, sheet.CountHeaderId,
            sheet.PlanHash, itemCode,
            Piles = piles.OrderBy(p => p.Candidate.Key).Select(p => new
            {
                p.CountLineId, p.BalLocId, p.PlanHash,
                p.Projection.Candidate.SignedDelta
            }).ToArray(),
            Candidates = candidates.OrderBy(c => c.Key).ToArray(),
            Bases = bases.OrderBy(b => b.StockCountLineId).Select(b => new
            {
                b.Id, b.StockCountLineId, b.CostBasisKind,
                b.FrozenFallbackUnitCost, b.CostBasisWatermark,
                b.CostBasisHash, b.CostEvidenceType,
                b.CostOverrideReason, b.CostApprovedBy,
                b.CostApprovedAtUtc
            }).ToArray(),
            Snapshot = snapshot,
            Projection = projection
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
