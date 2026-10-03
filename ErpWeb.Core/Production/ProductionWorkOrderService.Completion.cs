using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService
{
    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CompleteAsync(
        ProductionWorkOrderCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.WorkOrderNo) || request.RowVersion.Length == 0)
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation, "Work Order number and current row version are required.");

        var auth = await AuthorizeAsync(ProductionPermissionCodes.Release, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        var scope = auth.Scope!;
        var workOrderNo = request.WorkOrderNo.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var order = await LockWorkOrderForCompletionAsync(db, scope.CompanyCode, scope.BranchCode!, workOrderNo, cancellationToken);
            if (order is null)
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.NotFound, "Work Order was not found.");
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.Validation,
                    "Only Released or In Progress Work Orders can be completed.");
            if (!order.RowVersion.SequenceEqual(request.RowVersion))
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.Concurrency,
                    "This Work Order was changed by another user. Reload before completing it.");
            if (order.RemainingQty > 0.0001m)
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.Validation,
                    $"{order.WorkOrderNo} cannot be completed because {order.RemainingQty:n4} output remains open.");

            var reconciliation = await _materialReconciliation.ReconcileAsync(
                db, scope.CompanyCode, scope.BranchCode!, order.Uid, cancellationToken);
            if (!reconciliation.IsReconciled)
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.Validation,
                    $"{order.WorkOrderNo} cannot be completed. {reconciliation.Findings[0].Message}");

            var now = DateTime.UtcNow;
            var previousStatus = order.Status;
            order.Status = ProductionWorkOrderStatuses.Completed;
            order.ModifiedDate = now;
            order.ModifiedBy = scope.UserId;
            db.ProductionAuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrderId = order.Uid,
                EventType = ProductionAuditEventTypes.Completed,
                FromStatus = previousStatus,
                ToStatus = ProductionWorkOrderStatuses.Completed,
                SnapshotRevision = order.SnapshotRevision,
                Reason = string.IsNullOrWhiteSpace(request.Remark) ? "Work Order material reconciliation completed." : request.Remark.Trim(),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
            TouchSqliteRowVersions(db, order);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var loaded = await LoadAggregateAsync(db, scope, order.WorkOrderNo, tracking: false, cancellationToken);
            return loaded is null
                ? IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.NotFound, "Work Order was not found after completion.")
                : IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(loaded));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.Concurrency,
                "The Work Order changed while it was being completed. Reload and retry.");
        }
    }

    private static async Task<ProductionWorkOrder?> LockWorkOrderForCompletionAsync(
        AppDbContext db, string companyCode, string branchCode, string workOrderNo, CancellationToken cancellationToken) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrders.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={companyCode} AND BranchCode={branchCode} AND WorkOrderNo={workOrderNo}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode && x.WorkOrderNo == workOrderNo, cancellationToken);
}
