using ErpWeb.Core.Inventory;
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
    string PlanHash)
{
    internal long[] ExcludedReversedIssueValueFactIds { get; init; } = [];
}

/// <summary>
/// Captures a read-only WAC financial insertion before the branch lock and
/// checks the same evidence under the root lock. This does not write facts or
/// authorize ignoring other items' procurement, Production, or Sales effects.
/// </summary>
public static class StockReplayWacInsertionPlanner
{
    /// <summary>Checks a pre-mutation plan before prepared writers consume it.</summary>
    public static void ValidatePreparedPlan(StockReplayWacInsertionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(
            plan.PhysicalPlan);
        if (!StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                    plan.PhysicalPlan, plan.Candidate,
                    plan.Tail, plan.Projection, plan.ExcludedReversedIssueValueFactIds)))
            throw Stale("The prepared WAC insertion plan changed.");
    }

    public static async Task<StockReplayWacWritePermit> PrepareWithinRootAsync(
        StockPostingContext context,
        StockReplayWacInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        return new StockReplayWacWritePermit(context, plan.ItemCode,
            plan.PlanHash, plan.Tail.CurrentQuantity, plan.Tail.CurrentValue,
            plan.Projection.FinalQuantity, plan.Projection.FinalValue,
            plan.PhysicalPlan.BalLocId,
            plan.PhysicalPlan.Projection.FinalQuantity);
    }

    public static Task<StockReplayWacInsertionPlan> BuildAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        StockReplayPhysicalPlan physicalPlan,
        StockReplayWacEvent candidate,
        CancellationToken cancellationToken = default) =>
        BuildCoreAsync(db, companyCode, branchCode, itemCode, physicalPlan, candidate, [], cancellationToken);

    internal static Task<StockReplayWacInsertionPlan> BuildForReversedIssueAsync(
        AppDbContext db, string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalPlan physicalPlan, StockReplayWacEvent candidate,
        IvInventoryPostingService.WacReversedIssueProof proof, CancellationToken cancellationToken = default)
    {
        if (proof.IssueDeltaId is not long issueId || issueId <= 0
            || proof.ReversalDeltaId is not long reverseId || reverseId <= 0 || issueId == reverseId
            || proof.BalanceId != physicalPlan.BalLocId
            || proof.ReceiptQuantity != physicalPlan.Candidate.SignedDelta
            || proof.ProjectedQuantity != physicalPlan.Projection.FinalQuantity
            || proof.ReceiptValue != candidate.FixedSignedValue)
            throw Invalid("The reversed Production issue value pair is incomplete.");
        return BuildCoreAsync(db, companyCode, branchCode, itemCode, physicalPlan, candidate,
            new[] { issueId, reverseId }.Order().ToArray(), cancellationToken);
    }

    internal static Task<StockReplayWacInsertionPlan> BuildForReversedSaleAsync(
        AppDbContext db, string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalPlan physicalPlan, StockReplayWacEvent candidate,
        IvInventoryPostingService.ReversedSaleCycleState cycle, CancellationToken cancellationToken = default)
    {
        if (cycle.SaleDeltaId is not long saleId || saleId <= 0
            || cycle.ReversalDeltaId is not long reverseId || reverseId <= 0 || saleId == reverseId
            || cycle.ActiveRootId is not > 0 || cycle.ReceiptBalanceId != physicalPlan.BalLocId
            || cycle.ReceiptQuantity != physicalPlan.Candidate.SignedDelta
            || cycle.ReceiptValue != candidate.FixedSignedValue)
            throw Invalid("The reversed sale value pair or receipt differs from its verified replay evidence.");
        return BuildCoreAsync(db, companyCode, branchCode, itemCode, physicalPlan, candidate,
            new[] { saleId, reverseId }.Order().ToArray(), cancellationToken);
    }

    private static async Task<StockReplayWacInsertionPlan> BuildCoreAsync(
        AppDbContext db, string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalPlan physicalPlan, StockReplayWacEvent candidate,
        long[] excludedValueIds, CancellationToken cancellationToken)
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
        var tail = await ReadTailAsync(db, companyCode, branchCode, itemCode,
            physicalPlan.Candidate.Key, excludedValueIds, cancellationToken);
        var projection = StockReplayWacTailProjector.SimulateSinglePileInsertion(
            tail, physicalPlan, candidate);
        var hash = Hash(companyCode, branchCode, itemCode,
            physicalPlan, candidate, tail, projection, excludedValueIds);
        return new(companyCode, branchCode, itemCode,
            physicalPlan, candidate, tail, projection, hash)
        {
            ExcludedReversedIssueValueFactIds = excludedValueIds.ToArray()
        };
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayWacInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.PhysicalPlan.LedgerEpochId
            || plan.Candidate.Key != plan.PhysicalPlan.Candidate.Key
            || plan.Candidate.EventId != plan.PhysicalPlan.Candidate.EventId
            || !StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                    plan.PhysicalPlan, plan.Candidate, plan.Tail, plan.Projection,
                    plan.ExcludedReversedIssueValueFactIds)))
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
            currentTail = await ReadTailAsync(
                context.Db, plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                plan.Candidate.Key, plan.ExcludedReversedIssueValueFactIds, cancellationToken);
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
                currentTail, currentProjection, plan.ExcludedReversedIssueValueFactIds)))
            throw Stale("The active WAC tail changed after replay planning.");
    }

    private static Task<StockReplayWacFinancialTail> ReadTailAsync(
        AppDbContext db, string companyCode, string branchCode, string itemCode,
        StockReplayEventKey after, long[] excludedValueIds, CancellationToken cancellationToken) =>
        excludedValueIds.Length == 0
            ? StockReplayWacFinancialTailReader.ReadLaterAsync(db, companyCode, branchCode, itemCode, after, cancellationToken)
            : StockReplayWacFinancialTailReader.ReadLaterForReversedIssueAsync(
                db, companyCode, branchCode, itemCode, after, excludedValueIds, cancellationToken);

    private static string Hash(
        string companyCode,
        string branchCode,
        string itemCode,
        StockReplayPhysicalPlan physicalPlan,
        StockReplayWacEvent candidate,
        StockReplayWacFinancialTail tail,
        StockReplayWacResult projection, long[] excludedValueIds) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            physicalPlan.PlanHash,
            excludedValueIds,
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
