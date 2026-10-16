using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoRemovalPlan(
    string CompanyCode, string BranchCode, string ItemCode,
    StockReplayPhysicalRemovalPlan PhysicalPlan,
    StockReplayFifoActiveLayerSnapshot Snapshot,
    StockReplayFifoTailProjection Projection,
    string PlanHash);

/// <summary>
/// Read-only FIFO rollback plan for one original, single-leg Inventory history.
/// Transfers require a two-pile physical plan and remain unsupported here.
/// </summary>
public static class StockReplayFifoRemovalPlanner
{
    public static async Task<StockReplayFifoWritePermit> PrepareWithinRootAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        return new StockReplayFifoWritePermit(context, plan);
    }

    public static async Task<StockReplayFifoRemovalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalRemovalPlan physicalPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ValidatePhysicalProjection(companyCode, branchCode, physicalPlan);
        await ValidatePileItemAsync(db, companyCode, branchCode,
            itemCode, physicalPlan.BalLocId, cancellationToken);
        await ValidateHistoryAsync(db, companyCode, branchCode,
            itemCode, physicalPlan, cancellationToken);
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, companyCode, branchCode, itemCode, cancellationToken);
        var projection = Project(snapshot, physicalPlan);
        var hash = Hash(companyCode, branchCode, itemCode,
            physicalPlan, snapshot, projection);
        return new(companyCode, branchCode, itemCode,
            physicalPlan, snapshot, projection, hash);
    }

    public static void ValidatePreparedPlan(StockReplayFifoRemovalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePhysicalProjection(plan.CompanyCode,
            plan.BranchCode, plan.PhysicalPlan);
        StockReplayFifoTailProjection projected;
        try
        {
            projected = Project(plan.Snapshot, plan.PhysicalPlan);
        }
        catch (StockLedgerException)
        {
            throw Stale("The prepared FIFO removal projection changed.");
        }
        if (plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.PhysicalPlan, plan.Snapshot, projected)
            || plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.PhysicalPlan, plan.Snapshot,
                plan.Projection))
            throw Stale("The prepared FIFO removal plan changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.PhysicalPlan.LedgerEpochId)
            throw Stale("The FIFO removal root differs from its prepared plan.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.PhysicalPlan, cancellationToken);
        await ValidatePileItemAsync(context.Db, plan.CompanyCode,
            plan.BranchCode, plan.ItemCode, plan.PhysicalPlan.BalLocId,
            cancellationToken);
        await ValidateHistoryAsync(context.Db, plan.CompanyCode,
            plan.BranchCode, plan.ItemCode, plan.PhysicalPlan,
            cancellationToken);
        StockReplayFifoActiveLayerSnapshot current;
        try
        {
            current = await StockReplayFifoActiveLayerReader.ReadAsync(
                context.Db, plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The active FIFO layers changed after removal planning.");
        }
        if (!StockReplayFifoActiveLayerReader.SameSnapshot(
                plan.Snapshot, current))
            throw Stale("The active FIFO layers changed after removal planning.");
    }

    private static async Task ValidatePileItemAsync(AppDbContext db,
        string companyCode, string branchCode, string itemCode,
        int balLocId, CancellationToken cancellationToken)
    {
        var pileItem = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.Id == balLocId)
            .Select(b => b.ICode)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileItem != itemCode)
            throw Stale("The FIFO removal pile belongs to another item.");
    }

    private static StockReplayFifoTailProjection Project(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayPhysicalRemovalPlan physicalPlan)
    {
        var active = physicalPlan.RemovedActiveEvent;
        StockReplayFifoTailProjection projection;
        if (active.SignedDelta < 0m)
            projection = StockReplayFifoTailProjector.RemoveIssue(
                snapshot, active.Key);
        else
        {
            var origins = snapshot.Layers
                .Where(l => l.ReceiptKey == active.Key).ToArray();
            if (origins.Length != 1)
                throw Invalid("The removed FIFO receipt has no unique original layer.");
            projection = StockReplayFifoTailProjector.RemoveReceipt(
                snapshot, origins[0].LayerId);
        }
        if (projection.FinalQuantity != StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity - active.SignedDelta))
            throw Invalid("The FIFO and physical removal quantities differ.");
        return projection;
    }

    private static async Task ValidateHistoryAsync(AppDbContext db,
        string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalRemovalPlan physicalPlan,
        CancellationToken cancellationToken)
    {
        var active = physicalPlan.RemovedActiveEvent;
        var historyId = checked((int)active.EventId);
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == historyId
                && h.CompanyCode == companyCode
                && h.BranchCode == branchCode,
                cancellationToken);
        if (history is null)
            throw Invalid($"FIFO removal event {active.EventId} has no Inventory owner.");
        var singleLeg = active.SignedDelta < 0m
            ? history.FromBalLocId == physicalPlan.BalLocId
                && history.ToBalLocId is not > 0
                && StockLedgerPrecision.Quantity(history.FrStdQty ?? 0m)
                    == -active.SignedDelta
                && StockLedgerPrecision.Quantity(history.ToStdQty ?? 0m)
                    == 0m
            : history.ToBalLocId == physicalPlan.BalLocId
                && history.FromBalLocId is not > 0
                && StockLedgerPrecision.Quantity(history.ToStdQty ?? 0m)
                    == active.SignedDelta
                && StockLedgerPrecision.Quantity(history.FrStdQty ?? 0m)
                    == 0m;
        if (!singleLeg || history.ICode != itemCode
            || history.LedgerVersion != 2
            || history.LedgerEpochId != physicalPlan.LedgerEpochId
            || history.EntryRole != "ORIGINAL"
            || history.BatchStatus != "POSTED"
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.TrxDtTime != active.Key.EffectiveAt
            || active.Key.LineIdentity != $"H{historyId:D10}")
            throw Invalid($"FIFO removal event {active.EventId} has no single-leg Inventory owner.");
    }

    private static void ValidatePhysicalProjection(string companyCode,
        string branchCode, StockReplayPhysicalRemovalPlan plan)
    {
        var active = plan.RemovedActiveEvent;
        if (plan.CompanyCode != companyCode
            || plan.BranchCode != branchCode
            || active.EventId <= 0 || active.EventId > int.MaxValue
            || active.Key.ClassPriority != StockReplayEventKey.OrdinaryClass
            || active.AbsoluteCountQty is not null
            || active.SignedDelta == 0m
            || StockLedgerPrecision.Quantity(active.SignedDelta)
                != active.SignedDelta)
            throw Invalid("FIFO removal needs one original ordinary physical leg.");
        StockReplayPhysicalRemovalResult computed;
        try
        {
            computed = StockReplayPhysicalSimulator.SimulateRemoval(
                plan.CurrentQuantity, active, plan.LaterEvents);
        }
        catch (StockLedgerException)
        {
            throw Stale("The prepared physical removal projection changed.");
        }
        if (computed.HistoricalQuantityBeforeRemovedEvent
                != plan.Projection.HistoricalQuantityBeforeRemovedEvent
            || !computed.LaterSteps.SequenceEqual(plan.Projection.LaterSteps)
            || computed.FinalQuantity != plan.Projection.FinalQuantity)
            throw Stale("The prepared physical removal projection changed.");
    }

    private static string Hash(string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalRemovalPlan physicalPlan,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            physicalPlan,
            snapshot,
            projection
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
