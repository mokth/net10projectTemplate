using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacInsertionPlan(
    string CompanyCode,
    string BranchCode,
    string ItemCode,
    StockReplayPhysicalPlan PhysicalPlan,
    StockReplayWacEvent Candidate,
    StockReplayWacFinancialTail Tail,
    StockReplayWacResult Projection,
    string PlanHash);

/// <summary>
/// Captures a read-only WAC financial insertion before the branch lock and
/// checks the same evidence under the root lock. This does not write facts or
/// authorize ignoring other items' procurement, Production, or Sales effects.
/// </summary>
public static class StockReplayWacInsertionPlanner
{
    public static async Task<StockReplayWacInsertionPlan> BuildAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        StockReplayPhysicalPlan physicalPlan,
        StockReplayWacEvent candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(candidate);
        if (physicalPlan.CompanyCode != companyCode
            || physicalPlan.BranchCode != branchCode)
            throw Invalid("The WAC plan has a different physical tenant.");
        var pileItem = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.Id == physicalPlan.BalLocId)
            .Select(b => b.ICode)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileItem != itemCode)
            throw Invalid("The WAC plan's candidate pile belongs to another item.");
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, companyCode, branchCode, itemCode,
            physicalPlan.Candidate.Key, cancellationToken);
        var projection = StockReplayWacTailProjector.SimulateSinglePileInsertion(
            tail, physicalPlan, candidate);
        var hash = Hash(companyCode, branchCode, itemCode,
            physicalPlan, candidate, tail, projection);
        return new(companyCode, branchCode, itemCode,
            physicalPlan, candidate, tail, projection, hash);
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayWacInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.PhysicalPlan.LedgerEpochId
            || plan.Candidate.Key != plan.PhysicalPlan.Candidate.Key
            || plan.Candidate.EventId != plan.PhysicalPlan.Candidate.EventId
            || !StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                    plan.PhysicalPlan, plan.Candidate, plan.Tail, plan.Projection)))
            throw Stale("The WAC insertion evidence changed after planning.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.PhysicalPlan, cancellationToken);
        var pileItem = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && b.Id == plan.PhysicalPlan.BalLocId)
            .Select(b => b.ICode)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileItem != plan.ItemCode)
            throw Stale("The candidate pile's item changed after WAC planning.");
        StockReplayWacFinancialTail currentTail;
        StockReplayWacResult currentProjection;
        try
        {
            currentTail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
                context.Db, plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                plan.Candidate.Key, cancellationToken);
            currentProjection = StockReplayWacTailProjector.SimulateSinglePileInsertion(
                currentTail, plan.PhysicalPlan, plan.Candidate);
        }
        catch (StockLedgerException)
        {
            // The pre-lock plan was valid. A changed or removed dependency now
            // needs a fresh plan outside this root's branch lock.
            throw Stale("The active WAC tail changed after replay planning.");
        }
        if (!StringComparer.Ordinal.Equals(plan.PlanHash,
            Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                plan.PhysicalPlan, plan.Candidate,
                currentTail, currentProjection)))
            throw Stale("The active WAC tail changed after replay planning.");
    }

    private static string Hash(
        string companyCode,
        string branchCode,
        string itemCode,
        StockReplayPhysicalPlan physicalPlan,
        StockReplayWacEvent candidate,
        StockReplayWacFinancialTail tail,
        StockReplayWacResult projection) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            physicalPlan.PlanHash,
            candidate,
            tail.CurrentQuantity,
            tail.CurrentValue,
            later = tail.LaterEvents,
            piles = tail.CurrentQuantityByPile.OrderBy(p => p.Key)
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
