using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoTransferInsertionPlan(
    string CompanyCode, string BranchCode, string ItemCode,
    StockReplayPhysicalPlan Source, StockReplayPhysicalPlan Destination,
    StockReplayFifoActiveLayerSnapshot Snapshot,
    StockReplayFifoTailProjection Projection, string PlanHash)
{
    public StockReplayFifoTransferDraftAuthority? DraftAuthority { get; init; }
}

/// <summary>
/// Binds the two physical legs of one transfer to a value-neutral FIFO event.
/// The destination must already have a pile. This read-only plan does not
/// authorize posting; a transfer writer must verify its draft document.
/// </summary>
public static class StockReplayFifoTransferInsertionPlanner
{
    public static async Task<StockReplayFifoTransferInsertionPlan> BuildDraftAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, StockReplayPhysicalPlan source,
        StockReplayPhysicalPlan destination,
        CancellationToken cancellationToken = default)
    {
        var authority = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(
            db, companyCode, branchCode, batchNo,
            source.BalLocId, destination.BalLocId, cancellationToken);
        if (source.Candidate.Key.EffectiveAt != authority.EffectiveAt
            || source.Candidate.SignedDelta != -authority.Quantity
            || destination.Candidate.SignedDelta != authority.Quantity)
            throw Invalid("The paired transfer differs from its draft movement.");
        var plan = await BuildAsync(db, companyCode, branchCode,
            authority.ItemCode, source, destination, cancellationToken);
        return plan with
        {
            DraftAuthority = authority,
            PlanHash = Hash(companyCode, branchCode, authority.ItemCode,
                source, destination, plan.Snapshot, plan.Projection, authority)
        };
    }

    public static async Task<StockReplayFifoTransferInsertionPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalPlan source,
        StockReplayPhysicalPlan destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ValidateLegs(companyCode, branchCode, source, destination);
        await ValidatePilesAsync(db, companyCode, branchCode, itemCode,
            source.BalLocId, destination.BalLocId, cancellationToken);
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, companyCode, branchCode, itemCode, cancellationToken);
        var projection = Project(snapshot, source);
        var hash = Hash(companyCode, branchCode, itemCode,
            source, destination, snapshot, projection, null);
        return new(companyCode, branchCode, itemCode,
            source, destination, snapshot, projection, hash);
    }

    public static void ValidatePreparedPlan(
        StockReplayFifoTransferInsertionPlan plan)
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
            throw Stale("The prepared FIFO transfer projection changed.");
        }
        if (plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.Source, plan.Destination,
                plan.Snapshot, projected, plan.DraftAuthority)
            || plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.Source, plan.Destination,
                plan.Snapshot, plan.Projection, plan.DraftAuthority))
            throw Stale("The prepared FIFO transfer plan changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayFifoTransferInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.Source.LedgerEpochId
            || context.Posting.PostingSequence
                != plan.Source.Candidate.Key.OriginalPostingSequence
            || context.Posting.EffectiveAt
                != plan.Source.Candidate.Key.EffectiveAt
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.StockTransfer
            || context.Posting.PostingRole != "PRIMARY"
            || plan.DraftAuthority is not { } authority
            || context.Posting.SourceDocumentId
                != authority.BatchId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.SourceDocumentNo
                != authority.BatchNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.DocumentRevision != authority.DocumentRevision)
            throw Stale("The FIFO transfer root differs from its paired plan.");
        var currentDraft = await StockReplayFifoTransferDraftAuthorityReader.ReadAsync(
            context.Db, plan.CompanyCode, plan.BranchCode,
            authority.BatchNo, plan.Source.BalLocId,
            plan.Destination.BalLocId, cancellationToken);
        if (currentDraft != authority)
            throw Stale("The FIFO transfer draft changed after planning.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.Source, cancellationToken);
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.Destination, cancellationToken);
        await ValidatePilesAsync(context.Db, plan.CompanyCode,
            plan.BranchCode, plan.ItemCode, plan.Source.BalLocId,
            plan.Destination.BalLocId, cancellationToken);
        StockReplayFifoActiveLayerSnapshot current;
        try
        {
            current = await StockReplayFifoActiveLayerReader.ReadAsync(
                context.Db, plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The active FIFO layers changed after transfer planning.");
        }
        if (!StockReplayFifoActiveLayerReader.SameSnapshot(
                plan.Snapshot, current))
            throw Stale("The active FIFO layers changed after transfer planning.");
    }

    private static void ValidateLegs(string companyCode,
        string branchCode, StockReplayPhysicalPlan source,
        StockReplayPhysicalPlan destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(source);
        StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(destination);
        if (source.CompanyCode != companyCode
            || source.BranchCode != branchCode
            || destination.CompanyCode != companyCode
            || destination.BranchCode != branchCode
            || source.BalLocId == destination.BalLocId
            || source.LedgerEpochId != destination.LedgerEpochId
            || source.BranchWatermark != destination.BranchWatermark
            || source.Candidate.EventId != destination.Candidate.EventId
            || source.Candidate.Key != destination.Candidate.Key
            || source.Candidate.Key.ClassPriority
                != StockReplayEventKey.OrdinaryClass
            || source.Candidate.AbsoluteCountQty is not null
            || destination.Candidate.AbsoluteCountQty is not null
            || source.Candidate.SignedDelta >= 0m
            || destination.Candidate.SignedDelta != -source.Candidate.SignedDelta)
            throw Invalid("FIFO transfer requires equal and opposite paired physical legs.");
    }

    private static async Task ValidatePilesAsync(AppDbContext db,
        string companyCode, string branchCode, string itemCode,
        int sourceId, int destinationId, CancellationToken cancellationToken)
    {
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && (b.Id == sourceId || b.Id == destinationId))
            .ToArrayAsync(cancellationToken);
        if (piles.Length != 2 || piles.Any(b => b.ICode != itemCode
                || b.LotId is not null
                || !string.IsNullOrWhiteSpace(b.LotNo))
            || piles[0].StdUom != piles[1].StdUom)
            throw Invalid("FIFO transfer needs two existing non-lot piles of one item and UOM.");
    }

    private static StockReplayFifoTailProjection Project(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayPhysicalPlan source)
    {
        var projection = StockReplayFifoTailProjector.InsertTransfer(
            snapshot, source.Candidate.Key, -source.Candidate.SignedDelta);
        var candidate = projection.Issues.SingleOrDefault(i => i.IsCandidate);
        if (candidate is null || candidate.DepletesPool
            || candidate.Quantity != -source.Candidate.SignedDelta
            || projection.FinalQuantity != snapshot.CurrentQuantity
            || projection.FinalValue != snapshot.CurrentValue)
            throw Invalid("The FIFO transfer changed the item pool.");
        return projection;
    }

    private static string Hash(string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalPlan source,
        StockReplayPhysicalPlan destination,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection,
        StockReplayFifoTransferDraftAuthority? draftAuthority) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            sourceHash = source.PlanHash,
            destinationHash = destination.PlanHash,
            snapshot, projection, draftAuthority
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
