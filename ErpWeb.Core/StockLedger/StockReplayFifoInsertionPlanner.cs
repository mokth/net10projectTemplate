using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoInsertionPlan(
    string CompanyCode, string BranchCode, string ItemCode,
    StockReplayPhysicalPlan PhysicalPlan,
    StockReplayFifoActiveLayerSnapshot Snapshot,
    StockReplayFifoTailProjection Projection,
    decimal? ReceiptValue, string? ReceiptBaseUom,
    string PlanHash);

/// <summary>
/// Captures an ordinary FIFO physical/financial insertion before the branch
/// lock and rechecks the same layer evidence under the root lock. This is
/// read-only and does not authorize a correction write or public posting.
/// </summary>
public static class StockReplayFifoInsertionPlanner
{
    public static async Task<StockReplayFifoInsertionPlan> BuildMiscReceiptAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, StockReplayPhysicalPlan physicalPlan,
        CancellationToken cancellationToken = default,
        bool goodsReceipt = false, bool allowInvoiced = false)
    {
        ArgumentNullException.ThrowIfNull(physicalPlan);
        var authority = goodsReceipt
            ? await StockReplayGrReceiptAuthorityReader.ReadAsync(
                db, companyCode, branchCode, batchNo, physicalPlan.BalLocId,
                cancellationToken, allowInvoiced)
            : await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
                db, companyCode, branchCode, batchNo, physicalPlan.BalLocId,
                cancellationToken, allowExistingLot: true);
        if (physicalPlan.Candidate.SignedDelta != authority.Quantity
            || physicalPlan.Candidate.Key.EffectiveAt != authority.EffectiveAt)
            throw Invalid("The physical MR candidate differs from its approved cost line.");
        return await BuildAsync(db, companyCode, branchCode,
            authority.ItemCode, physicalPlan, authority.Value,
            authority.BaseUom, cancellationToken);
    }

    public static async Task<StockReplayFifoWritePermit>
        PrepareMiscReceiptWithinRootAsync(
            StockPostingContext context, StockReplayFifoInsertionPlan plan,
            int batchNo, CancellationToken cancellationToken = default,
            bool goodsReceipt = false, bool allowInvoiced = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        var authority = goodsReceipt
            ? await StockReplayGrReceiptAuthorityReader.ReadAsync(
                context.Db, context.CompanyCode, context.BranchCode,
                batchNo, plan.PhysicalPlan.BalLocId, cancellationToken,
                allowInvoiced)
            : await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
                context.Db, context.CompanyCode, context.BranchCode,
                batchNo, plan.PhysicalPlan.BalLocId, cancellationToken,
                allowExistingLot: true);
        if (context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != (goodsReceipt
                ? IvTrxTypes.GoodsReceive : IvTrxTypes.MiscellaneousReceipt)
            || context.Posting.PostingRole != "PRIMARY"
            || context.Posting.SourceDocumentId != authority.BatchId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.DocumentRevision != authority.DocumentRevision
            || plan.ItemCode != authority.ItemCode
            || plan.PhysicalPlan.Candidate.SignedDelta != authority.Quantity
            || plan.PhysicalPlan.Candidate.Key.EffectiveAt != authority.EffectiveAt
            || plan.ReceiptValue != authority.Value
            || plan.ReceiptBaseUom != authority.BaseUom)
            throw Invalid("The prepared FIFO MR root lost its approved cost authority.");
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, plan, cancellationToken);
        return await PrepareWithinRootAsync(context, plan, cancellationToken);
    }

    public static async Task<StockReplayFifoWritePermit> PrepareWithinRootAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        await ValidateWithinRootAsync(context, plan, cancellationToken);
        return new StockReplayFifoWritePermit(context, plan);
    }

    public static async Task<StockReplayFifoInsertionPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalPlan physicalPlan,
        decimal? receiptValue = null, string? receiptBaseUom = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(physicalPlan);
        ValidateCandidate(companyCode, branchCode, physicalPlan,
            receiptValue, receiptBaseUom);
        var pileItem = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && b.Id == physicalPlan.BalLocId)
            .Select(b => b.ICode)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileItem != itemCode)
            throw Invalid("The FIFO candidate pile belongs to another item.");
        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, companyCode, branchCode, itemCode, cancellationToken);
        var projection = Project(snapshot, physicalPlan,
            receiptValue, receiptBaseUom);
        var hash = Hash(companyCode, branchCode, itemCode,
            physicalPlan, snapshot, projection,
            receiptValue, receiptBaseUom);
        return new(companyCode, branchCode, itemCode,
            physicalPlan, snapshot, projection,
            receiptValue, receiptBaseUom, hash);
    }

    public static void ValidatePreparedPlan(StockReplayFifoInsertionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        StockReplayPhysicalPlanner.ValidatePreparedInsertionPlan(
            plan.PhysicalPlan);
        ValidateCandidate(plan.CompanyCode, plan.BranchCode,
            plan.PhysicalPlan, plan.ReceiptValue, plan.ReceiptBaseUom);
        StockReplayFifoTailProjection projected;
        try
        {
            projected = Project(plan.Snapshot, plan.PhysicalPlan,
                plan.ReceiptValue, plan.ReceiptBaseUom);
        }
        catch (StockLedgerException)
        {
            throw Stale("The prepared FIFO insertion plan changed.");
        }
        if (plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.PhysicalPlan, plan.Snapshot,
                projected, plan.ReceiptValue, plan.ReceiptBaseUom)
            || plan.PlanHash != Hash(plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, plan.PhysicalPlan, plan.Snapshot,
                plan.Projection, plan.ReceiptValue, plan.ReceiptBaseUom))
            throw Stale("The prepared FIFO insertion plan changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        ValidatePreparedPlan(plan);
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.PhysicalPlan.LedgerEpochId
            || context.Posting.PostingSequence
                != plan.PhysicalPlan.Candidate.Key.OriginalPostingSequence)
            throw Stale("The FIFO root differs from its prepared insertion.");
        await StockReplayPhysicalPlanner.ValidateWithinRootAsync(
            context, plan.PhysicalPlan, cancellationToken);
        var pileItem = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && b.Id == plan.PhysicalPlan.BalLocId)
            .Select(b => b.ICode)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileItem != plan.ItemCode)
            throw Stale("The FIFO candidate pile's item changed.");
        StockReplayFifoActiveLayerSnapshot current;
        try
        {
            current = await StockReplayFifoActiveLayerReader.ReadAsync(
                context.Db, plan.CompanyCode, plan.BranchCode,
                plan.ItemCode, cancellationToken);
        }
        catch (StockLedgerException)
        {
            throw Stale("The active FIFO layers changed after planning.");
        }
        if (!StockReplayFifoActiveLayerReader.SameSnapshot(
                plan.Snapshot, current))
            throw Stale("The active FIFO layers changed after planning.");
    }

    private static StockReplayFifoTailProjection Project(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayPhysicalPlan physicalPlan,
        decimal? receiptValue, string? receiptBaseUom)
    {
        var delta = physicalPlan.Candidate.SignedDelta;
        var projection = delta < 0m
            ? StockReplayFifoTailProjector.InsertIssue(snapshot,
                physicalPlan.Candidate.Key, -delta)
            : StockReplayFifoTailProjector.InsertReceipt(snapshot,
                physicalPlan.Candidate.Key, delta,
                receiptValue!.Value, receiptBaseUom!);
        if (projection.FinalQuantity != StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity + delta))
            throw Invalid("The FIFO and physical insertion quantities differ.");
        return projection;
    }

    private static void ValidateCandidate(string companyCode,
        string branchCode, StockReplayPhysicalPlan physicalPlan,
        decimal? receiptValue, string? receiptBaseUom)
    {
        var candidate = physicalPlan.Candidate;
        if (physicalPlan.CompanyCode != companyCode
            || physicalPlan.BranchCode != branchCode
            || candidate.Key.ClassPriority != StockReplayEventKey.OrdinaryClass
            || candidate.AbsoluteCountQty is not null
            || candidate.SignedDelta == 0m
            || StockLedgerPrecision.Quantity(candidate.SignedDelta)
                != candidate.SignedDelta
            || candidate.SignedDelta > 0m
                && (receiptValue is null
                    || string.IsNullOrWhiteSpace(receiptBaseUom))
            || candidate.SignedDelta < 0m
                && (receiptValue is not null || receiptBaseUom is not null))
            throw Invalid("The FIFO candidate needs one ordinary physical leg and matching cost input.");
    }

    private static string Hash(string companyCode, string branchCode,
        string itemCode, StockReplayPhysicalPlan physicalPlan,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection,
        decimal? receiptValue, string? receiptBaseUom) =>
        StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            physicalPlan.PlanHash,
            snapshot,
            projection,
            receiptValue, receiptBaseUom
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
