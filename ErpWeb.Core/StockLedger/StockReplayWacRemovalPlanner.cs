using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacRemovalPlan(
    string CompanyCode,
    string BranchCode,
    string ItemCode,
    StockReplayPhysicalRemovalPlan PhysicalPlan,
    StockReplayWacEvent RemovedActiveEvent,
    StockReplayWacFinancialTail Tail,
    StockReplayWacRemovalResult Projection,
    string PlanHash)
{
    internal long[] ExcludedReversedIssueValueFactIds { get; init; } = [];
}

/// <summary>
/// Captures and revalidates one read-only WAC removal under the branch root.
/// This is a dependency plan, not a rollback writer or permission to omit
/// downstream cost corrections.
/// </summary>
public static class StockReplayWacRemovalPlanner
{
    public static async Task<StockReplayWacWritePermit> PrepareWithinRootAsync(
        StockPostingContext context,
        StockReplayWacRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        return new StockReplayWacWritePermit(context, plan.ItemCode,
            plan.PlanHash, plan.Tail.CurrentQuantity, plan.Tail.CurrentValue,
            plan.Projection.FinalQuantity, plan.Projection.FinalValue,
            plan.PhysicalPlan.BalLocId,
            plan.PhysicalPlan.Projection.FinalQuantity);
    }

    public static Task<StockReplayWacRemovalPlan> BuildAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        StockReplayPhysicalRemovalPlan physicalPlan,
        CancellationToken cancellationToken = default) =>
        BuildCoreAsync(db, companyCode, branchCode, itemCode, physicalPlan, [], cancellationToken);

    internal static Task<StockReplayWacRemovalPlan> BuildForReversedIssueAsync(
        AppDbContext db, string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalRemovalPlan physicalPlan, IvInventoryPostingService.WacReversedIssueProof proof,
        CancellationToken cancellationToken = default)
    {
        if (proof.IssueDeltaId is not long issueId || issueId <= 0
            || proof.ReversalDeltaId is not long reversalId || reversalId <= 0 || issueId == reversalId
            || proof.BalanceId != physicalPlan.BalLocId
            || proof.ReceiptQuantity != physicalPlan.RemovedActiveEvent.SignedDelta
            || proof.ProjectedQuantity != physicalPlan.Projection.FinalQuantity)
            throw Invalid("The reversed Production issue value pair is incomplete.");
        return BuildCoreAsync(db, companyCode, branchCode, itemCode,
            physicalPlan, new[] { issueId, reversalId }.Order().ToArray(), cancellationToken);
    }

    private static async Task<StockReplayWacRemovalPlan> BuildCoreAsync(
        AppDbContext db, string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalRemovalPlan physicalPlan, long[] excludedValueIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        if (physicalPlan.CompanyCode != companyCode
            || physicalPlan.BranchCode != branchCode)
            throw Invalid("The WAC removal has a different physical tenant.");
        var pileItem = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.Id == physicalPlan.BalLocId)
            .Select(b => b.ICode)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileItem != itemCode)
            throw Invalid("The removed pile belongs to another WAC item.");
        var (removed, tail) = await ReadAsync(db, companyCode, branchCode,
            itemCode, physicalPlan.RemovedActiveEvent, excludedValueIds, cancellationToken);
        var projection = StockReplayWacTailProjector.SimulateSinglePileRemoval(
            tail, physicalPlan, removed);
        var hash = Hash(companyCode, branchCode, itemCode, physicalPlan,
            removed, tail, projection, excludedValueIds);
        return new(companyCode, branchCode, itemCode, physicalPlan,
            removed, tail, projection, hash)
        {
            ExcludedReversedIssueValueFactIds = excludedValueIds.ToArray()
        };
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayWacRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.PhysicalPlan.LedgerEpochId
            || !StringComparer.Ordinal.Equals(plan.PlanHash,
                Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                    plan.PhysicalPlan, plan.RemovedActiveEvent,
                    plan.Tail, plan.Projection, plan.ExcludedReversedIssueValueFactIds)))
            throw Stale("The WAC removal evidence changed after planning.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.PhysicalPlan, cancellationToken);
        var pileItem = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && b.Id == plan.PhysicalPlan.BalLocId)
            .Select(b => b.ICode)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileItem != plan.ItemCode)
            throw Stale("The removed pile's item changed after WAC planning.");
        StockReplayWacEvent removed;
        StockReplayWacFinancialTail tail;
        StockReplayWacRemovalResult projection;
        try
        {
            (removed, tail) = await ReadAsync(context.Db, plan.CompanyCode,
                plan.BranchCode, plan.ItemCode,
                plan.PhysicalPlan.RemovedActiveEvent, plan.ExcludedReversedIssueValueFactIds, cancellationToken);
            projection = StockReplayWacTailProjector.SimulateSinglePileRemoval(
                tail, plan.PhysicalPlan, removed);
        }
        catch (StockLedgerException)
        {
            throw Stale("The active WAC removal dependencies changed after planning.");
        }
        if (!StringComparer.Ordinal.Equals(plan.PlanHash,
            Hash(plan.CompanyCode, plan.BranchCode, plan.ItemCode,
                plan.PhysicalPlan, removed, tail, projection, plan.ExcludedReversedIssueValueFactIds)))
            throw Stale("The active WAC removal dependencies changed after planning.");
    }

    private static async Task<(StockReplayWacEvent Removed,
        StockReplayWacFinancialTail Later)> ReadAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalEvent physical,
        long[] excludedValueIds, CancellationToken cancellationToken)
    {
        if (physical.Key.EffectiveAt == DateTime.MinValue)
            throw Invalid("A WAC removal has no earlier timeline instant.");
        // Read from just before the removed event, then retain only its exact
        // descendants. This resolves the authoritative financial event without
        // trusting a caller-supplied cost or count observation.
        var before = new StockReplayEventKey(
            physical.Key.EffectiveAt.AddTicks(-1),
            StockReplayEventKey.OrdinaryClass, 0, "", 0);
        var observed = excludedValueIds.Length == 0
            ? await StockReplayWacFinancialTailReader.ReadLaterAsync(
                db, companyCode, branchCode, itemCode, before, cancellationToken)
            : await StockReplayWacFinancialTailReader.ReadLaterForReversedIssueAsync(
                db, companyCode, branchCode, itemCode, before, excludedValueIds, cancellationToken);
        var matches = observed.LaterEvents.Where(e =>
            e.EventId == physical.EventId && e.Key == physical.Key).ToArray();
        if (matches.Length != 1)
            throw Invalid($"Physical event {physical.EventId} has no unique active WAC valuation.");
        var later = observed with
        {
            LaterEvents = observed.LaterEvents.Where(e =>
                e.Key.CompareTo(physical.Key) > 0).ToArray(),
            PhysicalByPile = observed.PhysicalByPile.ToDictionary(
                p => p.Key,
                p => (IReadOnlyList<StockReplayPhysicalEvent>)p.Value.Where(e =>
                    e.Key.CompareTo(physical.Key) > 0).ToArray())
        };
        return (matches[0], later);
    }

    private static string Hash(
        string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalRemovalPlan physicalPlan,
        StockReplayWacEvent removed,
        StockReplayWacFinancialTail tail,
        StockReplayWacRemovalResult projection, long[] excludedValueIds) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            physicalPlan.PlanHash,
            excludedValueIds,
            removed,
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
