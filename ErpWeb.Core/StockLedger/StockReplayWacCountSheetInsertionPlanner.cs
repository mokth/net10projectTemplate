using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacCountSheetInsertionPlan(
    string CompanyCode,
    string BranchCode,
    string ItemCode,
    StockCountSheetPhysicalPlan PhysicalSheet,
    IReadOnlyList<StockReplayPhysicalPlan> CountedPiles,
    IReadOnlyList<StockReplayWacEvent> Candidates,
    StockReplayWacFinancialTail Tail,
    StockReplayWacResult Projection,
    string PlanHash);

/// <summary>
/// Plans all counted piles of one item in a sheet against one shared WAC pool.
/// The resulting plan must be checked again under the posting root lock.
/// </summary>
public static class StockReplayWacCountSheetInsertionPlanner
{
    public static async Task<StockReplayWacWritePermit> PrepareWithinRootAsync(
        StockPostingContext context,
        StockReplayWacCountSheetInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        return new StockReplayWacWritePermit(context, plan.ItemCode,
            plan.PlanHash, plan.Tail.CurrentQuantity, plan.Tail.CurrentValue,
            plan.Projection.FinalQuantity, plan.Projection.FinalValue,
            plan.CountedPiles.ToImmutableDictionary(p => p.BalLocId,
                p => p.Projection.FinalQuantity));
    }

    public static async Task<StockReplayWacCountSheetInsertionPlan> BuildAsync(
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
            throw Invalid("A counted item and physical sheet are required.");
        var pileIds = sheet.Lines.Select(p => p.BalLocId).ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != pileIds.Length
            || pileIds.Distinct().Count() != pileIds.Length)
            throw Invalid("The count sheet has a missing or duplicate item pile.");
        var selected = sheet.Lines.Where(p =>
                piles[p.BalLocId].ICode == itemCode)
            .OrderBy(p => p.Candidate.Key).ToArray();
        if (selected.Length == 0
            || selected.Any(p => p.CompanyCode != sheet.CompanyCode
                || p.BranchCode != sheet.BranchCode
                || p.LedgerEpochId != sheet.LedgerEpochId
                || p.CountHeaderId != sheet.CountHeaderId
                || p.CountLineId is not > 0))
            throw Invalid($"Count sheet {sheet.CountHeaderId} has no consistent {itemCode} line.");
        var selectedIds = selected.Select(p => p.CountLineId!.Value).ToHashSet();
        var relevantBases = frozenBases.Where(b =>
            selectedIds.Contains(b.StockCountLineId)).ToArray();
        if (relevantBases.GroupBy(b => b.StockCountLineId)
                .Any(g => g.Count() != 1))
            throw Invalid($"Count sheet {sheet.CountHeaderId} has duplicate cost bases.");
        var byLine = relevantBases.ToDictionary(b => b.StockCountLineId);
        var candidates = selected.Select(p =>
            StockReplayWacCountEventFactory.CreateInitial(p,
                byLine.GetValueOrDefault(p.CountLineId!.Value))).ToArray();
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, sheet.CompanyCode, sheet.BranchCode, itemCode,
            selected[0].Candidate.Key, cancellationToken);
        var projection = StockReplayWacTailProjector.SimulateCountSheetInsertion(
            tail, selected, candidates);
        var hash = Hash(sheet.CompanyCode, sheet.BranchCode, itemCode,
            sheet, selected, candidates, tail, projection);
        return new(sheet.CompanyCode, sheet.BranchCode, itemCode,
            sheet, selected, candidates, tail, projection, hash);
    }

    public static void ValidatePreparedPlan(
        StockReplayWacCountSheetInsertionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CountedPiles.Count == 0
            || plan.CountedPiles.Count != plan.Candidates.Count
            || plan.CountedPiles.Any(p =>
                !plan.PhysicalSheet.Lines.Contains(p)))
            throw Stale("The prepared count sheet WAC plan changed.");
        foreach (var pile in plan.CountedPiles)
            StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(pile);
        if (!StringComparer.Ordinal.Equals(plan.PlanHash,
            Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                plan.PhysicalSheet, plan.CountedPiles,
                plan.Candidates, plan.Tail, plan.Projection)))
            throw Stale("The prepared count sheet WAC plan changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayWacCountSheetInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.PhysicalSheet.LedgerEpochId)
            throw Stale("The count sheet WAC plan belongs to another root.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.PhysicalSheet, cancellationToken);
        var pileIds = plan.CountedPiles.Select(p => p.BalLocId).ToArray();
        var currentItems = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToArrayAsync(cancellationToken);
        if (currentItems.Length != pileIds.Length
            || currentItems.Any(b => b.ICode != plan.ItemCode))
            throw Stale("A counted pile changed its WAC item after planning.");
        try
        {
            var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
                context.Db, plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.CountedPiles[0].Candidate.Key,
                cancellationToken);
            var projection = StockReplayWacTailProjector
                .SimulateCountSheetInsertion(tail,
                    plan.CountedPiles, plan.Candidates);
            if (!StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                    plan.PhysicalSheet, plan.CountedPiles,
                    plan.Candidates, tail, projection)))
                throw Stale("The WAC count sheet tail changed after planning.");
        }
        catch (StockLedgerException)
        {
            throw Stale("The WAC count sheet tail changed after planning.");
        }
    }

    private static string Hash(
        string companyCode,
        string branchCode,
        string itemCode,
        StockCountSheetPhysicalPlan sheet,
        IReadOnlyCollection<StockReplayPhysicalPlan> plans,
        IReadOnlyCollection<StockReplayWacEvent> candidates,
        StockReplayWacFinancialTail tail,
        StockReplayWacResult projection) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            sheet.PlanHash,
            piles = plans.OrderBy(p => p.Candidate.Key)
                .Select(p => new { p.CountLineId, p.BalLocId, p.PlanHash })
                .ToArray(),
            candidates = candidates.OrderBy(c => c.Key).ToArray(),
            tail.CurrentQuantity,
            tail.CurrentValue,
            later = tail.LaterEvents,
            currentPiles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
                .Select(p => new
                {
                    PileId = p.Key, Quantity = p.Value,
                    Events = tail.PhysicalByPile[p.Key]
                }).ToArray(),
            projection
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
