using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Applies one prepared exact-pile Inventory issue under a validated replay
/// root. The caller must write its FIFO valuation, descendant corrections and
/// reconciled cost state before sealing; this method never commits the root.
/// </summary>
public static class StockReplayFifoIssuePhysicalWriter
{
    public static async Task<StockReplayFifoBoundIssueCandidate> AppendAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        var physical = plan.PhysicalPlan;
        var candidate = physical.Candidate;
        if (batchNo <= 0 || candidate.SignedDelta >= 0m
            || permit.Operation != "INSERT"
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.PlanHash != plan.PlanHash
            || permit.SnapshotHash != plan.Snapshot.SnapshotHash
            || permit.ItemCode != plan.ItemCode
            || permit.ChangedPileId != physical.BalLocId
            || context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Posting.SourceModule != "INVENTORY"
            || !StockReplayFifoInventoryIssueKinds.IsSupportedFifoRoot(
                context.Posting.SourceDocumentType)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.EffectiveAt != candidate.Key.EffectiveAt
            || context.Posting.PostingSequence != candidate.Key.OriginalPostingSequence)
            throw Invalid("FIFO issue physical write needs its prepared MI root permit.");

        var db = context.Db;
        var batches = await db.IvTrxBatches
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode && b.BatchNo == batchNo)
            .ToArrayAsync(cancellationToken);
        if (batches.Length != 1)
            throw Invalid("The prepared FIFO issue batch is missing or ambiguous.");
        var batch = batches[0];
        if (batch.TrxType != context.Posting.SourceDocumentType
            || batch.BatchStatus != IvBatchStatuses.New
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null
            || batch.TrxDtTime != candidate.Key.EffectiveAt
            || context.Posting.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid("The prepared FIFO issue is no longer an eligible draft.");

        var details = await db.IvTrxBatchDetails
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            throw Invalid("FIFO replay currently supports one MI detail per root.");
        var detail = details[0];
        var quantity = -candidate.SignedDelta;
        if (detail.CompanyCode != context.CompanyCode
            || detail.BranchCode != context.BranchCode
            || detail.BatchNo != batchNo
            || detail.DocumentRevision != context.Posting.DocumentRevision
            || detail.TrxLineNo <= 0
            || detail.TrxType != context.Posting.SourceDocumentType
            || detail.ICode != plan.ItemCode
            || detail.FromBalLocId != physical.BalLocId
            || detail.FrStdQty != quantity
            || detail.ToBalLocId is not null || detail.ToStdQty is not null)
            throw Invalid("The staged MI line differs from its prepared physical event.");
        if (detail.TrxType == IvTrxTypes.VendorReturn
            && (string.IsNullOrWhiteSpace(detail.PoNo)
                || detail.PoRelNo is null || detail.PoLineNo is null))
            throw Invalid("The FIFO Vendor Return has no exact PO line.");
        if (await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && ((h.BatchNo == batchNo
                        && h.DocumentRevision == detail.DocumentRevision)
                    || h.StockPostingId == context.Posting.Id),
                cancellationToken))
            throw Invalid("The MI batch or replay root already has Inventory history.");

        var masters = await db.IvStockMasters.AsNoTracking()
            .Where(m => m.CompanyCode == context.CompanyCode
                && m.ICode == plan.ItemCode).ToArrayAsync(cancellationToken);
        if (masters.Length != 1)
            throw Invalid("FIFO replay MI has no exact stock item.");
        var balances = await db.IvBalLocs
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.Id == physical.BalLocId).ToArrayAsync(cancellationToken);
        if (balances.Length != 1)
            throw Invalid("The prepared FIFO source pile is missing.");
        var balance = balances[0];
        if (!await StockReplayInventoryIssuePileAuthority.MatchesAsync(
                db, masters[0], balance, detail, cancellationToken)
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The FIFO source pile changed after replay preparation.");

        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Count == 0
            ? candidate.Key.EffectiveAt
            : physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.FromLotId = balance.LotId;
        detail.PriceEvidence = balance.PriceEvidence;
        var history = new IvTrxHistory
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            BatchNo = batchNo,
            TrxLineNo = detail.TrxLineNo,
            TrxType = detail.TrxType,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = plan.ItemCode,
            IDesc = detail.IDesc,
            FrWarehouse = detail.FrWarehouse,
            FrLocation = detail.FrLocation,
            FrLotNo = detail.FrLotNo,
            FrStdQty = detail.FrStdQty,
            FrStdUom = detail.FrStdUom,
            FrPurQty = detail.FrPurQty,
            FrPurUom = detail.FrPurUom,
            PoNo = detail.TrxType == IvTrxTypes.VendorReturn ? detail.PoNo : null,
            PoRelNo = detail.TrxType == IvTrxTypes.VendorReturn ? detail.PoRelNo : null,
            PoLineNo = detail.TrxType == IvTrxTypes.VendorReturn ? detail.PoLineNo : null,
            IStatus = detail.IStatus,
            Remarks = detail.Remarks,
            PriceEvidence = detail.PriceEvidence,
            CostEvidenceType = detail.CostEvidenceType,
            CostOverrideReason = detail.CostOverrideReason,
            CostApprovedBy = detail.CostApprovedBy,
            CostApprovedAtUtc = detail.CostApprovedAtUtc,
            UnitPrice = detail.UnitPrice,
            Cost = detail.Cost,
            CostPrice = detail.CostPrice,
            BaseUnitPrices = detail.BaseUnitPrices,
            LocationCode = detail.LocationCode,
            FromBalLocId = balance.Id,
            FromLotId = balance.LotId,
            CreatedDate = now,
            CreatedBy = user
        };
        new IvInventoryHistoryWriter().StampGeneration(
            context, [history], context.Posting.DocumentRevision);
        db.IvTrxHistories.Add(history);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = user;
        batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return await StockReplayFifoIssueCandidateBinder.BindAsync(
            context, plan, permit, history.Id, cancellationToken);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
