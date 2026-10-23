using System.Text.Json;
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
    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateDraftAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDraftRequest();
        if (!string.IsNullOrWhiteSpace(request.SourceType)
            && !string.Equals(Normalize(request.SourceType), ProductionSourceTypes.Manual, StringComparison.Ordinal))
        {
            return ValidationFailure<ProductionWorkOrderDetail>(
                "Only Manual Work Order creation is available in Phase 1.", "SourceType");
        }

        var auth = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var scope = auth.Scope!;
        var built = await BuildCurrentSnapshotAsync(
            scope, request, snapshotRevision: 1, explicitScheduleAnchor: null, cancellationToken);
        if (built.WorkOrder is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                built.Error ?? "The snapshot could not be built.",
                built.ValidationErrors);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entity = built.WorkOrder;
            entity.WorkOrderNo = await AllocateWorkOrderNoAsync(db, scope.CompanyCode, cancellationToken);
            var now = DateTime.UtcNow;
            entity.CreatedDate = now;
            entity.CreatedBy = scope.UserId;
            entity.ModifiedDate = now;
            entity.ModifiedBy = scope.UserId;
            entity.AuditEvents.Add(new ProductionAuditEvent
            {
                EventType = ProductionAuditEventTypes.Created,
                ToStatus = ProductionWorkOrderStatuses.Draft,
                SnapshotRevision = entity.SnapshotRevision,
                DetailsJson = JsonSerializer.Serialize(new { entity.ProductCode, entity.SnapshotHash, entity.SourceBomVersion }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
            db.ProductionWorkOrders.Add(entity);
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "A Work Order with the allocated number already exists. Try saving again.");
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateAndReleaseAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDraftRequest();
        if (!string.IsNullOrWhiteSpace(request.SourceType)
            && !string.Equals(Normalize(request.SourceType), ProductionSourceTypes.Manual, StringComparison.Ordinal))
        {
            return ValidationFailure<ProductionWorkOrderDetail>(
                "Only Manual Work Order creation is available in Phase 1.", "SourceType");
        }

        var add = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: true, cancellationToken);
        if (add.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(add.Error.Value.Code, add.Error.Value.Message);
        }

        var release = await AuthorizeAsync(ProductionPermissionCodes.Release, requireWriteScope: true, cancellationToken);
        if (release.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(release.Error.Value.Code, release.Error.Value.Message);
        }

        var scope = add.Scope!;
        var built = await BuildCurrentSnapshotAsync(
            scope, request, snapshotRevision: 1, explicitScheduleAnchor: null, cancellationToken);
        if (built.WorkOrder is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                built.Error ?? "The snapshot could not be built.",
                built.ValidationErrors);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entity = built.WorkOrder;
            entity.WorkOrderNo = await AllocateWorkOrderNoAsync(db, scope.CompanyCode, cancellationToken);
            var now = DateTime.UtcNow;
            entity.CreatedDate = now;
            entity.CreatedBy = scope.UserId;
            entity.ModifiedDate = now;
            entity.ModifiedBy = scope.UserId;
            entity.AuditEvents.Add(new ProductionAuditEvent
            {
                EventType = ProductionAuditEventTypes.Created,
                ToStatus = ProductionWorkOrderStatuses.Draft,
                SnapshotRevision = entity.SnapshotRevision,
                DetailsJson = JsonSerializer.Serialize(new { entity.ProductCode, entity.SnapshotHash, entity.SourceBomVersion }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
            db.ProductionWorkOrders.Add(entity);
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);

            await ValidateAndApplyCurrentReleaseAsync(db, entity, scope, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "A Work Order with the allocated number already exists. Try saving again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> UpdateDraftHeaderAsync(
        ProductionWorkOrderHeaderUpdate request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderHeaderUpdate();
        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockDemandBridgeBeforeWorkOrderAsync(
                db, auth.Scope!, Normalize(request.WorkOrderNo), cancellationToken);
            var entity = await RequireDraftAsync(db, auth.Scope!, request.WorkOrderNo, request.RowVersion, cancellationToken);
            RequireCurrentSnapshot(entity);
            var mutation = await ApplyDraftHeaderChangesAsync(
                db,
                entity,
                auth.Scope!,
                request.PlannedQty,
                request.SchedulingDirection,
                request.ScheduleAnchorDateTime,
                request.SourceReference,
                request.Remark,
                cancellationToken);
            if (!mutation.Changed && mutation.DeliveryRequestIdToReconcile is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
            }

            await db.SaveChangesAsync(cancellationToken);
            if (mutation.DeliveryRequestIdToReconcile is long deliveryRequestId
                && _deliveryRequestFulfilment is not null)
            {
                await _deliveryRequestFulfilment.ReconcileInTransactionAsync(
                    db, deliveryRequestId, auth.Scope!, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> UpdateAndReleaseAsync(
        ProductionWorkOrderUpdateAndReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderUpdateAndReleaseRequest();
        var edit = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (edit.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(edit.Error.Value.Code, edit.Error.Value.Message);
        }

        var release = await AuthorizeAsync(ProductionPermissionCodes.Release, requireWriteScope: true, cancellationToken);
        if (release.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(release.Error.Value.Code, release.Error.Value.Message);
        }

        var scope = edit.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockDemandBridgeBeforeWorkOrderAsync(
                db, scope, Normalize(request.WorkOrderNo), cancellationToken);
            var entity = await RequireDraftAsync(db, scope, request.WorkOrderNo, request.RowVersion, cancellationToken);
            RequireCurrentSnapshot(entity);
            if (entity.SnapshotRevision != request.SnapshotRevision
                || !string.Equals(entity.SnapshotHash, request.SnapshotHash, StringComparison.Ordinal)
                || entity.SourceProductDefinitionRevisionId != request.SourceProductDefinitionRevisionId)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Concurrency,
                    ProductionReadinessErrorCodes.SnapshotStale
                    + ": The Draft changed after it was opened. Reload it before release.");
            }

            var mutation = await ApplyDraftHeaderChangesAsync(
                db,
                entity,
                scope,
                request.PlannedQty,
                request.SchedulingDirection,
                request.ScheduleAnchorDateTime,
                request.SourceReference,
                request.Remark,
                cancellationToken);

            if (mutation.DeliveryRequestIdToReconcile is long deliveryRequestId
                && _deliveryRequestFulfilment is not null)
            {
                await db.SaveChangesAsync(cancellationToken);
                await _deliveryRequestFulfilment.ReconcileInTransactionAsync(
                    db, deliveryRequestId, scope, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await ValidateAndApplyCurrentReleaseAsync(db, entity, scope, cancellationToken);
            await MarkDeliveryRequestWorkOrderReleasedAsync(
                db, entity, scope, deliveryRequestId: null,
                "Work Order released from Delivery Request.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> RecalculateDraftScheduleAsync(
        ProductionWorkOrderRecalculateRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderRecalculateRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await WorkOrderSchedulingLock.AcquireAsync(db, auth.Scope!.CompanyCode, exclusive: false, cancellationToken);
            var entity = await RequireDraftAsync(db, auth.Scope!, request.WorkOrderNo, request.RowVersion, cancellationToken);
            if (entity.SnapshotRevision != request.SnapshotRevision
                || !string.Equals(entity.SnapshotHash, request.SnapshotHash, StringComparison.Ordinal))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Concurrency,
                    ProductionReadinessErrorCodes.SnapshotStale + ": The Draft changed after this preview. Reload it.");
            }

            var scheduled = await _scheduler.ScheduleAsync(entity, cancellationToken);
            if (!scheduled.Succeeded)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.Validation, scheduled.FailureCode + ": " + scheduled.FailureMessage);
            }

            entity.SnapshotHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(entity);
            entity.SnapshotRevision += 1;
            StampDraftAudit(entity, auth.Scope!, ProductionAuditEventTypes.ScheduleRecalculated, "Schedule recalculated.");
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderRefreshPreview>> PreviewRefreshFromDefinitionAsync(
        string workOrderNo,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderRefreshPreview>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadAggregateAsync(db, auth.Scope!, Normalize(workOrderNo), tracking: false, cancellationToken);
        if (entity is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderRefreshPreview>.Fail(IvMasterErrorCode.NotFound, "Work Order not found.");
        }

        if (!ProductionWorkOrderRules.CanEdit(entity.Status))
        {
            return IvMasterOperationResult<ProductionWorkOrderRefreshPreview>.Fail(
                IvMasterErrorCode.Validation, "Only a Draft Work Order can refresh from its Product Definition.");
        }

        var procurementBlocker = await GetLinkedProcurementBlockerAsync(db, entity, cancellationToken);
        if (procurementBlocker is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderRefreshPreview>.Fail(
                IvMasterErrorCode.InUse, procurementBlocker);
        }

        var header = HeaderRequest(entity);
        if (string.IsNullOrWhiteSpace(header.DefinitionCode) && entity.SourceBomHdrId > 0)
        {
            var source = await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.Uid == entity.SourceBomHdrId)
                .Select(x => x.DefinitionCode)
                .FirstOrDefaultAsync(cancellationToken);
            header.DefinitionCode = source ?? string.Empty;
        }

        var built = await BuildCurrentSnapshotAsync(
            auth.Scope!, header, entity.SnapshotRevision, entity.ScheduleAnchorDateTime, cancellationToken);
        if (built.WorkOrder is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderRefreshPreview>.Fail(
                IvMasterErrorCode.Validation, built.Error ?? "The snapshot could not be built.");
        }

        var diff = DiffSnapshot(entity, built.WorkOrder);
        return IvMasterOperationResult<ProductionWorkOrderRefreshPreview>.Ok(new ProductionWorkOrderRefreshPreview
        {
            RowVersion = entity.RowVersion.ToArray(),
            SourceProductDefinitionRevisionId = built.WorkOrder.SourceProductDefinitionRevisionId,
            DefinitionSourceHashVersion = built.WorkOrder.DefinitionSourceHashVersion ?? ProductionDefinitionSourceHashVersions.Current,
            DefinitionSourceHash = built.WorkOrder.DefinitionSourceHash ?? string.Empty,
            Added = diff.Added,
            Removed = diff.Removed,
            Changed = diff.Changed
        });
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> RefreshDraftFromDefinitionAsync(
        ProductionWorkOrderRefreshConfirm request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderRefreshConfirm();
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ValidationFailure<ProductionWorkOrderDetail>("A reason is required to refresh a Draft.", "Reason");
        }

        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entity = await RequireDraftAsync(db, auth.Scope!, request.WorkOrderNo, request.RowVersion, cancellationToken);
            var procurementBlocker = await GetLinkedProcurementBlockerAsync(db, entity, cancellationToken);
            if (procurementBlocker is not null)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.InUse, procurementBlocker);
            }
            var header = HeaderRequest(entity);
            if (string.IsNullOrWhiteSpace(header.DefinitionCode) && entity.SourceBomHdrId > 0)
            {
                var source = await db.PrBomHdrs.AsNoTracking()
                    .Where(x => x.Uid == entity.SourceBomHdrId)
                    .Select(x => x.DefinitionCode)
                    .FirstOrDefaultAsync(cancellationToken);
                header.DefinitionCode = source ?? string.Empty;
            }

            var built = await BuildCurrentSnapshotAsync(
                auth.Scope!, header, entity.SnapshotRevision + 1,
                entity.ScheduleAnchorDateTime, cancellationToken);
            if (built.WorkOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Validation, built.Error ?? "The snapshot could not be built.");
            }

            if (built.WorkOrder.SourceProductDefinitionRevisionId != request.SourceProductDefinitionRevisionId
                || built.WorkOrder.DefinitionSourceHashVersion != request.DefinitionSourceHashVersion
                || !string.Equals(built.WorkOrder.DefinitionSourceHash, request.DefinitionSourceHash, StringComparison.Ordinal))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Concurrency,
                    ProductionReadinessErrorCodes.DefinitionSourceStale
                    + ": The Product Definition changed after the refresh preview. Preview it again.");
            }

            ReplaceSnapshot(db, entity, built.WorkOrder);
            entity.SnapshotRevision += 1;
            StampDraftAudit(entity, auth.Scope!, ProductionAuditEventTypes.Refreshed, request.Reason.Trim());
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
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>> PreviewChangeDefinitionAsync(
        string workOrderNo,
        string targetDefinitionCode,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Fail(
                auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var targetCode = PrProductDefinitionCodes.Normalize(targetDefinitionCode);
        if (targetCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(targetCode))
        {
            return ValidationFailure<ProductionWorkOrderChangeDefinitionPreview>(
                "A valid target Product Definition code is required.", "TargetDefinitionCode");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadAggregateAsync(db, auth.Scope!, Normalize(workOrderNo), tracking: false, cancellationToken);
        if (entity is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Fail(
                IvMasterErrorCode.NotFound, "Work Order not found.");
        }

        if (!ProductionWorkOrderRules.CanEdit(entity.Status))
        {
            return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Fail(
                IvMasterErrorCode.Validation, "Only a Draft Work Order can change its Product Definition.");
        }

        var procurementBlocker = await GetLinkedProcurementBlockerAsync(db, entity, cancellationToken);
        if (procurementBlocker is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Fail(
                IvMasterErrorCode.InUse, procurementBlocker);
        }

        if (!ProductionSnapshotFormatVersions.IsCurrentForDraft(
                entity.SnapshotFormatVersion, entity.SnapshotHashVersion))
        {
            return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                + ": Refresh Definition to upgrade the Work Order snapshot/costing contract before editing or re-releasing.");
        }

        if (string.Equals(entity.SourceDefinitionCode, targetCode, StringComparison.Ordinal))
        {
            return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Fail(
                IvMasterErrorCode.Validation,
                "The Work Order already uses that Product Definition. Choose a different definition.");
        }

        var request = HeaderRequest(entity);
        request.DefinitionCode = targetCode;
        var built = await BuildCurrentSnapshotAsync(
            auth.Scope!, request, entity.SnapshotRevision, entity.ScheduleAnchorDateTime, cancellationToken);
        if (built.WorkOrder is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Fail(
                IvMasterErrorCode.Validation, built.Error ?? "The snapshot could not be built.");
        }

        var diff = DiffSnapshot(entity, built.WorkOrder);
        return IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>.Ok(
            new ProductionWorkOrderChangeDefinitionPreview
            {
                RowVersion = entity.RowVersion.ToArray(),
                TargetDefinitionCode = built.WorkOrder.SourceDefinitionCode,
                TargetDefinitionName = built.WorkOrder.SourceDefinitionName,
                TargetSourceProductDefinitionRevisionId = built.WorkOrder.SourceProductDefinitionRevisionId,
                TargetSourceBomVersion = built.WorkOrder.SourceBomVersion,
                TargetDefinitionSourceHashVersion =
                    built.WorkOrder.DefinitionSourceHashVersion ?? ProductionDefinitionSourceHashVersions.Current,
                TargetDefinitionSourceHash = built.WorkOrder.DefinitionSourceHash ?? string.Empty,
                Added = diff.Added,
                Removed = diff.Removed,
                Changed = diff.Changed
            });
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ConfirmChangeDefinitionAsync(
        ProductionWorkOrderChangeDefinitionConfirm request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderChangeDefinitionConfirm();
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ValidationFailure<ProductionWorkOrderDetail>("A reason is required to change the Product Definition.", "Reason");
        }

        var targetCode = PrProductDefinitionCodes.Normalize(request.TargetDefinitionCode);
        if (targetCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(targetCode))
        {
            return ValidationFailure<ProductionWorkOrderDetail>(
                "A valid target Product Definition code is required.", "TargetDefinitionCode");
        }

        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entity = await RequireDraftAsync(db, auth.Scope!, request.WorkOrderNo, request.RowVersion, cancellationToken);
            var procurementBlocker = await GetLinkedProcurementBlockerAsync(db, entity, cancellationToken);
            if (procurementBlocker is not null)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.InUse, procurementBlocker);
            }
            if (!ProductionSnapshotFormatVersions.IsCurrentForDraft(
                    entity.SnapshotFormatVersion, entity.SnapshotHashVersion))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                    + ": Refresh Definition to upgrade the Work Order snapshot/costing contract before editing or re-releasing.");
            }

            var oldDefinitionCode = entity.SourceDefinitionCode;
            var oldDefinitionName = entity.SourceDefinitionName;
            var oldRevisionId = entity.SourceProductDefinitionRevisionId;
            var oldBomVersion = entity.SourceBomVersion;
            var oldSnapshotRevision = entity.SnapshotRevision;

            var header = HeaderRequest(entity);
            header.DefinitionCode = targetCode;
            var built = await BuildCurrentSnapshotAsync(
                auth.Scope!, header, entity.SnapshotRevision + 1,
                entity.ScheduleAnchorDateTime, cancellationToken);
            if (built.WorkOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Validation, built.Error ?? "The snapshot could not be built.");
            }

            if (!string.Equals(built.WorkOrder.SourceDefinitionCode, targetCode, StringComparison.Ordinal)
                || built.WorkOrder.SourceProductDefinitionRevisionId != request.TargetSourceProductDefinitionRevisionId
                || built.WorkOrder.DefinitionSourceHashVersion != request.TargetDefinitionSourceHashVersion
                || !string.Equals(
                    built.WorkOrder.DefinitionSourceHash,
                    request.TargetDefinitionSourceHash,
                    StringComparison.Ordinal))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Concurrency,
                    ProductionReadinessErrorCodes.DefinitionSourceStale
                    + ": The Product Definition changed after the change-definition preview. Preview it again.");
            }

            ReplaceSnapshot(db, entity, built.WorkOrder);
            entity.SnapshotRevision += 1;

            var now = DateTime.UtcNow;
            entity.ModifiedDate = now;
            entity.ModifiedBy = auth.Scope!.UserId;
            entity.AuditEvents.Add(new ProductionAuditEvent
            {
                EventType = ProductionAuditEventTypes.DefinitionChanged,
                FromStatus = ProductionWorkOrderStatuses.Draft,
                ToStatus = ProductionWorkOrderStatuses.Draft,
                SnapshotRevision = entity.SnapshotRevision,
                Reason = request.Reason.Trim(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    OldDefinitionCode = oldDefinitionCode,
                    OldDefinitionName = oldDefinitionName,
                    OldSourceProductDefinitionRevisionId = oldRevisionId,
                    OldSourceBomVersion = oldBomVersion,
                    OldSnapshotRevision = oldSnapshotRevision,
                    NewDefinitionCode = entity.SourceDefinitionCode,
                    NewDefinitionName = entity.SourceDefinitionName,
                    NewSourceProductDefinitionRevisionId = entity.SourceProductDefinitionRevisionId,
                    NewSourceBomVersion = entity.SourceBomVersion,
                    NewSnapshotRevision = entity.SnapshotRevision
                }),
                OccurredDate = now,
                ActorUserId = auth.Scope.UserId
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
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReleaseCurrentAsync(
        ProductionWorkOrderReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderReleaseRequest();
        var auth = await AuthorizeAsync(ProductionPermissionCodes.Release, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockDemandBridgeBeforeWorkOrderAsync(
                db, scope, Normalize(request.WorkOrderNo), cancellationToken);
            var entity = await LoadAggregateAsync(db, scope, Normalize(request.WorkOrderNo), tracking: true, cancellationToken);
            if (entity is null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            ValidateReleaseRequestFingerprint(db, entity, request);
            await ValidateAndApplyCurrentReleaseAsync(db, entity, scope, cancellationToken);
            await MarkDeliveryRequestWorkOrderReleasedAsync(
                db, entity, scope, deliveryRequestId: null,
                "Work Order released from Delivery Request.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> SelectDraftMachineAsync(
        ProductionWorkOrderMachineSelectRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderMachineSelectRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await WorkOrderSchedulingLock.AcquireAsync(db, auth.Scope!.CompanyCode, exclusive: false, cancellationToken);
            var entity = await RequireDraftAsync(db, auth.Scope!, request.WorkOrderNo, request.RowVersion, cancellationToken);
            RequireCurrentSnapshot(entity);
            RequireSnapshotFingerprint(entity, request.SnapshotRevision, request.SnapshotHash);

            var operation = entity.Operations.FirstOrDefault(o => o.Uid == request.WorkOrderOperationId)
                ?? entity.RouteSteps.SelectMany(s => s.Operations).FirstOrDefault(o => o.Uid == request.WorkOrderOperationId);
            if (operation is null)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    "The requested Work Order operation was not found on this Draft.");
            }

            var target = operation.Machines.FirstOrDefault(m => m.Uid == request.WorkOrderMachineId);
            if (target is null)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    "The requested machine does not belong to that operation.");
            }

            if (target.IsSelected)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
            }

            var previous = operation.Machines.FirstOrDefault(m => m.IsSelected);
            foreach (var machine in operation.Machines.Where(m => m.IsSelected))
            {
                machine.IsSelected = false;
                ClearMachineScheduleProvenance(machine);
            }

            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);

            target.IsSelected = true;
            SyncMachineLabourContribution(operation);
            ClearOperationScheduleProvenance(operation);

            var quantities = await _quantities.CalculateAsync(entity, cancellationToken);
            if (!quantities.Succeeded)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.Validation, quantities.Summary);
            }

            var scheduled = await _scheduler.ScheduleAsync(entity, cancellationToken);
            if (!scheduled.Succeeded)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    scheduled.FailureCode + ": " + scheduled.FailureMessage);
            }

            entity.SnapshotHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(entity);
            entity.SnapshotRevision += 1;
            var reason = string.IsNullOrWhiteSpace(request.Reason)
                ? $"Selected machine {target.MachineCode} for operation {operation.OperationCode}"
                    + (previous is null ? "." : $" (was {previous.MachineCode}).")
                : request.Reason.Trim();
            StampDraftAudit(entity, auth.Scope!, ProductionAuditEventTypes.MachineSelected, reason);
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>> GetDraftMaterialAlternatesAsync(
        string workOrderNo,
        long workOrderMaterialId,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>.Fail(
                auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadAggregateAsync(db, auth.Scope!, Normalize(workOrderNo), tracking: false, cancellationToken);
        if (entity is null)
        {
            return IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>.Fail(
                IvMasterErrorCode.NotFound, "Work Order not found.");
        }

        if (!ProductionWorkOrderRules.CanEdit(entity.Status))
        {
            return IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>.Fail(
                IvMasterErrorCode.Validation, "Only a Draft Work Order can change materials.");
        }

        if (!ProductionSnapshotFormatVersions.IsCurrentForDraft(
                entity.SnapshotFormatVersion, entity.SnapshotHashVersion))
        {
            return IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                + ": Refresh Definition to upgrade the Work Order snapshot/costing contract before editing or re-releasing.");
        }

        var material = entity.Materials.FirstOrDefault(m => m.Uid == workOrderMaterialId);
        if (material is null)
        {
            return IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>.Fail(
                IvMasterErrorCode.NotFound, "The Work Order material was not found.");
        }

        var group = PrBomAlternateGroups.Normalize(material.AlternateGroupCode);
        if (material.SourceBomHdrId is null
            || material.SourceBomLineId is null
            || material.SourceOperationId is null
            || group is null)
        {
            return IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>.Ok([]);
        }

        var candidates = await db.PrDefBOMs.AsNoTracking()
            .Where(x => x.BomHdrId == material.SourceBomHdrId
                        && x.OperationId == material.SourceOperationId
                        && x.Uid != material.SourceBomLineId)
            .OrderByDescending(x => x.BomDefault)
            .ThenBy(x => x.SeqNo)
            .ThenBy(x => x.ICode)
            .ToListAsync(cancellationToken);

        var alternates = candidates
            .Where(x => string.Equals(
                PrBomAlternateGroups.Normalize(x.AlternateGroupCode),
                group,
                StringComparison.Ordinal))
            .Select(x => new ProductionWorkOrderMaterialAlternateVm
            {
                SourceBomLineId = x.Uid,
                ComponentCode = x.ICode,
                ComponentDescription = x.IName,
                BomDefault = x.BomDefault,
                AlternateGroupCode = PrBomAlternateGroups.Normalize(x.AlternateGroupCode),
                ComponentQtyPerParent = x.StdQty,
                StandardUom = x.StdUom,
                SupplySource = x.SupplySource,
                ComponentDefinitionCode = string.IsNullOrWhiteSpace(x.ComponentDefinitionCode)
                    ? null
                    : PrProductDefinitionCodes.Normalize(x.ComponentDefinitionCode)
            })
            .ToList();

        return IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>.Ok(alternates);
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> SubstituteDraftMaterialAsync(
        ProductionWorkOrderMaterialSubstituteRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderMaterialSubstituteRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await WorkOrderSchedulingLock.AcquireAsync(db, auth.Scope!.CompanyCode, exclusive: false, cancellationToken);
            var entity = await RequireDraftAsync(db, auth.Scope!, request.WorkOrderNo, request.RowVersion, cancellationToken);
            var procurementBlocker = await GetLinkedProcurementBlockerAsync(db, entity, cancellationToken);
            if (procurementBlocker is not null)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.InUse, procurementBlocker);
            }
            RequireCurrentSnapshot(entity);
            RequireSnapshotFingerprint(entity, request.SnapshotRevision, request.SnapshotHash);

            var material = entity.Materials.FirstOrDefault(m => m.Uid == request.WorkOrderMaterialId);
            if (material is null)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "The Work Order material was not found.");
            }

            var group = PrBomAlternateGroups.Normalize(material.AlternateGroupCode);
            if (material.SourceBomHdrId is null
                || material.SourceBomLineId is null
                || material.SourceOperationId is null
                || group is null)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    "This material has no alternate group in the frozen Product Definition revision.");
            }

            var replacement = await db.PrDefBOMs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Uid == request.ReplacementSourceBomLineId, cancellationToken);
            if (replacement is null
                || replacement.BomHdrId != material.SourceBomHdrId
                || replacement.OperationId != material.SourceOperationId
                || !string.Equals(PrBomAlternateGroups.Normalize(replacement.AlternateGroupCode), group, StringComparison.Ordinal))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    "The replacement must be another member of the same exact-source alternate group.");
            }

            if (replacement.Uid == material.SourceBomLineId)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
            }

            var componentCode = (replacement.ICode ?? string.Empty).Trim().ToUpperInvariant();
            var duplicate = entity.Materials.Any(m =>
                m.Uid != material.Uid
                && m.WorkOrderOperationId == material.WorkOrderOperationId
                && string.Equals(m.ComponentCode, componentCode, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    $"Component {componentCode} is already required on the same consuming operation.");
            }

            var revision = await db.PrBomHdrs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Uid == material.SourceBomHdrId.Value, cancellationToken)
                ?? throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    "The frozen Product Definition revision is missing.");

            var itemCodes = new[] { componentCode };
            var masters = await db.IvStockMasters.AsNoTracking()
                .Where(i => i.CompanyCode == entity.CompanyCode && itemCodes.Contains(i.ICode))
                .ToDictionaryAsync(
                    i => i.ICode.Trim().ToUpperInvariant(),
                    i => i,
                    StringComparer.OrdinalIgnoreCase,
                    cancellationToken);

            var stepsBySourceId = entity.RouteSteps
                .Where(s => s.SourceRouteStepId is not null)
                .GroupBy(s => s.SourceRouteStepId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            var errors = new List<WorkOrderCalculationError>();
            var oldCode = material.ComponentCode;
            WorkOrderSnapshotBuilder.ApplyDefinitionMaterial(
                material,
                revision,
                replacement,
                material.WorkOrderOperation,
                masters,
                stepsBySourceId,
                errors);
            if (errors.Count > 0)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    string.Join(" ", errors.Select(e => e.Code + ": " + e.Message)));
            }

            var quantities = await _quantities.CalculateAsync(entity, cancellationToken);
            if (!quantities.Succeeded)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.Validation, quantities.Summary);
            }

            var scheduled = await _scheduler.ScheduleAsync(entity, cancellationToken);
            if (!scheduled.Succeeded)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    scheduled.FailureCode + ": " + scheduled.FailureMessage);
            }

            entity.SnapshotHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(entity);
            entity.SnapshotRevision += 1;
            var reason = string.IsNullOrWhiteSpace(request.Reason)
                ? $"Substituted material {oldCode} → {material.ComponentCode} in group {group}."
                : request.Reason.Trim();
            StampDraftAudit(entity, auth.Scope!, ProductionAuditEventTypes.MaterialSubstituted, reason);
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Concurrency<ProductionWorkOrderDetail>();
        }
    }

    private static void ValidateReleaseRequestFingerprint(
        AppDbContext db,
        ProductionWorkOrder entity,
        ProductionWorkOrderReleaseRequest request)
    {
        if (request.RowVersion is not { Length: > 0 })
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Concurrency, "The Work Order version is missing. Reload before release.");
        }

        db.Entry(entity).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
        if (entity.SnapshotRevision != request.SnapshotRevision
            || !string.Equals(entity.SnapshotHash, request.SnapshotHash, StringComparison.Ordinal)
            || entity.SourceProductDefinitionRevisionId != request.SourceProductDefinitionRevisionId)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Concurrency,
                ProductionReadinessErrorCodes.SnapshotStale + ": The Draft changed after it was opened. Reload it before release.");
        }
    }

    /// <summary>
    /// Release invariants for a tracked current-format Draft. Does not save, commit, or roll back.
    /// The caller owns the transaction.
    /// </summary>
    private async Task ValidateAndApplyCurrentReleaseAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        if (!_options.ReleaseEnabled)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.ReleaseDisabled + ": Release is disabled by configuration.");
        }

        if (!ProductionWorkOrderRules.CanRelease(entity.Status))
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation, "Only a Draft Work Order can be released.");
        }

        var selectedMachineCodes = entity.RouteSteps
            .SelectMany(step => step.Operations)
            .SelectMany(operation => operation.Machines)
            .Where(machine => machine.IsSelected)
            .Select(machine => machine.MachineCode)
            .ToList();

        // Seed locks with the union of selected machines and every machine that still owns an
        // allocation for this Work Order (plan §15.1). Deleting an old-machine fragment without
        // holding its physical-machine lock would race a concurrent planner placement.
        var ownedAllocationMachineCodes = await db.ProductionMachineScheduleAllocations.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderId == entity.Uid)
            .Select(x => x.MachineCode)
            .Distinct()
            .ToListAsync(cancellationToken);
        var releaseMachineCodes = selectedMachineCodes
            .Concat(ownedAllocationMachineCodes)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        try
        {
            // Keep the global order compatible with planner commits: physical machines first,
            // scheduling-source shared lock second, Work Order execution lock third.
            await ProductionMachineScheduleLock.AcquireManyAsync(
                db, scope.CompanyCode, releaseMachineCodes, cancellationToken);
        }
        catch (ProductionMachineScheduleLockException ex)
        {
            throw new WorkOrderCommandException(IvMasterErrorCode.Concurrency, ex.Message);
        }
        await WorkOrderSchedulingLock.AcquireAsync(db, scope.CompanyCode, exclusive: false, cancellationToken);

        // Re-read after locks. A code that appeared since the seed means another command wrote
        // an allocation on a machine we do not hold; abort and let the caller retry.
        var lockedCodes = releaseMachineCodes.ToHashSet(StringComparer.Ordinal);
        var currentAllocationCodes = await db.ProductionMachineScheduleAllocations.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderId == entity.Uid)
            .Select(x => x.MachineCode)
            .Distinct()
            .ToListAsync(cancellationToken);
        var unlockedAllocationCode = currentAllocationCodes
            .Select(Normalize)
            .FirstOrDefault(code => !lockedCodes.Contains(code));
        if (unlockedAllocationCode is not null)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Concurrency,
                $"Work Order {entity.WorkOrderNo} gained an allocation on unlocked machine {unlockedAllocationCode}. "
                + "Retry the release.");
        }

        // Execution lock is the third step of the Release lock order. Everything below re-reads
        // state while planner commits and Daily Production are excluded (plan §15.1).
        try
        {
            await ProductionWorkOrderExecutionLock.AcquireAsync(
                db, scope.CompanyCode, scope.BranchCode!, entity.Uid, cancellationToken);
        }
        catch (ProductionWorkOrderExecutionLockException ex)
        {
            throw new WorkOrderCommandException(IvMasterErrorCode.Concurrency, ex.Message);
        }

        // A Draft saved from a local planner date stored a kind-sensitive hash. SQL datetime2
        // reloads the same clock time as Unspecified, so adopt the canonical hash before release.
        WorkOrderSnapshotHasher.CanonicalizeStoredTimestampKindHash(entity);
        var recomputed = WorkOrderSnapshotHasher.ComputeSnapshotHash(entity);
        var report = _readiness.Validate(entity, new WorkOrderReadinessContext
        {
            ReleaseEnabled = true,
            RequireCurrentSnapshotFormat = true,
            CurrentSnapshotHash = recomputed
        });
        if (!report.IsReady)
        {
            throw new WorkOrderCommandException(IvMasterErrorCode.Validation, report.Summary);
        }

        var currentHashes = await _scheduler.CurrentScheduleHashesAsync(entity, cancellationToken);
        foreach (var step in entity.RouteSteps)
        {
            foreach (var operation in step.Operations)
            {
                if (PrProcessTypes.SupportsMachine(operation.ProcessType))
                {
                    var machine = operation.Machines.FirstOrDefault(m => m.IsSelected);
                    if (machine is null)
                    {
                        continue;
                    }

                    if (!currentHashes.TryGetValue(WorkOrderScheduleCalculator.MachineKey(operation, machine), out var hash)
                        || !string.Equals(hash, machine.ScheduleSourceHash, StringComparison.Ordinal))
                    {
                        throw new WorkOrderCommandException(
                            IvMasterErrorCode.Validation,
                            ProductionReadinessErrorCodes.ScheduleStale
                            + ": The machine calendar changed. Recalculate the schedule before release.");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(operation.ScheduleSourceHash))
                {
                    if (!currentHashes.TryGetValue(WorkOrderScheduleCalculator.OperationKey(operation), out var hash)
                        || !string.Equals(hash, operation.ScheduleSourceHash, StringComparison.Ordinal))
                    {
                        throw new WorkOrderCommandException(
                            IvMasterErrorCode.Validation,
                            ProductionReadinessErrorCodes.ScheduleStale
                            + ": The plant calendar changed. Recalculate the schedule before release.");
                    }
                }
            }
        }

        var allocationFragmentCount = await ReserveMachineCapacityForReleaseAsync(
            db, entity, scope, cancellationToken);

        var now = DateTime.UtcNow;
        entity.Status = ProductionWorkOrderStatuses.Released;
        entity.ReleasedDate = now;
        entity.ReleasedBy = scope.UserId;
        entity.ModifiedDate = now;
        entity.ModifiedBy = scope.UserId;
        entity.AuditEvents.Add(new ProductionAuditEvent
        {
            WorkOrder = entity,
            EventType = ProductionAuditEventTypes.Released,
            FromStatus = ProductionWorkOrderStatuses.Draft,
            ToStatus = ProductionWorkOrderStatuses.Released,
            SnapshotRevision = entity.SnapshotRevision,
            Reason = "Draft snapshot approved for execution.",
            DetailsJson = JsonSerializer.Serialize(new { MachineScheduleAllocationFragments = allocationFragmentCount }),
            OccurredDate = now,
            ActorUserId = scope.UserId
        });
        TouchSqliteRowVersions(db, entity);
    }

    private async Task<int> ReserveMachineCapacityForReleaseAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var selectedMachines = entity.RouteSteps
            .SelectMany(step => step.Operations)
            .SelectMany(operation => operation.Machines
                .Where(machine => machine.IsSelected)
                .Select(machine => (Operation: operation, Machine: machine)))
            .ToList();
        var machineCodes = selectedMachines
            .Select(x => Normalize(x.Machine.MachineCode))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (selectedMachines.Count == 0)
            return 0;

        // Allocation-authoritative capacity (plan §9/§14). Active jobs are never reconstructed
        // from the raw calendar: a Released job may have been deliberately rescheduled, and
        // rebuilding it would fabricate a conflict. Legacy empty zero-duration rows contribute
        // nothing; corrupt rows fail closed instead of being silently ignored.
        var evidence = await _capacity.LoadAsync(
            db,
            scope.CompanyCode,
            machineCodes,
            excludedWorkOrderId: entity.Uid,
            cancellationToken: cancellationToken);
        if (evidence.HasBlockers)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.MachineCapacityInvalid + ": "
                + string.Join("; ", evidence.Blockers));
        }

        var prior = selectedMachines.ToDictionary(
            x => x.Machine,
            x => new
            {
                MachineStart = x.Machine.PlannedStartDateTime,
                MachineCompletion = x.Machine.PlannedCompletionDateTime,
                OperationStart = x.Operation.PlannedStartDateTime,
                OperationCompletion = x.Operation.PlannedCompletionDateTime
            });

        // Canonical revalidation under the caller's transaction and machine locks: the identical
        // engine that produced the Draft placement decides whether the saved dates still hold.
        var provider = new TransactionalMachineCapacityWindowProvider(
            _capacity, db, scope.CompanyCode, excludedWorkOrderId: entity.Uid);
        var scheduled = await _scheduler.ScheduleAsync(entity, provider, cancellationToken);
        if (!scheduled.Succeeded)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                scheduled.FailureCode + ": " + scheduled.FailureMessage);
        }

        foreach (var (operation, machine) in selectedMachines)
        {
            var before = prior[machine];
            if (before.MachineStart != machine.PlannedStartDateTime
                || before.MachineCompletion != machine.PlannedCompletionDateTime
                || before.OperationStart != operation.PlannedStartDateTime
                || before.OperationCompletion != operation.PlannedCompletionDateTime)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    ProductionReleaseErrorCodes.MachineCapacityStale
                    + $": Machine {machine.MachineCode} capacity changed after this Draft was calculated. "
                    + "Recalculate the Draft before release.");
            }
        }

        var pending = new List<(ProductionWorkOrderOperation Operation, ProductionWorkOrderMachine Machine,
            IReadOnlyList<TimeInterval> Fragments)>();
        foreach (var (operation, machine) in selectedMachines)
        {
            if (!scheduled.ConsumedByOperation.TryGetValue(operation, out var fragments) || fragments.Count == 0)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    ProductionReleaseErrorCodes.MachineCapacityStale
                    + $": Machine {machine.MachineCode} produced no capacity fragments. Recalculate the Draft.");
            }

            pending.Add((operation, machine, fragments));
        }

        var fragmentCount = 0;
        var createdBy = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
        var now = DateTime.UtcNow;

        // Replacement boundary is the whole current Work Order in this company/branch
        // (plan §15.2). Old-machine fragments are removed under their machine lock.
        var superseded = await db.ProductionMachineScheduleAllocations
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderId == entity.Uid)
            .ToListAsync(cancellationToken);
        await MachineScheduleSegmentPersistence.RemoveAllocationLinksAsync(db, superseded, cancellationToken);
        db.ProductionMachineScheduleAllocations.RemoveRange(superseded);

        var supersededSets = await db.ProductionMachineScheduleSegmentSets
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderId == entity.Uid
                && x.IsCurrent)
            .ToListAsync(cancellationToken);
        MachineScheduleSegmentPersistence.SupersedeSets(supersededSets, scope.UserId, now);

        // Metadata dual-write only: re-release supersedes the previous planning history and seeds
        // one ORIGINAL business segment per selected machine. No quantity/hash/cost changes.
        var supersededSegments = await db.ProductionMachineScheduleSegments
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderId == entity.Uid
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .ToListAsync(cancellationToken);
        MachineScheduleSegmentPersistence.Supersede(
            supersededSegments, scope.UserId, now, "Re-release superseded the previous planning segments.");

        // Clear the filtered one-current-set index before inserting replacement sets.
        await db.SaveChangesAsync(cancellationToken);

        var setByMachine = new Dictionary<long, ProductionMachineScheduleSegmentSet>();
        foreach (var (operation, machine, _) in pending)
        {
            var set = MachineScheduleSegmentPersistence.BuildCurrentSet(
                entity, operation, machine,
                ProductionMachineScheduleSegmentSetModes.FullPlan,
                ProductionMachineSchedulePlanningStates.Scheduled,
                operation.PlannedOutputQty,
                "Draft release seeded the canonical one-span plan.",
                scope.UserId, now, db);
            db.ProductionMachineScheduleSegmentSets.Add(set);
            setByMachine[machine.Uid] = set;
        }
        if (setByMachine.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        var segmentByMachine = new Dictionary<long, ProductionMachineScheduleSegment>();
        foreach (var (operation, machine, _) in pending)
        {
            var segment = MachineScheduleSegmentPersistence.BuildOriginalSegment(
                entity, operation, machine, scope.UserId, now, db);
            segment.SegmentSetId = setByMachine[machine.Uid].Uid;
            db.ProductionMachineScheduleSegments.Add(segment);
            segmentByMachine[machine.Uid] = segment;
        }

        // The allocation's nullable segment identity needs the database-generated segment UID.
        // This save stays inside the caller's transaction and is rolled back with the release.
        if (segmentByMachine.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        var allocationLinks = new List<(ProductionMachineScheduleAllocation Allocation,
            ProductionMachineScheduleSegment Segment)>();
        foreach (var (operation, machine, fragments) in pending)
        {
            var fragmentNo = 1;
            var segment = segmentByMachine[machine.Uid];
            foreach (var interval in fragments)
            {
                var allocation = new ProductionMachineScheduleAllocation
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode!,
                    MachineCode = machine.MachineCode,
                    WorkOrderId = entity.Uid,
                    WorkOrderOperationId = operation.Uid,
                    WorkOrderMachineId = machine.Uid,
                    MachineScheduleSegmentId = segment.Uid,
                    ScheduleRevision = machine.ScheduleRevision,
                    FragmentNo = fragmentNo++,
                    StartDateTime = interval.Start,
                    EndDateTime = interval.End,
                    ScheduleSourceHash = machine.ScheduleSourceHash,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = createdBy,
                    RowVersion = IsSqlite(db) ? Guid.NewGuid().ToByteArray() : []
                };
                db.ProductionMachineScheduleAllocations.Add(allocation);
                allocationLinks.Add((allocation, segment));
                fragmentCount++;
            }
        }

        if (allocationLinks.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var (allocation, segment) in allocationLinks)
                MachineScheduleSegmentPersistence.AddAllocationLink(db, allocation, segment, scope.UserId, now);
        }

        return fragmentCount;
    }

    private sealed record DraftHeaderMutationResult(
        bool Changed,
        long? DeliveryRequestIdToReconcile = null);

    /// <summary>
    /// Validates the persisted snapshot, then applies permitted header edits on a tracked Draft.
    /// Does not save, commit, or roll back. A false <see cref="DraftHeaderMutationResult.Changed"/>
    /// means the caller may return without persisting; Update and Release must still continue.
    /// </summary>
    private async Task<DraftHeaderMutationResult> ApplyDraftHeaderChangesAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        decimal plannedQty,
        string? schedulingDirection,
        DateTime? scheduleAnchorDateTime,
        string? sourceReference,
        string? remark,
        CancellationToken cancellationToken)
    {
        if (!WorkOrderSnapshotHasher.MatchesStoredSnapshotHash(entity))
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SnapshotHashInvalid
                + ": The saved Work Order snapshot failed its integrity check.");
        }

        WorkOrderSnapshotHasher.CanonicalizeStoredTimestampKindHash(entity);

        var qtyChanged = entity.PlannedQty != plannedQty;
        var direction = string.IsNullOrWhiteSpace(schedulingDirection)
            ? entity.SchedulingDirection
            : Normalize(schedulingDirection);
        if (!ProductionSchedulingDirections.IsKnown(direction))
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                "Schedule direction must be FORWARD or BACKWARD.");
        }

        var anchor = scheduleAnchorDateTime ?? entity.ScheduleAnchorDateTime;
        var scheduleChanged = !string.Equals(entity.SchedulingDirection, direction, StringComparison.Ordinal)
            || entity.ScheduleAnchorDateTime != anchor;

        if (qtyChanged || scheduleChanged)
        {
            await WorkOrderSchedulingLock.AcquireAsync(
                db, scope.CompanyCode, exclusive: false, cancellationToken);
        }

        plannedQty = IvQty.Round(plannedQty);
        entity.PlannedQty = plannedQty;
        entity.RemainingQty = plannedQty;
        entity.SchedulingDirection = direction;
        entity.ScheduleAnchorDateTime = anchor;
        if (!IsDeliveryRequestWorkOrder(entity.SourceType))
        {
            entity.SourceReference = TrimTo(sourceReference, SourceReferenceMax);
        }
        entity.Remark = TrimTo(remark, RemarkMax);

        long? deliveryRequestIdToReconcile = null;
        if (qtyChanged && IsDeliveryRequestWorkOrder(entity.SourceType))
        {
            deliveryRequestIdToReconcile = await AdjustDeliveryRequestAllocationAsync(
                db, entity, scope, plannedQty, cancellationToken);
        }

        if (qtyChanged)
        {
            var quantities = await _quantities.CalculateAsync(entity, cancellationToken);
            if (!quantities.Succeeded)
            {
                throw new WorkOrderCommandException(IvMasterErrorCode.Validation, quantities.Summary);
            }
        }

        if (qtyChanged || scheduleChanged)
        {
            var scheduled = await _scheduler.ScheduleAsync(entity, cancellationToken);
            if (!scheduled.Succeeded)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation, scheduled.FailureCode + ": " + scheduled.FailureMessage);
            }
        }

        var nextHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(entity);
        if (string.Equals(nextHash, entity.SnapshotHash, StringComparison.Ordinal))
        {
            return new DraftHeaderMutationResult(false, deliveryRequestIdToReconcile);
        }

        entity.SnapshotHash = nextHash;
        entity.SnapshotRevision += 1;
        StampDraftAudit(entity, scope, ProductionAuditEventTypes.DraftUpdated, "Header updated.");
        TouchSqliteRowVersions(db, entity);
        return new DraftHeaderMutationResult(true, deliveryRequestIdToReconcile);
    }

    private sealed class BuiltSnapshot
    {
        public ProductionWorkOrder? WorkOrder { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];
        public string? Error { get; init; }
        public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<BuiltSnapshot> BuildCurrentSnapshotAsync(
        InventoryTenantScope scope,
        ProductionWorkOrderDraftRequest request,
        int snapshotRevision,
        DateTime? explicitScheduleAnchor,
        CancellationToken cancellationToken)
    {
        var normalizedRequest = new ProductionWorkOrderDraftRequest
        {
            ProductCode = request.ProductCode,
            DefinitionCode = request.DefinitionCode,
            PlannedQty = request.PlannedQty,
            PlannedStartDate = request.PlannedStartDate,
            PlannedCompletionDate = request.PlannedCompletionDate,
            SchedulingDirection = request.SchedulingDirection,
            SourceType = string.IsNullOrWhiteSpace(request.SourceType)
                ? ProductionSourceTypes.Manual
                : request.SourceType,
            SourceReference = request.SourceReference,
            Remark = request.Remark
        };
        var validationErrors = ValidateAndNormalize(
            normalizedRequest, out var normalized, allowDeliveryRequest: true);
        if (validationErrors.Count > 0)
        {
            return new BuiltSnapshot
            {
                Error = "Correct the highlighted fields.",
                ValidationErrors = validationErrors
            };
        }

        var plannerDate = string.Equals(
            normalized.SchedulingDirection,
            ProductionSchedulingDirections.Backward,
            StringComparison.Ordinal)
            ? normalized.PlannedCompletionDate
            : normalized.PlannedStartDate;
        var anchor = explicitScheduleAnchor
            ?? ProductionSchedulingDirections.NormalizePlannerDateAnchor(plannerDate, normalized.SchedulingDirection);

        var built = await _snapshotBuilder.BuildAsync(new WorkOrderSnapshotRequest
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode ?? string.Empty,
            LocationCode = scope.LocationCode,
            ProductCode = normalized.ProductCode,
            PlannedQty = normalized.PlannedQty,
            DefinitionCode = normalized.DefinitionCode,
            PlannedStartDateTime = normalized.PlannedStartDate,
            PlannedCompletionDateTime = normalized.PlannedCompletionDate,
            ScheduleAnchorDateTime = anchor,
            SchedulingDirection = normalized.SchedulingDirection,
            SourceType = normalized.SourceType,
            SourceReference = normalized.SourceReference,
            Remark = normalized.Remark,
            SnapshotRevision = snapshotRevision
        }, cancellationToken);

        if (!built.Succeeded || built.WorkOrder is null)
        {
            var message = string.IsNullOrWhiteSpace(built.FailureCode)
                ? built.FailureMessage
                : built.FailureCode + ": " + built.FailureMessage;
            return new BuiltSnapshot { Error = message, Warnings = built.Warnings };
        }

        var scheduled = await _scheduler.ScheduleAsync(built.WorkOrder, cancellationToken);
        if (!scheduled.Succeeded)
        {
            return new BuiltSnapshot
            {
                Error = scheduled.FailureCode + ": " + scheduled.FailureMessage,
                Warnings = built.Warnings
            };
        }

        built.WorkOrder.SnapshotHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(built.WorkOrder);
        built.WorkOrder.SnapshotHashVersion = ProductionSnapshotHashVersions.Current;
        return new BuiltSnapshot { WorkOrder = built.WorkOrder, Warnings = built.Warnings };
    }

    private async Task<ProductionWorkOrder> RequireDraftAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string? workOrderNo,
        byte[]? rowVersion,
        CancellationToken cancellationToken)
    {
        var entity = await LoadAggregateAsync(db, scope, Normalize(workOrderNo), tracking: true, cancellationToken)
            ?? throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "Work Order not found.");
        if (!ProductionWorkOrderRules.CanEdit(entity.Status))
        {
            throw new WorkOrderCommandException(IvMasterErrorCode.Validation, "Only a Draft Work Order can be changed.");
        }

        if (rowVersion is not { Length: > 0 })
        {
            throw new WorkOrderCommandException(IvMasterErrorCode.Concurrency, "The Work Order version is missing. Reload before saving.");
        }

        db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;
        return entity;
    }

    private static ProductionWorkOrderDraftRequest HeaderRequest(ProductionWorkOrder entity) => new()
    {
        WorkOrderNo = entity.WorkOrderNo,
        ProductCode = entity.ProductCode,
        DefinitionCode = entity.SourceDefinitionCode,
        PlannedQty = entity.PlannedQty,
        PlannedStartDate = entity.PlannedStartDateTime,
        PlannedCompletionDate = entity.PlannedCompletionDateTime,
        SchedulingDirection = entity.SchedulingDirection,
        SourceType = entity.SourceType,
        SourceReference = entity.SourceReference,
        Remark = entity.Remark
    };

    private static void ReplaceSnapshot(AppDbContext db, ProductionWorkOrder target, ProductionWorkOrder source)
    {
        RemoveSnapshotGraph(db, target);

        target.ProductCode = source.ProductCode;
        target.ProductDescription = source.ProductDescription;
        target.OutputUom = source.OutputUom;
        target.SourceDefinitionCode = source.SourceDefinitionCode;
        target.SourceDefinitionName = source.SourceDefinitionName;
        target.SourceBomHdrId = source.SourceBomHdrId;
        target.SourceBomVersion = source.SourceBomVersion;
        target.BomBaseQty = source.BomBaseQty;
        target.BomBaseUom = source.BomBaseUom;
        target.DefinitionEffectiveDate = source.DefinitionEffectiveDate;
        target.SourceEffectiveFrom = source.SourceEffectiveFrom;
        target.SourceProductDefinitionRevisionId = source.SourceProductDefinitionRevisionId;
        target.DefinitionSourceHash = source.DefinitionSourceHash;
        target.DefinitionSourceHashVersion = source.DefinitionSourceHashVersion;
        target.SnapshotFormatVersion = source.SnapshotFormatVersion;
        target.SnapshotHashVersion = source.SnapshotHashVersion;
        target.SnapshotHash = source.SnapshotHash;
        target.IsLegacySnapshot = false;
        target.LegacySnapshotReason = null;
        target.PlannedQty = source.PlannedQty;
        target.RemainingQty = source.PlannedQty;
        target.PlannedStartDateTime = source.PlannedStartDateTime;
        target.PlannedCompletionDateTime = source.PlannedCompletionDateTime;
        target.ScheduleAnchorDateTime = source.ScheduleAnchorDateTime;
        target.ScheduleCalculationTrace = source.ScheduleCalculationTrace;
        target.SchedulingDirection = source.SchedulingDirection;

        foreach (var step in source.RouteSteps)
        {
            target.RouteSteps.Add(step);
        }

        foreach (var operation in source.Operations)
        {
            target.Operations.Add(operation);
        }

        foreach (var material in source.Materials)
        {
            target.Materials.Add(material);
        }
    }

    private static void RemoveSnapshotGraph(AppDbContext db, ProductionWorkOrder target)
    {
        var operations = target.Operations
            .Concat(target.RouteSteps.SelectMany(x => x.Operations))
            .Distinct()
            .ToList();
        var machines = operations
            .SelectMany(x => x.Machines)
            .Distinct()
            .ToList();
        var directLabours = operations
            .SelectMany(x => x.Labours)
            .Distinct()
            .ToList();
        var machineLabours = machines
            .SelectMany(x => x.Labours)
            .Distinct()
            .ToList();
        var materials = target.Materials
            .Concat(operations.SelectMany(x => x.Materials))
            .Distinct()
            .ToList();
        var resources = operations
            .SelectMany(x => x.Resources)
            .Distinct()
            .ToList();

        // Several aggregate relationships intentionally use NoAction. Keep this order aligned
        // with the SQL Server FK graph: operation labours, machine labours, materials, machines,
        // resources, operations, then route steps.
        db.ProductionWorkOrderLabours.RemoveRange(directLabours);
        db.ProductionWorkOrderLabours.RemoveRange(machineLabours);
        db.ProductionWorkOrderMaterials.RemoveRange(materials);
        db.ProductionWorkOrderMachines.RemoveRange(machines);
        db.ProductionWorkOrderResources.RemoveRange(resources);
        db.ProductionWorkOrderOperations.RemoveRange(operations);
        db.ProductionWorkOrderRouteSteps.RemoveRange(target.RouteSteps);

        foreach (var operation in operations)
        {
            operation.Labours.Clear();
            foreach (var machine in operation.Machines)
            {
                machine.Labours.Clear();
            }

            operation.Machines.Clear();
            operation.Materials.Clear();
            operation.Resources.Clear();
        }

        foreach (var routeStep in target.RouteSteps)
        {
            routeStep.Operations.Clear();
        }

        target.RouteSteps.Clear();
        target.Operations.Clear();
        target.Materials.Clear();
    }

    private static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed) DiffSnapshot(
        ProductionWorkOrder current,
        ProductionWorkOrder next)
    {
        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<string>();

        Diff(
            current.RouteSteps.Select(s => (Key: s.SourceRouteStepKey?.ToString("D") ?? s.WorkCentreCode + ":" + s.StageSequence, Label: $"Route {s.StageSequence} {s.WorkCentreCode}", Sig: $"{s.OutputItemCode}|{s.OutputType}|{s.OutputBaseQty}|{s.OutputUom}|{s.OutputBaseUom}|{s.OutputConversionFactorToBase}|{s.YieldPercent}")),
            next.RouteSteps.Select(s => (Key: s.SourceRouteStepKey?.ToString("D") ?? s.WorkCentreCode + ":" + s.StageSequence, Label: $"Route {s.StageSequence} {s.WorkCentreCode}", Sig: $"{s.OutputItemCode}|{s.OutputType}|{s.OutputBaseQty}|{s.OutputUom}|{s.OutputBaseUom}|{s.OutputConversionFactorToBase}|{s.YieldPercent}")),
            added, removed, changed);
        Diff(
            current.Operations.Select(o => (Key: o.SourceOperationKey?.ToString("D") ?? o.OperationCode, Label: $"Operation {o.OperationCode}", Sig: $"{o.ProcessType}|{o.StandardDurationMinutes}|{o.IsFinalOperation}")),
            next.Operations.Select(o => (Key: o.SourceOperationKey?.ToString("D") ?? o.OperationCode, Label: $"Operation {o.OperationCode}", Sig: $"{o.ProcessType}|{o.StandardDurationMinutes}|{o.IsFinalOperation}")),
            added, removed, changed);
        Diff(
            current.Materials.Select(m => (Key: m.ComponentCode + ":" + m.MaterialSequence, Label: $"Material {m.ComponentCode}", Sig: $"{m.ComponentQtyPerParent}|{m.ScrapPercent}|{m.IssueMethod}|{m.SupplySource}")),
            next.Materials.Select(m => (Key: m.ComponentCode + ":" + m.MaterialSequence, Label: $"Material {m.ComponentCode}", Sig: $"{m.ComponentQtyPerParent}|{m.ScrapPercent}|{m.IssueMethod}|{m.SupplySource}")),
            added, removed, changed);
        return (added, removed, changed);
    }

    private static void Diff(
        IEnumerable<(string Key, string Label, string Sig)> current,
        IEnumerable<(string Key, string Label, string Sig)> next,
        List<string> added,
        List<string> removed,
        List<string> changed)
    {
        var left = current.GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var right = next.GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var key in right.Keys.Except(left.Keys, StringComparer.Ordinal))
        {
            added.Add(right[key].Label);
        }

        foreach (var key in left.Keys.Except(right.Keys, StringComparer.Ordinal))
        {
            removed.Add(left[key].Label);
        }

        foreach (var key in left.Keys.Intersect(right.Keys, StringComparer.Ordinal))
        {
            if (!string.Equals(left[key].Sig, right[key].Sig, StringComparison.Ordinal))
            {
                changed.Add(right[key].Label);
            }
        }
    }

    private static void StampDraftAudit(ProductionWorkOrder entity, InventoryTenantScope scope, string eventType, string reason)
    {
        var now = DateTime.UtcNow;
        entity.ModifiedDate = now;
        entity.ModifiedBy = scope.UserId;
        entity.AuditEvents.Add(new ProductionAuditEvent
        {
            EventType = eventType,
            FromStatus = ProductionWorkOrderStatuses.Draft,
            ToStatus = ProductionWorkOrderStatuses.Draft,
            SnapshotRevision = entity.SnapshotRevision,
            Reason = reason,
            OccurredDate = now,
            ActorUserId = scope.UserId
        });
    }

    private static void RequireCurrentSnapshot(ProductionWorkOrder entity)
    {
        if (!ProductionSnapshotFormatVersions.IsCurrentForDraft(
                entity.SnapshotFormatVersion, entity.SnapshotHashVersion))
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                + ": Refresh Definition to upgrade the Work Order snapshot/costing contract before editing or re-releasing.");
        }
    }

    private static void RequireSnapshotFingerprint(ProductionWorkOrder entity, int snapshotRevision, string? snapshotHash)
    {
        if (entity.SnapshotRevision != snapshotRevision
            || !string.Equals(entity.SnapshotHash, snapshotHash, StringComparison.Ordinal))
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Concurrency,
                ProductionReadinessErrorCodes.SnapshotStale + ": The Draft changed after this preview. Reload it.");
        }
    }

    private static void ClearMachineScheduleProvenance(ProductionWorkOrderMachine machine)
    {
        machine.PlannedStartDateTime = null;
        machine.PlannedCompletionDateTime = null;
        machine.CalendarSourceId = null;
        machine.CalendarSourceLastModified = null;
        machine.ScheduleSourceHash = null;
        machine.CalendarHorizonStart = null;
        machine.CalendarHorizonEnd = null;
    }

    private static void ClearOperationScheduleProvenance(ProductionWorkOrderOperation operation)
    {
        operation.CalendarSourceType = null;
        operation.CalendarSourceId = null;
        operation.CalendarSourceLastModified = null;
        operation.ScheduleSourceHash = null;
        operation.CalendarHorizonStart = null;
        operation.CalendarHorizonEnd = null;
        operation.PlannedStartDateTime = null;
        operation.PlannedCompletionDateTime = null;
    }

    private static void SyncMachineLabourContribution(ProductionWorkOrderOperation operation)
    {
        foreach (var machine in operation.Machines)
        {
            foreach (var labour in machine.Labours)
            {
                labour.ContributesToPlan = machine.IsSelected;
            }
        }

        foreach (var labour in operation.Labours)
        {
            if (labour.MachineId is long machineId)
            {
                var owner = operation.Machines.FirstOrDefault(m => m.Uid == machineId);
                if (owner is not null
                    && string.Equals(labour.RateBasis, ProductionLabourRateBases.PerOutputUnit, StringComparison.Ordinal))
                {
                    labour.ContributesToPlan = owner.IsSelected;
                }
            }
        }
    }

    private static IvMasterOperationResult<T> Concurrency<T>() =>
        IvMasterOperationResult<T>.Fail(
            IvMasterErrorCode.Concurrency,
            ProductionReadinessErrorCodes.ConcurrencyConflict + ": This Work Order was changed by another user. Reload it.");
}
