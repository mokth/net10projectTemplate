using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Stages one approved non-lot MR receipt in an existing pile under its
/// prepared FIFO root. The caller must append the valued receipt and layer,
/// correct later issues, reconcile state and seal before committing.
/// </summary>
public static class StockReplayFifoMrReceiptPhysicalWriter
{
    public static async Task<int> AppendAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        var prepared = await StockReplayFifoInsertionPlanner
            .PrepareMiscReceiptWithinRootAsync(context, plan, batchNo,
                cancellationToken);
        if (permit.Operation != "INSERT"
            || permit.RootPostingId != prepared.RootPostingId
            || permit.RootPostingSequence != prepared.RootPostingSequence
            || permit.LedgerEpochId != prepared.LedgerEpochId
            || permit.PlanHash != prepared.PlanHash
            || permit.SnapshotHash != prepared.SnapshotHash
            || permit.CompanyCode != prepared.CompanyCode
            || permit.BranchCode != prepared.BranchCode
            || permit.ItemCode != prepared.ItemCode
            || permit.CurrentQuantity != prepared.CurrentQuantity
            || permit.CurrentValue != prepared.CurrentValue
            || permit.FinalQuantity != prepared.FinalQuantity
            || permit.FinalValue != prepared.FinalValue
            || permit.ChangedPileId != prepared.ChangedPileId
            || permit.ChangedPileFinalQuantity
                != prepared.ChangedPileFinalQuantity)
            throw Invalid("FIFO MR physical write needs its prepared receipt permit.");
        var physical = plan.PhysicalPlan;
        var candidate = physical.Candidate;
        var authority = await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
            context.Db, context.CompanyCode, context.BranchCode,
            batchNo, physical.BalLocId, cancellationToken);
        if (candidate.SignedDelta != authority.Quantity
            || candidate.Key.EffectiveAt != authority.EffectiveAt
            || plan.ReceiptValue != authority.Value
            || plan.ReceiptBaseUom != authority.BaseUom)
            throw Invalid("The approved MR cost differs from the prepared receipt.");

        var db = context.Db;
        var batch = await db.IvTrxBatches.SingleAsync(
            b => b.Id == authority.BatchId, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(
            d => d.Id == authority.DetailId, cancellationToken);
        db.ChangeTracker.DetectChanges();
        if (batch.BatchStatus != IvBatchStatuses.New
            || detail.DocumentRevision != context.Posting.DocumentRevision
            || db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && (h.BatchNo == batchNo
                    || h.StockPostingId == context.Posting.Id),
                cancellationToken))
            throw Invalid("The FIFO MR draft or root already has Inventory history.");
        var balance = await db.IvBalLocs.SingleAsync(
            b => b.Id == physical.BalLocId
                && b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode,
            cancellationToken);
        if (balance.ICode != authority.ItemCode
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The FIFO MR pile changed after preparation.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        if (balance.StdQty == 0m)
        {
            balance.UnitPrice = authority.UnitCost;
            balance.Cost = authority.UnitCost;
            balance.PriceEvidence = "EXPLICIT_COMPANY_BASE_PRICE";
        }
        else if (balance.UnitPrice != authority.UnitCost)
            balance.PriceEvidence = null;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Count == 0
            ? candidate.Key.EffectiveAt
            : physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.ToBalLocId = balance.Id;
        detail.ToLotId = null;
        var history = new IvTrxHistory
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            BatchNo = batchNo,
            TrxLineNo = detail.TrxLineNo,
            TrxType = IvTrxTypes.MiscellaneousReceipt,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = authority.ItemCode,
            IDesc = detail.IDesc,
            ToWarehouse = detail.ToWarehouse,
            ToLocation = detail.ToLocation,
            ToLotNo = detail.ToLotNo,
            ToStdQty = detail.ToStdQty,
            ToStdUom = detail.ToStdUom,
            ToPurQty = detail.ToPurQty,
            ToPurUom = detail.ToPurUom,
            IStatus = detail.IStatus,
            DoNo = detail.DoNo,
            PoNo = detail.PoNo,
            PoRelNo = detail.PoRelNo,
            PoLineNo = detail.PoLineNo,
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
            ToBalLocId = balance.Id,
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
        return history.Id;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
