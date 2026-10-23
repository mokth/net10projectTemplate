using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Appends one original MR's physical reversal inside its replay root.</summary>
public static class StockReplayFifoMrReceiptRemovalPhysicalWriter
{
    public static async Task<int> AppendAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        var physical = plan.PhysicalPlan;
        var removed = physical.RemovedActiveEvent;
        var receipt = plan.Projection.CandidateReceipt;
        if (batchNo <= 0 || removed.SignedDelta <= 0m
            || receipt is null || receipt.ReceiptKey != removed.Key
            || receipt.OriginalQuantity != removed.SignedDelta
            || permit.Operation != "REMOVE"
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != plan.ItemCode
            || permit.PlanHash != plan.PlanHash
            || permit.SnapshotHash != plan.Snapshot.SnapshotHash
            || permit.RemovedHistoryId != removed.EventId
            || permit.ChangedPileId != physical.BalLocId
            || permit.ChangedPileFinalQuantity != physical.Projection.FinalQuantity
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "REVERSAL"
            || context.Posting.EffectiveAt != removed.Key.EffectiveAt)
            throw Invalid("FIFO MR removal needs its prepared receipt rollback root.");
        var db = context.Db;
        var batch = await db.IvTrxBatches.SingleOrDefaultAsync(b =>
            b.CompanyCode == context.CompanyCode
            && b.BranchCode == context.BranchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != IvTrxTypes.MiscellaneousReceipt
            || batch.BatchStatus != IvBatchStatuses.Posted
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null
            || batch.TrxDtTime != removed.Key.EffectiveAt
            || context.Posting.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid("The removed MR is no longer an eligible posted batch.");
        var details = await db.IvTrxBatchDetails
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        var histories = await db.IvTrxHistories
            .Where(h => h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && h.BatchNo == batchNo
                && h.DocumentRevision == context.Posting.DocumentRevision)
            .ToArrayAsync(cancellationToken);
        if (details.Length != 1 || histories.Length != 1
            || histories[0].Id != permit.RemovedHistoryId)
            throw Invalid("FIFO MR rollback needs one unchanged original line.");
        var detail = details[0];
        var history = histories[0];
        db.ChangeTracker.DetectChanges();
        if (db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || db.Entry(history).State != EntityState.Unchanged
            || history.EntryRole != "ORIGINAL" || history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.TrxType != IvTrxTypes.MiscellaneousReceipt
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.TrxDtTime != removed.Key.EffectiveAt
            || history.ICode != plan.ItemCode
            || history.ToBalLocId != physical.BalLocId
            || history.ToStdQty != removed.SignedDelta
            || history.FromBalLocId is not null || history.FrStdQty is not null
            || detail.DocumentRevision != context.Posting.DocumentRevision
            || detail.TrxLineNo != history.TrxLineNo
            || detail.ICode != plan.ItemCode
            || detail.ToBalLocId != physical.BalLocId
            || detail.ToStdQty != history.ToStdQty
            || detail.ToStdUom != history.ToStdUom
            || detail.ToWarehouse != history.ToWarehouse
            || detail.ToLocation != history.ToLocation
            || detail.IStatus != history.IStatus
            || detail.CostEvidenceType != history.CostEvidenceType
            || detail.PriceEvidence != history.PriceEvidence
            || detail.CostOverrideReason != history.CostOverrideReason
            || detail.CostApprovedBy != history.CostApprovedBy
            || detail.CostApprovedAtUtc != history.CostApprovedAtUtc
            || detail.UnitPrice != history.UnitPrice
            || string.IsNullOrWhiteSpace(detail.Reason)
            || detail.Cost is not null || detail.CostPrice is not null
            || detail.BaseUnitPrices is not null)
            throw Invalid("The posted MR detail differs from its receipt origin.");
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == history.StockPostingId,
                cancellationToken);
        if (owner is null || owner.SealedAtUtc is null
            || owner.PostingSequence != removed.Key.OriginalPostingSequence
            || context.Posting.ReversesPostingId != owner.Id)
            throw Invalid("The MR rollback root does not reverse its original owner.");
        var master = await db.IvStockMasters.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CompanyCode == context.CompanyCode
                && m.ICode == plan.ItemCode, cancellationToken);
        if (master is null || !master.IsActive
            || !master.StockControl || master.LotControl)
            throw Invalid("FIFO MR rollback needs an active non-lot item.");
        var balance = await db.IvBalLocs.SingleOrDefaultAsync(b =>
            b.CompanyCode == context.CompanyCode
            && b.BranchCode == context.BranchCode
            && b.Id == physical.BalLocId, cancellationToken);
        if (balance is null || balance.ICode != plan.ItemCode
            || balance.LotId is not null || !string.IsNullOrEmpty(balance.LotNo)
            || balance.WhCode != detail.ToWarehouse
            || balance.LocCode != (detail.ToLocation ?? string.Empty)
            || balance.IStatus != detail.IStatus
            || balance.StdUom != detail.ToStdUom
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The MR destination pile changed after preparation.");
        var timeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, context.CompanyCode, context.BranchCode, physical.BalLocId,
            new StockReplayEventKey(new DateTime(1753, 1, 1), 0, 0L, "", 0),
            cancellationToken);
        if (timeline.Count(e => e.EventId == removed.EventId) != 1)
            throw Invalid("The removed MR is no longer active in its pile.");
        var latest = timeline.Where(e => e.EventId != removed.EventId)
            .Select(e => (DateTime?)e.Key.EffectiveAt).Max();
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = latest ?? (balance.StdQty > 0m
            ? balance.TransDate : null);
        balance.ModifiedDate = now;
        var reversal = new IvInventoryHistoryWriter().AppendReversal(
            context, [history], context.Posting.DocumentRevision).Single();
        detail.ToBalLocId = null;
        detail.ToLotId = null;
        detail.DocumentRevision = checked(detail.DocumentRevision + 1);
        batch.BatchStatus = IvBatchStatuses.New;
        batch.RollbackDate = now;
        batch.RollbackBy = user;
        batch.RollbackCount += 1;
        batch.RollbackOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return reversal.Id;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
