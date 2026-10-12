using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionStockReturnService
{
    public async Task<IvMasterOperationResult<bool>> DeleteAsync(int id, byte[] expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Delete, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            db.ProductionStockReturnWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var r = await LoadAsync(db, scope, id, true, ct); Version(r, expectedVersion);
            if (r.DeletedAtUtc is not null || r.Batch.DeletedAtUtc is not null)
            {
                await tx.CommitAsync(ct);
                return IvMasterOperationResult<bool>.Ok(true);
            }
            if (r.Batch.BatchStatus == IvBatchStatuses.Posted)
                throw new ReturnException("Roll back the posted Production Stock Return before archiving it.", IvMasterErrorCode.InUse);
            if (r.Batch.BatchStatus == IvBatchStatuses.New && r.PostingId is null)
            {
                db.ProductionStockReturnLines.RemoveRange(r.Lines);
                db.IvTrxBatchDetails.RemoveRange(r.Lines.Select(x => x.Detail));
                db.ProductionStockReturns.Remove(r); db.IvTrxBatches.Remove(r.Batch);
            }
            else if (r.Batch.BatchStatus == "REVERSED" && r.ReversalPostingId is not null)
            {
                r.DeletedAtUtc = clock.Now; r.DeletedBy = User(scope); r.DeleteReason = "Historical Production Stock Return archived.";
                r.Batch.DeletedAtUtc = r.DeletedAtUtc; r.Batch.DeletedBy = r.DeletedBy; r.Batch.DeleteReason = r.DeleteReason;
            }
            else throw new ReturnException("This Production Stock Return cannot be deleted in its current state.", IvMasterErrorCode.InUse);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return IvMasterOperationResult<bool>.Ok(true);
        }
        catch (ReturnException e) { return Fail<bool>(e.Message, e.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<bool>("The return changed. Reload before deleting.", IvMasterErrorCode.Concurrency); }
    }

    public async Task<IvMasterOperationResult<ProductionStockReturnDocument>> CreateCorrectionAsync(int id, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Add, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var original = await LoadAsync(db, scope, id, false, ct);
            if (original.Batch.BatchStatus != "REVERSED" || original.ReversalPostingId is null)
                throw new ReturnException("Complete rollback before creating a correction.");
            var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == original.WorkOrderId, ct);
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                throw new ReturnException("The Work Order must be active before creating a replacement return.");
            return await SaveCoreAsync(new ProductionStockReturnSaveRequest
            {
                WorkOrderId = order.Uid, SnapshotRevision = order.SnapshotRevision, SnapshotHash = order.SnapshotHash,
                EffectiveDate = clock.Now, RefNo = original.Batch.RefNo, Remarks = original.Batch.Remarks,
                Lines = original.Lines.Select(x => new ProductionStockReturnLineInput
                { ProductionBalLotId = x.ProductionBalLotId, Quantity = x.RequestedQty,
                    Reason = x.Detail.Reason ?? ProductionStockReturnReasons.Excess, Remarks = x.Detail.Remarks }).ToList()
            }, id, ct);
        }
        catch (ReturnException e) { return Fail<ProductionStockReturnDocument>(e.Message, e.Code); }
    }
}
