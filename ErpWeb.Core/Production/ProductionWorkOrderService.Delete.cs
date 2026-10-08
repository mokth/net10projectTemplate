using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService
{
    private const string HistoricalDeleteBlockerMessage =
        "This Work Order was previously released and cannot be permanently deleted. Cancel it or use the controlled production correction process.";

    private const string ExecutionDeleteBlockerMessage =
        "This Work Order has production execution history and cannot be permanently deleted. Cancel it or use the controlled production correction process.";

    public async Task<IvMasterOperationResult<string>> DeleteDraftAsync(
        ProductionWorkOrderDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDeleteRequest();

        var number = Normalize(request.WorkOrderNo);
        if (number.Length == 0)
        {
            return ValidationFailure<string>("Work Order number is required.", "WorkOrderNo");
        }

        if (request.RowVersion is not { Length: > 0 })
        {
            return IvMasterOperationResult<string>.Fail(
                IvMasterErrorCode.Concurrency,
                "The Work Order version is missing. Reload before deleting.");
        }

        var auth = await AuthorizeAsync(PermissionCodes.Delete, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<string>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // A DR-sourced Work Order follows the shared lock order: DR header -> allocation -> WO.
            // Manual Work Orders have no demand bridge and retain the existing lifecycle path.
            var orderIdentity = await db.ProductionWorkOrders.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderNo == number)
                .Select(x => new { x.Uid })
                .SingleOrDefaultAsync(cancellationToken);
            if (orderIdentity is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            SaDeliveryRequest? deliveryRequest = null;
            PrWorkOrderDemandAllocation? demandAllocation = null;
            var allocationIdentity = await db.PrWorkOrderDemandAllocations.AsNoTracking()
                .Where(x => x.WorkOrderId == orderIdentity.Uid
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode)
                .Select(x => new { x.DeliveryRequestId })
                .SingleOrDefaultAsync(cancellationToken);
            if (allocationIdentity is not null)
            {
                deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
                    db, scope, allocationIdentity.DeliveryRequestId, cancellationToken);
                if (deliveryRequest is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return IvMasterOperationResult<string>.Fail(IvMasterErrorCode.InUse, "The Work Order demand source could not be loaded.");
                }

                demandAllocation = (await LockDeliveryRequestAllocationsForWorkOrderAsync(
                    db, scope, deliveryRequest.Uid, cancellationToken))
                    .SingleOrDefault(x => x.WorkOrderId == orderIdentity.Uid);
            }
            var order = await LockWorkOrderForLifecycleAsync(
                db,
                scope.CompanyCode,
                scope.BranchCode!,
                number,
                cancellationToken);
            if (order is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.NotFound,
                    "Work Order not found.");
            }

            if (!string.Equals(order.CompanyCode, scope.CompanyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(order.BranchCode, scope.BranchCode, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.NotFound,
                    "Work Order not found.");
            }

            if (!ProductionWorkOrderRules.CanDeleteDraft(order.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.Validation,
                    "Only a Draft Work Order can be permanently deleted.");
            }

            if (!order.RowVersion.SequenceEqual(request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.Concurrency,
                    "This Work Order was changed by another user. Reload the latest version before deleting.");
            }

            db.Entry(order).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

            var hasHistoricalRelease = await db.ProductionAuditEvents
                .AsNoTracking()
                .AnyAsync(
                    x => x.WorkOrderId == order.Uid
                        && (x.EventType == ProductionAuditEventTypes.Released
                            || x.EventType == ProductionAuditEventTypes.ReopenedForEdit),
                    cancellationToken);
            if (hasHistoricalRelease)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.InUse,
                    HistoricalDeleteBlockerMessage);
            }

            var blocker = await GetHardDeleteBlockerAsync(db, order, cancellationToken);
            if (blocker is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.InUse,
                    blocker);
            }

            var aggregate = await LoadAggregateAsync(
                db,
                scope,
                order.WorkOrderNo,
                tracking: true,
                cancellationToken);
            if (aggregate is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.NotFound,
                    "Work Order not found.");
            }

            db.Entry(aggregate).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            if (allocationIdentity is not null && demandAllocation is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<string>.Fail(
                    IvMasterErrorCode.InUse,
                    "The Work Order demand allocation could not be locked.");
            }

            if (demandAllocation is not null && deliveryRequest is not null)
            {
                db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
                {
                    DeliveryRequestId = deliveryRequest.Uid,
                    EventType = SaDeliveryRequestAuditEventTypes.AllocationDeleted,
                    WorkOrderId = aggregate.Uid,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        aggregate.WorkOrderNo,
                        demandAllocation.AllocatedQty
                    }),
                    Reason = "Safe never-released Draft Work Order deleted.",
                    OccurredDate = DateTime.UtcNow,
                    ActorUserId = scope.UserId
                });
                db.PrWorkOrderDemandAllocations.Remove(demandAllocation);
            }

            RemoveSnapshotGraph(db, aggregate);
            db.ProductionWorkOrders.Remove(aggregate);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<string>.Ok(number);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<string>.Fail(
                IvMasterErrorCode.Concurrency,
                "This Work Order was changed by another user. Reload the latest version before deleting.");
        }
    }

    private static async Task<string?> GetHardDeleteBlockerAsync(
        AppDbContext db,
        ProductionWorkOrder order,
        CancellationToken cancellationToken)
    {
        if (await HasWorkOrderExecutionProjectionAsync(db, order, cancellationToken))
        {
            return ExecutionDeleteBlockerMessage;
        }

        if (await db.ProductionPostingLinks.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "A production posting link exists for this Work Order. Resolve the posting document before deleting it.";
        }

        if (await db.ProductionMaterialIssueLines.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "A Material Issue line exists for this Work Order. It must be resolved before deletion.";
        }

        if (await db.ProductionMaterialMovements.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return ExecutionDeleteBlockerMessage;
        }

        if (await db.ProductionOutputs.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "A Production Output exists for this Work Order. Production history cannot be deleted.";
        }

        if (await db.ProductionChangeOrders.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "A Change Order exists for this Work Order. Work Order lifecycle history cannot be deleted.";
        }

        if (await db.ProductionBalLots.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "A production balance lot exists for this Work Order. WIP or material history cannot be deleted.";
        }

        // WorkOrderId is historical evidence on this entity but is intentionally not a direct FK.
        if (await db.ProductionBalLotMovements.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "A production balance movement exists for this Work Order. WIP or material history cannot be deleted.";
        }

        if (await db.ProductionFinishedGoodReceiptRows.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "A Finished Good Receipt exists for this Work Order. Finished Good history cannot be deleted.";
        }

        // Lot origin is immutable lineage and is separately restricted from the Work Order.
        if (await db.ProductionFinishedGoodLotOriginRows.AsNoTracking()
                .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken))
        {
            return "Finished Good lot lineage exists for this Work Order. Finished Good history cannot be deleted.";
        }

        return null;
    }
}
