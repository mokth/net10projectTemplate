using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoTransferRemovalPlan(
    string CompanyCode, string BranchCode, string ItemCode,
    StockReplayPhysicalRemovalPlan Source,
    StockReplayPhysicalRemovalPlan Destination,
    StockReplayFifoActiveLayerSnapshot Snapshot,
    StockReplayFifoTailProjection Projection, string PlanHash);

/// <summary>
/// Prepares both physical legs of a posted transfer for removal while keeping
/// the branch/item FIFO pool unchanged. This plan is read-only.
/// </summary>
public static class StockReplayFifoTransferRemovalPlanner
{
    public static async Task<StockReplayFifoTransferRemovalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalRemovalPlan source,
        StockReplayPhysicalRemovalPlan destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ValidateLegs(companyCode, branchCode, source, destination);
        await ValidateOwnerAsync(db, companyCode, branchCode, itemCode,
            source, destination, cancellationToken);
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, companyCode, branchCode, itemCode, cancellationToken);
        var projection = Project(snapshot, source);
        var hash = Hash(companyCode, branchCode, itemCode,
            source, destination, snapshot, projection);
        return new(companyCode, branchCode, itemCode,
            source, destination, snapshot, projection, hash);
    }

    public static void ValidatePreparedPlan(
        StockReplayFifoTransferRemovalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateLegs(plan.CompanyCode, plan.BranchCode,
            plan.Source, plan.Destination);
        StockReplayFifoTailProjection projected;
        try
        {
            projected = Project(plan.Snapshot, plan.Source);
        }
        catch (StockLedgerException)
        {
            throw Stale("The prepared FIFO transfer removal projection changed.");
        }
        if (plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.Source, plan.Destination,
                plan.Snapshot, projected)
            || plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.Source, plan.Destination,
                plan.Snapshot, plan.Projection))
            throw Stale("The prepared FIFO transfer removal plan changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context, StockReplayFifoTransferRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.Source.LedgerEpochId
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || context.Posting.PostingRole != "REVERSAL")
            throw Stale("The FIFO transfer reversal root differs from its plan.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.Source, cancellationToken);
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.Destination, cancellationToken);
        await ValidateOwnerAsync(context.Db, plan.CompanyCode,
            plan.BranchCode, plan.ItemCode, plan.Source, plan.Destination,
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
            throw Stale("The active FIFO layers changed after transfer removal planning.");
        }
        if (!StockReplayFifoActiveLayerReader.SameSnapshot(
                plan.Snapshot, current))
            throw Stale("The active FIFO layers changed after transfer removal planning.");
    }

    private static void ValidateLegs(string companyCode,
        string branchCode, StockReplayPhysicalRemovalPlan source,
        StockReplayPhysicalRemovalPlan destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidatePhysical(source);
        ValidatePhysical(destination);
        if (source.CompanyCode != companyCode
            || source.BranchCode != branchCode
            || destination.CompanyCode != companyCode
            || destination.BranchCode != branchCode
            || source.BalLocId == destination.BalLocId
            || source.LedgerEpochId != destination.LedgerEpochId
            || source.BranchWatermark != destination.BranchWatermark
            || source.RemovedActiveEvent.EventId
                != destination.RemovedActiveEvent.EventId
            || source.RemovedActiveEvent.Key
                != destination.RemovedActiveEvent.Key
            || source.RemovedActiveEvent.SignedDelta >= 0m
            || destination.RemovedActiveEvent.SignedDelta
                != -source.RemovedActiveEvent.SignedDelta)
            throw Invalid("FIFO transfer removal requires paired active legs.");
    }

    private static void ValidatePhysical(StockReplayPhysicalRemovalPlan plan)
    {
        var active = plan.RemovedActiveEvent;
        if (active.EventId <= 0 || active.EventId > int.MaxValue
            || active.Key.ClassPriority != StockReplayEventKey.OrdinaryClass
            || active.AbsoluteCountQty is not null || active.SignedDelta == 0m
            || StockLedgerPrecision.Quantity(active.SignedDelta)
                != active.SignedDelta)
            throw Invalid("FIFO transfer removal needs an original ordinary event.");
        StockReplayPhysicalRemovalResult projected;
        try
        {
            projected = StockReplayPhysicalSimulator.SimulateRemoval(
                plan.CurrentQuantity, active, plan.LaterEvents);
        }
        catch (StockLedgerException)
        {
            throw Stale("The prepared transfer physical removal changed.");
        }
        if (projected.HistoricalQuantityBeforeRemovedEvent
                != plan.Projection.HistoricalQuantityBeforeRemovedEvent
            || !projected.LaterSteps.SequenceEqual(plan.Projection.LaterSteps)
            || projected.FinalQuantity != plan.Projection.FinalQuantity)
            throw Stale("The prepared transfer physical removal changed.");
    }

    private static async Task ValidateOwnerAsync(AppDbContext db,
        string companyCode, string branchCode, string itemCode,
        StockReplayPhysicalRemovalPlan source,
        StockReplayPhysicalRemovalPlan destination,
        CancellationToken cancellationToken)
    {
        var historyId = checked((int)source.RemovedActiveEvent.EventId);
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == historyId
                && h.CompanyCode == companyCode
                && h.BranchCode == branchCode, cancellationToken);
        if (history is null || history.ICode != itemCode
            || history.TrxType != IvTrxTypes.StockTransfer
            || history.EntryRole != "ORIGINAL"
            || history.BatchStatus != "POSTED"
            || history.LedgerVersion != 2
            || history.LedgerEpochId != source.LedgerEpochId
            || history.StockPostingId is null
            || history.FromBalLocId != source.BalLocId
            || history.ToBalLocId != destination.BalLocId
            || history.FrStdQty != -source.RemovedActiveEvent.SignedDelta
            || history.ToStdQty != destination.RemovedActiveEvent.SignedDelta
            || history.TrxDtTime != source.RemovedActiveEvent.Key.EffectiveAt)
            throw Invalid("FIFO transfer removal has no matching posted owner.");
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == history.StockPostingId.Value,
                cancellationToken);
        if (owner is null || owner.CompanyCode != companyCode
            || owner.BranchCode != branchCode
            || owner.SealedAtUtc is null
            || owner.PostingSequence
                != source.RemovedActiveEvent.Key.OriginalPostingSequence
            || owner.EffectiveAt != history.TrxDtTime)
            throw Invalid("FIFO transfer removal has no sealed chronological owner.");
    }

    private static StockReplayFifoTailProjection Project(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayPhysicalRemovalPlan source)
    {
        var projection = StockReplayFifoTailProjector.RemoveTransfer(
            snapshot, source.RemovedActiveEvent.Key);
        if (projection.FinalQuantity != snapshot.CurrentQuantity
            || projection.FinalValue != snapshot.CurrentValue)
            throw Invalid("FIFO transfer removal changed the item pool.");
        return projection;
    }

    private static string Hash(string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalRemovalPlan source,
        StockReplayPhysicalRemovalPlan destination,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            source, destination, snapshot, projection
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
