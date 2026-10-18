using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacSingleCountRemovalResult(
    int ReversalHistoryId, long RemovalGenerationId);

/// <summary>
/// Rolls back one WAC count adjustment when it has no later item event. Later
/// descendants and multi-line sheets require a shared financial replay writer.
/// </summary>
public static class StockReplayWacSingleCountRemovalRootWriter
{
    public static async Task<StockReplayWacSingleCountRemovalResult> AppendWithinRootAsync(
        StockPostingContext context,
        StockReplayWacSingleCountRemovalPlan plan,
        IRunningNumberService numbers, string reason, DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 250)
            throw Invalid("Count rollback requires a reason within the audit limit.");
        var sheet = plan.Sheet;
        var financial = plan.Financial;
        if (sheet.Lines.Count != 1
            || sheet.Lines[0].Physical.LaterEvents.Count != 0
            || financial.Tail.LaterEvents.Count != 0
            || financial.RemovedActiveEvent.OldSignedQuantity == 0m
            || context.CostMethod != StockCostMethods.MovingAverage)
            throw Invalid("Count rollback needs a single nonzero WAC adjustment with no descendants.");
        await StockReplayWacSingleCountRemovalPlanner.ValidateWithinRootAsync(
            context, plan, cancellationToken);
        var permit = await StockReplayWacRemovalPlanner.PrepareWithinRootAsync(
            context, financial, cancellationToken);
        var physical = sheet.Lines[0].Physical;
        var lineId = sheet.Lines[0].CountLineId;
        var db = context.Db;
        var active = (await StockCountGenerationResolver.ResolveActiveAsync(
            db, context.CompanyCode, context.BranchCode, [lineId],
            cancellationToken))[lineId];
        if (active.IsRemoval || active.Id != -physical.RemovedActiveEvent.EventId
            || active.AnchorPostingId != sheet.AnchorPostingId
            || active.WritePostingId != sheet.AnchorPostingId
            || active.SignedAdjustmentQty
                != financial.RemovedActiveEvent.OldSignedQuantity
            || active.AdjustmentHistoryId is not int originalHistoryId)
            throw Invalid("The planned count adjustment is no longer the original active generation.");
        var original = await db.IvTrxHistories.AsNoTracking().SingleAsync(h =>
            h.Id == originalHistoryId, cancellationToken);
        var activeFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, [original],
            cancellationToken))[originalHistoryId];
        if (activeFacts.Count != 1
            || activeFacts[0].StockPostingId != sheet.AnchorPostingId
            || activeFacts[0].SignedQty != active.SignedAdjustmentQty
            || activeFacts[0].SignedValue
                != financial.RemovedActiveEvent.OldSignedValue
            || original.StockPostingId != sheet.AnchorPostingId
            || original.BatchNo != active.AdjustmentBatchNo
            || original.EntryRole != "ORIGINAL"
            || original.TrxType != IvTrxTypes.StockAdjustment
            || original.BatchStatus != IvBatchStatuses.Posted
            || original.ValuationStatus != StockValuationStatuses.Valued)
            throw Invalid("The count has no exact active WAC adjustment fact.");
        var header = await db.IvStockCountHdrs.SingleAsync(h =>
            h.Id == sheet.CountHeaderId
            && h.CompanyCode == context.CompanyCode
            && h.BranchCode == context.BranchCode, cancellationToken);
        if (header.Status != IvStockCountStatuses.Posted
            || header.PostedBatchNo != original.BatchNo
            || !header.RowVersion.SequenceEqual(sheet.HeaderRowVersion))
            throw Stale("The count header changed before reversal.");
        var originalBatch = await db.IvTrxBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.BatchNo == original.BatchNo, cancellationToken);
        if (originalBatch is null
            || originalBatch.TrxType != IvTrxTypes.StockAdjustment
            || originalBatch.BatchStatus != IvBatchStatuses.Posted
            || originalBatch.TrxDtTime != header.CountDate.Date
            || originalBatch.RefNo != header.CountNo)
            throw Invalid("The original count ADJ batch changed before reversal.");
        var originalDetails = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == originalBatch.Id)
            .ToArrayAsync(cancellationToken);
        var originalBatchHistoryIds = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && h.BatchNo == original.BatchNo)
            .Select(h => h.Id).ToArrayAsync(cancellationToken);
        if (originalDetails.Length != 1
            || originalBatchHistoryIds.Length != 1
            || originalBatchHistoryIds[0] != original.Id
            || originalDetails[0].CompanyCode != context.CompanyCode
            || originalDetails[0].BranchCode != context.BranchCode
            || originalDetails[0].BatchNo != original.BatchNo
            || originalDetails[0].DocumentRevision != original.DocumentRevision
            || originalDetails[0].TrxLineNo != original.TrxLineNo
            || originalDetails[0].TrxType != original.TrxType
            || originalDetails[0].ICode != original.ICode
            || originalDetails[0].FromBalLocId != original.FromBalLocId
            || originalDetails[0].ToBalLocId != original.ToBalLocId
            || originalDetails[0].FromLotId != original.FromLotId
            || originalDetails[0].ToLotId != original.ToLotId
            || originalDetails[0].FrWarehouse != original.FrWarehouse
            || originalDetails[0].ToWarehouse != original.ToWarehouse
            || originalDetails[0].FrLocation != original.FrLocation
            || originalDetails[0].ToLocation != original.ToLocation
            || originalDetails[0].FrLotNo != original.FrLotNo
            || originalDetails[0].ToLotNo != original.ToLotNo
            || originalDetails[0].FrStdQty != original.FrStdQty
            || originalDetails[0].ToStdQty != original.ToStdQty
            || originalDetails[0].FrStdUom != original.FrStdUom
            || originalDetails[0].ToStdUom != original.ToStdUom
            || originalDetails[0].FrPurQty != original.FrPurQty
            || originalDetails[0].ToPurQty != original.ToPurQty
            || originalDetails[0].FrPurUom != original.FrPurUom
            || originalDetails[0].ToPurUom != original.ToPurUom
            || originalDetails[0].IStatus != original.IStatus
            || originalDetails[0].StockCountCostBasisId
                != original.StockCountCostBasisId
            || originalDetails[0].CostEvidenceType != original.CostEvidenceType
            || originalDetails[0].CostOverrideReason != original.CostOverrideReason
            || originalDetails[0].CostApprovedBy != original.CostApprovedBy
            || originalDetails[0].CostApprovedAtUtc != original.CostApprovedAtUtc
            || originalDetails[0].UnitPrice != original.UnitPrice)
            throw Invalid("The original count ADJ detail changed before reversal.");
        var pile = await db.IvBalLocs.SingleAsync(b =>
            b.Id == physical.BalLocId
            && b.CompanyCode == context.CompanyCode
            && b.BranchCode == context.BranchCode, cancellationToken);
        if (pile.StdQty != physical.CurrentQuantity
            || !pile.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Stale("The counted pile changed before reversal.");
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries<IvStockCountHdr>().Any(e =>
                e.Entity.Id == header.Id && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvStockCountLine>().Any(e =>
                e.Entity.Id == lineId && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxBatch>().Any(e =>
                e.Entity.Id == originalBatch.Id
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxBatchDetail>().Any(e =>
                e.Entity.BatchId == originalBatch.Id
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxHistory>().Any(e =>
                e.Entity.Id == original.Id && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvBalLoc>().Any(e =>
                e.Entity.Id == pile.Id && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<StockCostState>().Any(e =>
                e.Entity.CompanyCode == context.CompanyCode
                && e.Entity.BranchCode == context.BranchCode
                && e.Entity.ItemCode == financial.ItemCode
                && e.State != EntityState.Unchanged))
            throw Invalid("The count or its valuation authority has pending edits.");

        var batchNo = await numbers.GetNextAsync(db, context.CompanyCode,
            RunningNumberKeys.IvBatch, cancellationToken);
        if (await db.IvTrxBatches.AsNoTracking().AnyAsync(b =>
                b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.BatchNo == batchNo, cancellationToken))
            throw Invalid("The count reversal batch number is already used.");
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        var batch = new IvTrxBatch
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            BatchNo = batchNo,
            TrxDtTime = header.CountDate.Date,
            TrxType = IvTrxTypes.StockAdjustment,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = header.CountNo,
            PostedDate = now,
            PostedBy = user,
            PostedCount = 1,
            CreatedDate = now,
            CreatedBy = user
        };
        db.IvTrxBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        var reversal = new IvInventoryHistoryWriter().AppendCountReversal(
            context, [original], context.Posting.DocumentRevision,
            header.CountDate, batchNo, 1).Single();
        reversal.ValuationStatus = StockValuationStatuses.Unvalued;
        db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
        {
            BatchId = batch.Id,
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            BatchNo = batchNo,
            DocumentRevision = context.Posting.DocumentRevision,
            TrxLineNo = 1,
            TrxType = IvTrxTypes.StockAdjustment,
            ICode = reversal.ICode,
            IDesc = reversal.IDesc,
            FromBalLocId = reversal.FromBalLocId,
            ToBalLocId = reversal.ToBalLocId,
            FromLotId = reversal.FromLotId,
            ToLotId = reversal.ToLotId,
            FrWarehouse = reversal.FrWarehouse,
            ToWarehouse = reversal.ToWarehouse,
            FrLocation = reversal.FrLocation,
            ToLocation = reversal.ToLocation,
            FrLotNo = reversal.FrLotNo,
            ToLotNo = reversal.ToLotNo,
            FrStdQty = reversal.FrStdQty,
            ToStdQty = reversal.ToStdQty,
            FrStdUom = reversal.FrStdUom,
            ToStdUom = reversal.ToStdUom,
            FrPurQty = reversal.FrPurQty,
            ToPurQty = reversal.ToPurQty,
            FrPurUom = reversal.FrPurUom,
            ToPurUom = reversal.ToPurUom,
            IStatus = reversal.IStatus,
            StockCountCostBasisId = reversal.StockCountCostBasisId,
            CostEvidenceType = reversal.CostEvidenceType,
            CostOverrideReason = reversal.CostOverrideReason,
            CostApprovedBy = reversal.CostApprovedBy,
            CostApprovedAtUtc = reversal.CostApprovedAtUtc,
            UnitPrice = reversal.UnitPrice
        });
        await db.SaveChangesAsync(cancellationToken);
        await StockReplayValuationReversalWriter.AppendAsync(context,
            originalHistoryId,
            [new StockReplayValuationReversalLine(activeFacts[0].Id,
                1, 0, reversal.Id)], cancellationToken);
        reversal.ValuationStatus = StockValuationStatuses.Reversed;

        pile.StdQty = physical.Projection.FinalQuantity;
        pile.ModifiedDate = now;
        var timeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, context.CompanyCode, context.BranchCode, pile.Id,
            new StockReplayEventKey(new DateTime(1753, 1, 1), 0, 0, "", 0),
            cancellationToken);
        var latest = timeline.Where(e => e.EventId != physical.RemovedActiveEvent.EventId)
            .Select(e => (DateTime?)e.Key.EffectiveAt).Max();
        pile.TransDate = latest ?? (pile.StdQty > 0m ? pile.TransDate : null);
        var removal = new StockCountReplayGeneration
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            StockCountLineId = lineId,
            AnchorPostingId = active.AnchorPostingId,
            AnchorPostingSequence = active.AnchorPostingSequence,
            WritePostingId = context.Posting.Id,
            PreviousGenerationId = active.Id,
            EffectiveAt = active.EffectiveAt,
            SignedAdjustmentQty = 0m,
            ReversalBatchNo = batchNo,
            ReversalHistoryId = reversal.Id,
            IsRemoval = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.StockCountReplayGenerations.Add(removal);
        header.Status = IvStockCountStatuses.RolledBack;
        header.RolledBackBy = user;
        header.RolledBackOn = now;
        header.RollbackReason = reason.Trim();
        header.ModifiedBy = user;
        header.ModifiedDate = now;
        await StockReplayWacCostStateWriter.ApplyAsync(context, permit,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new(reversal.Id, removal.Id);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
