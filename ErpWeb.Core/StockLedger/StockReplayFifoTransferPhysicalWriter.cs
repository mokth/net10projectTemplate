using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoTransferPhysicalWriteResult(int HistoryId);

/// <summary>
/// Posts one prepared two-pile transfer inside the caller's unsealed root.
/// It leaves FIFO facts, tail corrections, reconciliation and commit to the
/// subsequent financial writer and caller.
/// </summary>
public static class StockReplayFifoTransferPhysicalWriter
{
    public static async Task<StockReplayFifoTransferPhysicalWriteResult> AppendAsync(
        StockPostingContext context,
        StockReplayFifoTransferInsertionPlan plan, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        await StockReplayFifoTransferInsertionPlanner.ValidateWithinRootAsync(
            context, plan, cancellationToken);
        var authority = plan.DraftAuthority!;
        if (batchNo != authority.BatchNo)
            throw Invalid("The prepared FIFO transfer belongs to another batch.");
        var first = plan.Source.Candidate.Key.EffectiveAt.Date;
        var last = plan.Source.LaterEvents.Select(e => e.Key.EffectiveAt.Date)
            .Concat(plan.Destination.LaterEvents.Select(e => e.Key.EffectiveAt.Date))
            .Append(first).Max();
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, first, last, cancellationToken);
        var db = context.Db;
        var batch = await db.IvTrxBatches.SingleAsync(
            b => b.Id == authority.BatchId, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(
            d => d.Id == authority.DetailId, cancellationToken);
        var balances = await db.IvBalLocs
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && (b.Id == authority.SourceBalLocId
                    || b.Id == authority.DestinationBalLocId))
            .ToArrayAsync(cancellationToken);
        var source = balances.Single(b => b.Id == authority.SourceBalLocId);
        var destination = balances.Single(b => b.Id == authority.DestinationBalLocId);
        if (db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || db.Entry(source).State != EntityState.Unchanged
            || db.Entry(destination).State != EntityState.Unchanged
            || source.StdQty != plan.Source.CurrentQuantity
            || destination.StdQty != plan.Destination.CurrentQuantity
            || !source.RowVersion.SequenceEqual(plan.Source.BalanceRowVersion)
            || !destination.RowVersion.SequenceEqual(
                plan.Destination.BalanceRowVersion)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && (h.BatchNo == batchNo
                    || h.StockPostingId == context.Posting.Id),
                cancellationToken))
            throw Invalid("The FIFO transfer changed before its physical write.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        source.StdQty = plan.Source.Projection.FinalQuantity;
        destination.StdQty = plan.Destination.Projection.FinalQuantity;
        source.TransDate = plan.Source.LaterEvents.Count == 0
            ? authority.EffectiveAt
            : plan.Source.LaterEvents.Max(e => e.Key.EffectiveAt);
        destination.TransDate = plan.Destination.LaterEvents.Count == 0
            ? authority.EffectiveAt
            : plan.Destination.LaterEvents.Max(e => e.Key.EffectiveAt);
        source.ModifiedDate = now;
        destination.ModifiedDate = now;
        detail.ToBalLocId = destination.Id;
        detail.FromLotId = null;
        detail.ToLotId = null;
        detail.PriceEvidence = source.PriceEvidence;
        var history = new IvTrxHistory
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            BatchNo = batchNo,
            TrxLineNo = detail.TrxLineNo,
            TrxType = IvTrxTypes.StockTransfer,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = authority.ItemCode,
            IDesc = detail.IDesc,
            FrWarehouse = source.WhCode,
            FrLocation = source.LocCode,
            FrLotNo = string.Empty,
            FrStdQty = authority.Quantity,
            FrStdUom = authority.BaseUom,
            ToWarehouse = destination.WhCode,
            ToLocation = destination.LocCode,
            ToLotNo = string.Empty,
            ToStdQty = authority.Quantity,
            ToStdUom = authority.BaseUom,
            IStatus = detail.IStatus,
            Remarks = detail.Remarks,
            PriceEvidence = source.PriceEvidence,
            UnitPrice = source.UnitPrice,
            Cost = source.Cost,
            CostPrice = detail.CostPrice,
            BaseUnitPrices = detail.BaseUnitPrices,
            LocationCode = detail.LocationCode,
            FromBalLocId = source.Id,
            ToBalLocId = destination.Id,
            CreatedDate = now,
            CreatedBy = user
        };
        new IvInventoryHistoryWriter().StampGeneration(
            context, [history], authority.DocumentRevision);
        db.IvTrxHistories.Add(history);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = user;
        batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return new(history.Id);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
