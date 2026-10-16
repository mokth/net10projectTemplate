using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayStandardCountSheetInsertionPlan(
    string CompanyCode,
    string BranchCode,
    string ItemCode,
    StockCountSheetPhysicalPlan PhysicalSheet,
    IReadOnlyList<StockReplayPhysicalPlan> CountedPiles,
    IReadOnlyList<StockReplayStandardEvent> Candidates,
    StockReplayStandardFinancialTail Tail,
    StockReplayStandardResult Projection,
    string PlanHash);

/// <summary>
/// Plans the Standard portion of one counted item before the branch lock,
/// then repeats its revision, physical and financial proof under that lock.
/// This is read-only; a writer still needs a root-scoped permit.
/// </summary>
public static class StockReplayStandardCountSheetInsertionPlanner
{
    public static async Task<StockReplayStandardWritePermit> PrepareWithinRootAsync(
        StockPostingContext context,
        StockReplayStandardCountSheetInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        if (context.CostMethod != StockCostMethods.Standard)
            throw Invalid("The Standard count sheet root has another cost method.");
        return new StockReplayStandardWritePermit(context, plan);
    }

    public static async Task<StockReplayStandardCountSheetInsertionPlan> BuildAsync(
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
            throw Invalid("A Standard counted item and physical sheet are required.");
        var pileIds = sheet.Lines.Select(p => p.BalLocId).ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && pileIds.Contains(b.Id))
            .Select(b => new { b.Id, b.ICode })
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != pileIds.Length
            || pileIds.Distinct().Count() != pileIds.Length)
            throw Invalid("The Standard count sheet has a missing item pile.");
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
        var lineIds = selected.Select(p => p.CountLineId!.Value).ToHashSet();
        var relevantBases = frozenBases.Where(b =>
            lineIds.Contains(b.StockCountLineId)).ToArray();
        if (relevantBases.GroupBy(b => b.StockCountLineId)
                .Any(g => g.Count() != 1))
            throw Invalid($"Count sheet {sheet.CountHeaderId} has duplicate Standard bases.");
        var byLine = relevantBases.ToDictionary(b => b.StockCountLineId);
        var revision = await new ItemStandardCostResolver().ResolveAsync(
            db, sheet.CompanyCode, sheet.BranchCode, itemCode,
            selected[0].Candidate.Key.EffectiveAt, cancellationToken);
        var candidates = selected.Select(p =>
            StockReplayStandardCountEventFactory.CreateInitial(p,
                itemCode, revision,
                byLine.GetValueOrDefault(p.CountLineId!.Value))).ToArray();
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
            db, sheet.CompanyCode, sheet.BranchCode, itemCode,
            selected[0].Candidate.Key, cancellationToken);
        var projection = StockReplayStandardCountSheetProjector
            .SimulateInsertion(tail, selected, candidates);
        await StockReplayStandardRevisionAuthority.ValidateAsync(db,
            sheet.CompanyCode, sheet.BranchCode, itemCode,
            candidates[0], tail.LaterEvents.Concat(candidates.Skip(1))
                .ToArray(), cancellationToken);
        return new(sheet.CompanyCode, sheet.BranchCode, itemCode,
            sheet, selected, candidates, tail, projection,
            Hash(sheet.CompanyCode, sheet.BranchCode, itemCode,
                sheet, selected, candidates, tail, projection));
    }

    public static void ValidatePreparedPlan(
        StockReplayStandardCountSheetInsertionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CountedPiles.Count == 0
            || plan.CountedPiles.Count != plan.Candidates.Count
            || plan.CountedPiles.Any(p =>
                !plan.PhysicalSheet.Lines.Contains(p)))
            throw Stale("The prepared Standard count sheet changed.");
        foreach (var pile in plan.CountedPiles)
            StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(pile);
        StockReplayStandardResult projected;
        try
        {
            projected = StockReplayStandardCountSheetProjector
                .SimulateInsertion(plan.Tail, plan.CountedPiles,
                    plan.Candidates);
        }
        catch (Exception ex) when (ex is ArgumentException
            or StockLedgerException)
        {
            throw Stale("The prepared Standard count sheet projection changed.");
        }
        if (projected.HistoricalQuantityBeforeCandidate
                != plan.Projection.HistoricalQuantityBeforeCandidate
            || projected.HistoricalValueBeforeCandidate
                != plan.Projection.HistoricalValueBeforeCandidate
            || projected.Candidate != plan.Projection.Candidate
            || !projected.LaterSteps.SequenceEqual(plan.Projection.LaterSteps)
            || projected.FinalQuantity != plan.Projection.FinalQuantity
            || projected.FinalValue != plan.Projection.FinalValue
            || !StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                    plan.PhysicalSheet, plan.CountedPiles,
                    plan.Candidates, plan.Tail, plan.Projection)))
            throw Stale("The prepared Standard count sheet changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayStandardCountSheetInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.PhysicalSheet.LedgerEpochId)
            throw Stale("The Standard count sheet belongs to another root.");
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
            || currentItems.Any(p => p.ICode != plan.ItemCode))
            throw Stale("A counted pile changed its Standard item.");
        try
        {
            var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
                context.Db, plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.CountedPiles[0].Candidate.Key,
                cancellationToken);
            var projection = StockReplayStandardCountSheetProjector
                .SimulateInsertion(tail, plan.CountedPiles, plan.Candidates);
            await StockReplayStandardRevisionAuthority.ValidateAsync(
                context.Db, plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.Candidates[0],
                tail.LaterEvents.Concat(plan.Candidates.Skip(1)).ToArray(),
                cancellationToken);
            if (!StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                    plan.PhysicalSheet, plan.CountedPiles,
                    plan.Candidates, tail, projection)))
                throw Stale("The Standard count tail changed after planning.");
        }
        catch (StockLedgerException)
        {
            throw Stale("The Standard count tail changed after planning.");
        }
    }

    private static string Hash(
        string companyCode,
        string branchCode,
        string itemCode,
        StockCountSheetPhysicalPlan sheet,
        IReadOnlyCollection<StockReplayPhysicalPlan> plans,
        IReadOnlyCollection<StockReplayStandardEvent> candidates,
        StockReplayStandardFinancialTail tail,
        StockReplayStandardResult projection) =>
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
