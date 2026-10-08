using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService
{
    public async Task<IvMasterOperationResult<ProductionWorkOrderPreview>> PreviewFromDeliveryRequestAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDeliveryRequestRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var prepared = await BuildDeliveryRequestSnapshotAsync(db, auth.Scope!, request, cancellationToken);
            if (prepared.Error is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(IvMasterErrorCode.Validation, prepared.Error);
            }

            return IvMasterOperationResult<ProductionWorkOrderPreview>.Ok(
                MapDeliveryRequestPreview(prepared));
        }
        catch (WorkOrderCommandException ex)
        {
            return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(ex.Code, ex.Message);
        }
    }

    public Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateDraftFromDeliveryRequestAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken = default) =>
        CreateFromDeliveryRequestCoreAsync(request, release: false, cancellationToken);

    public Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateAndReleaseFromDeliveryRequestAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken = default) =>
        CreateFromDeliveryRequestCoreAsync(request, release: true, cancellationToken);

    private async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateFromDeliveryRequestCoreAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        bool release,
        CancellationToken cancellationToken)
    {
        request ??= new ProductionWorkOrderDeliveryRequestRequest();
        var add = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: true, cancellationToken);
        if (add.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(add.Error.Value.Code, add.Error.Value.Message);
        }

        if (release)
        {
            var approve = await AuthorizeAsync(ProductionPermissionCodes.Release, requireWriteScope: true, cancellationToken);
            if (approve.Error is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(approve.Error.Value.Code, approve.Error.Value.Message);
            }
        }

        if (request.DeliveryRequestId <= 0)
        {
            return ValidationFailure<ProductionWorkOrderDetail>("Delivery Request is required.", "DeliveryRequestId");
        }

        var plannedQty = IvQty.Round(request.PlannedQty);
        if (plannedQty <= 0m)
        {
            return ValidationFailure<ProductionWorkOrderDetail>("Planned quantity must be positive.", "PlannedQty");
        }

        var scope = add.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
                db, scope, request.DeliveryRequestId, cancellationToken)
                ?? throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "Delivery Request not found.");
            if (request.DeliveryRequestRowVersion is { Length: > 0 }
                && !deliveryRequest.RowVersion.SequenceEqual(request.DeliveryRequestRowVersion))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Concurrency,
                    "The Delivery Request changed by another user. Reload it before creating a Work Order.");
            }

            if (deliveryRequest.Status is not (SaDeliveryRequestStatuses.Released or SaDeliveryRequestStatuses.InProduction))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    "Only a Released Delivery Request can create a Work Order.");
            }

            var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
                db, scope, deliveryRequest.Uid, cancellationToken);
            var activeAllocated = allocations.Where(x => x.IsActive).Sum(x => x.AllocatedQty);
            var unplanned = Math.Max(deliveryRequest.RequestedQty - activeAllocated, 0m);
            if (plannedQty - unplanned > 0.0001m)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    $"Planned quantity exceeds the Delivery Request unplanned quantity ({unplanned:N4}).");
            }

            var prepared = await BuildDeliveryRequestSnapshotAsync(
                db,
                scope,
                CopyRequestWithPlannedQty(request, plannedQty),
                cancellationToken);
            if (prepared.Error is not null || prepared.Snapshot?.WorkOrder is null)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    prepared.Error ?? "The Work Order snapshot could not be built.");
            }

            var entity = prepared.Snapshot.WorkOrder;
            var now = DateTime.UtcNow;
            entity.WorkOrderNo = await AllocateWorkOrderNoAsync(db, scope.CompanyCode, cancellationToken);
            entity.CreatedDate = now;
            entity.CreatedBy = scope.UserId;
            entity.ModifiedDate = now;
            entity.ModifiedBy = scope.UserId;
            entity.SourceType = ProductionSourceTypes.DeliveryRequest;
            entity.SourceReference = deliveryRequest.DeliveryRequestNo;
            entity.AuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrder = entity,
                EventType = ProductionAuditEventTypes.Created,
                ToStatus = ProductionWorkOrderStatuses.Draft,
                SnapshotRevision = entity.SnapshotRevision,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    entity.ProductCode,
                    entity.SnapshotHash,
                    entity.SourceBomVersion,
                    DeliveryRequestId = deliveryRequest.Uid,
                    DeliveryRequestNo = deliveryRequest.DeliveryRequestNo,
                    AllocatedQty = plannedQty
                }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });

            var allocation = new PrWorkOrderDemandAllocation
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                WorkOrder = entity,
                DeliveryRequest = deliveryRequest,
                AllocatedQty = plannedQty,
                IsActive = true,
                CreatedDate = now,
                CreatedBy = scope.UserId
            };
            db.ProductionWorkOrders.Add(entity);
            db.PrWorkOrderDemandAllocations.Add(allocation);
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);

            if (release)
            {
                await ValidateAndApplyCurrentReleaseAsync(db, entity, scope, cancellationToken);
                await MarkDeliveryRequestWorkOrderReleasedAsync(
                    db,
                    entity,
                    scope,
                    deliveryRequest.Uid,
                    "Work Order released from Delivery Request.",
                    cancellationToken);
            }

            db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
            {
                DeliveryRequestId = deliveryRequest.Uid,
                EventType = SaDeliveryRequestAuditEventTypes.WorkOrderCreated,
                WorkOrderId = entity.Uid,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    entity.WorkOrderNo,
                    AllocatedQty = plannedQty,
                    Released = release
                }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
            db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
            {
                DeliveryRequestId = deliveryRequest.Uid,
                EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
                WorkOrderId = entity.Uid,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { AllocatedQty = plannedQty, IsActive = true }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency,
                "The Delivery Request or Work Order changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "The Work Order demand allocation already exists. Reload the Delivery Request and try again.");
        }
    }

    private async Task<DeliveryRequestSnapshotBuild> BuildDeliveryRequestSnapshotAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken)
    {
        var deliveryRequest = await db.SaDeliveryRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == request.DeliveryRequestId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode, cancellationToken);
        if (deliveryRequest is null)
        {
            return DeliveryRequestSnapshotBuild.Fail("Delivery Request not found.");
        }

        if (deliveryRequest.Status is not (SaDeliveryRequestStatuses.Released or SaDeliveryRequestStatuses.InProduction))
        {
            return DeliveryRequestSnapshotBuild.Fail("Only a Released Delivery Request can create a Work Order.");
        }

        var allocations = await db.PrWorkOrderDemandAllocations.AsNoTracking()
            .Where(x => x.DeliveryRequestId == deliveryRequest.Uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .ToListAsync(cancellationToken);
        var activeAllocated = allocations.Where(x => x.IsActive).Sum(x => x.AllocatedQty);
        var unplanned = Math.Max(deliveryRequest.RequestedQty - activeAllocated, 0m);
        var plannedQty = IvQty.Round(request.PlannedQty);
        if (plannedQty <= 0m)
        {
            return DeliveryRequestSnapshotBuild.Fail("Planned quantity must be positive.");
        }
        if (plannedQty - unplanned > 0.0001m)
        {
            return DeliveryRequestSnapshotBuild.Fail(
                $"Planned quantity exceeds the Delivery Request unplanned quantity ({unplanned:N4}).");
        }

        var direction = string.IsNullOrWhiteSpace(request.SchedulingDirection)
            ? ProductionSchedulingDirections.Forward
            : Normalize(request.SchedulingDirection);
        if (!ProductionSchedulingDirections.IsKnown(direction))
        {
            return DeliveryRequestSnapshotBuild.Fail("Schedule direction must be FORWARD or BACKWARD.");
        }

        var start = request.PlannedStartDate == default ? deliveryRequest.RequiredDate.Date : request.PlannedStartDate.Date;
        var completion = request.PlannedCompletionDate == default ? start : request.PlannedCompletionDate.Date;
        if (completion < start)
        {
            return DeliveryRequestSnapshotBuild.Fail("Planned completion cannot be before planned start.");
        }

        var draft = new ProductionWorkOrderDraftRequest
        {
            ProductCode = deliveryRequest.ProductCode,
            DefinitionCode = Normalize(request.DefinitionCode ?? deliveryRequest.DefinitionCode ?? PrProductDefinitionCodes.Standard),
            PlannedQty = plannedQty,
            PlannedStartDate = start,
            PlannedCompletionDate = completion,
            SchedulingDirection = direction,
            SourceType = ProductionSourceTypes.DeliveryRequest,
            SourceReference = deliveryRequest.DeliveryRequestNo,
            Remark = request.Remark ?? deliveryRequest.Remark
        };
        var snapshot = await BuildCurrentSnapshotAsync(
            scope,
            draft,
            snapshotRevision: 1,
            explicitScheduleAnchor: null,
            cancellationToken);
        if (snapshot.WorkOrder is null)
        {
            return DeliveryRequestSnapshotBuild.Fail(snapshot.Error ?? "The Work Order snapshot could not be built.");
        }

        if (!string.Equals(
                Normalize(snapshot.WorkOrder.OutputUom),
                Normalize(deliveryRequest.ProductionUom),
                StringComparison.OrdinalIgnoreCase))
        {
            return DeliveryRequestSnapshotBuild.Fail(
                $"The Product Definition output UOM ({snapshot.WorkOrder.OutputUom}) is incompatible with the Delivery Request production UOM ({deliveryRequest.ProductionUom}).");
        }

        return new DeliveryRequestSnapshotBuild(
            deliveryRequest,
            activeAllocated,
            unplanned,
            snapshot,
            null);
    }

    private static ProductionWorkOrderPreview MapDeliveryRequestPreview(DeliveryRequestSnapshotBuild prepared)
    {
        var detail = MapDetail(prepared.Snapshot!.WorkOrder!);
        return new ProductionWorkOrderPreview
        {
            DeliveryRequestId = prepared.DeliveryRequest!.Uid,
            DeliveryRequestNo = prepared.DeliveryRequest.DeliveryRequestNo,
            DeliveryRequestUnplannedQty = prepared.UnplannedQty,
            ProductCode = detail.ProductCode,
            ProductDescription = detail.ProductDescription,
            OutputUom = detail.OutputUom,
            SourceDefinitionCode = detail.SourceDefinitionCode,
            SourceDefinitionName = detail.SourceDefinitionName,
            SourceBomHdrId = detail.SourceBomHdrId,
            SourceBomVersion = detail.SourceBomVersion,
            BomBaseQty = detail.BomBaseQty,
            BomBaseUom = detail.BomBaseUom,
            PlannedQty = detail.PlannedQty,
            PlannedStartDate = detail.PlannedStartDate,
            PlannedCompletionDate = detail.PlannedCompletionDate,
            SchedulingDirection = detail.SchedulingDirection,
            ScheduleAnchorDateTime = detail.ScheduleAnchorDateTime
                ?? ProductionSchedulingDirections.NormalizePlannerDateAnchor(detail.PlannedStartDate, detail.SchedulingDirection),
            SnapshotHash = detail.SnapshotHash,
            SourceProductDefinitionRevisionId = detail.SourceProductDefinitionRevisionId,
            RouteSteps = detail.RouteSteps,
            Materials = detail.Materials,
            Operations = detail.Operations,
            Warnings = prepared.Snapshot.Warnings
        };
    }

    private static ProductionWorkOrderDeliveryRequestRequest CopyRequestWithPlannedQty(
        ProductionWorkOrderDeliveryRequestRequest request,
        decimal plannedQty) => new()
        {
            DeliveryRequestId = request.DeliveryRequestId,
            PlannedQty = plannedQty,
            DefinitionCode = request.DefinitionCode,
            PlannedStartDate = request.PlannedStartDate,
            PlannedCompletionDate = request.PlannedCompletionDate,
            SchedulingDirection = request.SchedulingDirection,
            Remark = request.Remark,
            DeliveryRequestRowVersion = request.DeliveryRequestRowVersion
        };

    private async Task AdjustDeliveryRequestAllocationAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        decimal plannedQty,
        CancellationToken cancellationToken)
    {
        var allocation = entity.DemandAllocations
            .OrderByDescending(x => x.IsActive)
            .FirstOrDefault();
        if (allocation is null)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "This Delivery Request Work Order has no demand allocation and cannot be resized.");
        }

        var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
            db, scope, allocation.DeliveryRequestId, cancellationToken)
            ?? throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "The Delivery Request source was not found.");
        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        var otherActive = allocations
            .Where(x => x.IsActive && x.WorkOrderId != entity.Uid)
            .Sum(x => x.AllocatedQty);
        var availableForThisWorkOrder = Math.Max(deliveryRequest.RequestedQty - otherActive, 0m);
        if (plannedQty - availableForThisWorkOrder > 0.0001m)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                $"Planned quantity exceeds the Delivery Request quantity available to this Work Order ({availableForThisWorkOrder:N4}).");
        }

        allocation.AllocatedQty = plannedQty;
        allocation.ReleaseReason = "Work Order Draft quantity changed.";
        db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
        {
            DeliveryRequestId = deliveryRequest.Uid,
            EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
            WorkOrderId = entity.Uid,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { AllocatedQty = plannedQty }),
            OccurredDate = DateTime.UtcNow,
            ActorUserId = scope.UserId
        });
        TouchSqliteRowVersions(db, entity);
    }

    /// <summary>
    /// Locks the demand side before a normal Work Order lifecycle command locks the
    /// Work Order aggregate. Call this before LoadAggregateAsync/RequireDraftAsync
    /// whenever the Work Order may carry a Delivery Request allocation.
    /// </summary>
    private async Task LockDemandBridgeBeforeWorkOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string workOrderNo,
        CancellationToken cancellationToken)
    {
        var identity = await (
            from allocation in db.PrWorkOrderDemandAllocations.AsNoTracking()
            join workOrder in db.ProductionWorkOrders.AsNoTracking()
                on allocation.WorkOrderId equals workOrder.Uid
            where workOrder.CompanyCode == scope.CompanyCode
                && workOrder.BranchCode == scope.BranchCode
                && workOrder.WorkOrderNo == workOrderNo
                && allocation.CompanyCode == scope.CompanyCode
                && allocation.BranchCode == scope.BranchCode
            select new { allocation.DeliveryRequestId, allocation.WorkOrderId })
            .SingleOrDefaultAsync(cancellationToken);
        if (identity is null)
        {
            return;
        }

        var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
            db, scope, identity.DeliveryRequestId, cancellationToken);
        if (deliveryRequest is null)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Work Order demand source could not be loaded.");
        }

        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        if (!allocations.Any(x => x.WorkOrderId == identity.WorkOrderId))
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Work Order demand allocation could not be locked.");
        }
    }

    private async Task MarkDeliveryRequestWorkOrderReleasedAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        long? deliveryRequestId,
        string reason,
        CancellationToken cancellationToken)
    {
        var requestId = deliveryRequestId
            ?? entity.DemandAllocations
                .OrderByDescending(x => x.IsActive)
                .Select(x => (long?)x.DeliveryRequestId)
                .FirstOrDefault();
        if (requestId is not > 0)
        {
            return;
        }

        var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
            db, scope, requestId.Value, cancellationToken)
            ?? throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "The Delivery Request source was not found.");
        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        var allocation = allocations.SingleOrDefault(x => x.WorkOrderId == entity.Uid);
        if (allocation is null)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Work Order demand allocation could not be found.");
        }

        var now = entity.ReleasedDate ?? DateTime.UtcNow;
        allocation.IsActive = true;
        allocation.ReleasedDate = now;
        allocation.ReleasedBy = entity.ReleasedBy ?? scope.UserId;
        allocation.ReleaseReason = reason;
        deliveryRequest.Status = SaDeliveryRequestStatuses.InProduction;
        deliveryRequest.ModifiedDate = now;
        deliveryRequest.ModifiedBy = scope.UserId;
        db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
        {
            DeliveryRequestId = deliveryRequest.Uid,
            EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
            WorkOrderId = entity.Uid,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                allocation.AllocatedQty,
                allocation.IsActive,
                allocation.ReleasedDate,
                Released = true
            }),
            Reason = reason,
            OccurredDate = now,
            ActorUserId = scope.UserId
        });
    }

    private async Task DeactivateDeliveryRequestAllocationAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        string reason,
        CancellationToken cancellationToken)
    {
        var requestId = entity.DemandAllocations
            .OrderByDescending(x => x.IsActive)
            .Select(x => (long?)x.DeliveryRequestId)
            .FirstOrDefault();
        if (requestId is not > 0)
        {
            return;
        }

        var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
            db, scope, requestId.Value, cancellationToken)
            ?? throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "The Delivery Request source was not found.");
        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        var allocation = allocations.SingleOrDefault(x => x.WorkOrderId == entity.Uid);
        if (allocation is null)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Work Order demand allocation could not be found.");
        }

        if (!allocation.IsActive)
        {
            return;
        }

        var now = DateTime.UtcNow;
        allocation.IsActive = false;
        allocation.ReleasedDate = now;
        allocation.ReleasedBy = scope.UserId;
        allocation.ReleaseReason = reason;
        db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
        {
            DeliveryRequestId = deliveryRequest.Uid,
            EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
            WorkOrderId = entity.Uid,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                allocation.AllocatedQty,
                allocation.IsActive
            }),
            Reason = reason,
            OccurredDate = now,
            ActorUserId = scope.UserId
        });
    }

    private async Task<SaDeliveryRequest?> LockDeliveryRequestForWorkOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long uid,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            return await db.SaDeliveryRequests.FromSqlInterpolated($@"
SELECT * FROM dbo.SaDeliveryRequest WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE UID = {uid} AND CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}")
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.SaDeliveryRequests.SingleOrDefaultAsync(x => x.Uid == uid
            && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, cancellationToken);
    }

    private static async Task<List<PrWorkOrderDemandAllocation>> LockDeliveryRequestAllocationsForWorkOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long deliveryRequestId,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            return await db.PrWorkOrderDemandAllocations.FromSqlInterpolated($@"
SELECT * FROM dbo.PrWorkOrderDemandAllocation WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE DeliveryRequestID = {deliveryRequestId}
  AND CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}
ORDER BY WorkOrderID")
                .AsTracking()
                .ToListAsync(cancellationToken);
        }

        return await db.PrWorkOrderDemandAllocations
            .Where(x => x.DeliveryRequestId == deliveryRequestId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .OrderBy(x => x.WorkOrderId)
            .ToListAsync(cancellationToken);
    }

    private static bool IsSqlServer(AppDbContext db) =>
        db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record DeliveryRequestSnapshotBuild(
        SaDeliveryRequest? DeliveryRequest,
        decimal ActiveAllocatedQty,
        decimal UnplannedQty,
        BuiltSnapshot? Snapshot,
        string? Error)
    {
        public static DeliveryRequestSnapshotBuild Fail(string message) =>
            new(null, 0m, 0m, null, message);
    }
}
