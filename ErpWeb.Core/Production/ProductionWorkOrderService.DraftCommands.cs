using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService
{
    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateDraftAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDraftRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var scope = auth.Scope!;
        var built = await BuildCurrentSnapshotAsync(scope, request, snapshotRevision: 1, cancellationToken);
        if (built.WorkOrder is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.Validation, built.Error ?? "The snapshot could not be built.");
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
            var entity = await RequireDraftAsync(db, auth.Scope!, request.WorkOrderNo, request.RowVersion, cancellationToken);
            if (entity.SnapshotFormatVersion < ProductionSnapshotFormatVersions.Current)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                    + ": Refresh this legacy Draft from its Product Definition before editing it.");
            }

            var qtyChanged = entity.PlannedQty != request.PlannedQty;
            var direction = string.IsNullOrWhiteSpace(request.SchedulingDirection)
                ? entity.SchedulingDirection
                : Normalize(request.SchedulingDirection);
            var anchor = request.ScheduleAnchorDateTime ?? entity.ScheduleAnchorDateTime;
            var scheduleChanged = !string.Equals(entity.SchedulingDirection, direction, StringComparison.Ordinal)
                || entity.ScheduleAnchorDateTime != anchor;

            entity.PlannedQty = request.PlannedQty;
            entity.RemainingQty = request.PlannedQty;
            entity.SchedulingDirection = direction;
            entity.ScheduleAnchorDateTime = anchor;
            entity.SourceReference = TrimTo(request.SourceReference, SourceReferenceMax);
            entity.Remark = TrimTo(request.Remark, RemarkMax);

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
                    throw new WorkOrderCommandException(IvMasterErrorCode.Validation, scheduled.FailureCode + ": " + scheduled.FailureMessage);
                }
            }

            var nextHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(entity);
            if (string.Equals(nextHash, entity.SnapshotHash, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
            }

            entity.SnapshotHash = nextHash;
            entity.SnapshotRevision += 1;
            StampDraftAudit(entity, auth.Scope!, ProductionAuditEventTypes.DraftUpdated, "Header updated.");
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

        var built = await BuildCurrentSnapshotAsync(auth.Scope!, HeaderRequest(entity), entity.SnapshotRevision, cancellationToken);
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
            var built = await BuildCurrentSnapshotAsync(
                auth.Scope!, HeaderRequest(entity), entity.SnapshotRevision + 1, cancellationToken);
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
            var entity = await LoadAggregateAsync(db, scope, Normalize(request.WorkOrderNo), tracking: true, cancellationToken);
            if (entity is null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            return await FinishCurrentReleaseAsync(db, tx, entity, scope, request, cancellationToken);
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

    private async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> FinishCurrentReleaseAsync(
        AppDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        ProductionWorkOrderReleaseRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.ReleaseEnabled)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.ReleaseDisabled + ": Release is disabled by configuration.");
        }

        if (!ProductionWorkOrderRules.CanRelease(entity.Status))
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation, "Only a Draft Work Order can be released.");
        }

        await WorkOrderSchedulingLock.AcquireAsync(db, scope.CompanyCode, exclusive: false, cancellationToken);

        if (request.RowVersion is not { Length: > 0 })
        {
            throw new WorkOrderCommandException(IvMasterErrorCode.Concurrency, "The Work Order version is missing. Reload before release.");
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
            OccurredDate = now,
            ActorUserId = scope.UserId
        });
        TouchSqliteRowVersions(db, entity);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
    }

    private sealed class BuiltSnapshot
    {
        public ProductionWorkOrder? WorkOrder { get; init; }
        public string? Error { get; init; }
    }

    private async Task<BuiltSnapshot> BuildCurrentSnapshotAsync(
        InventoryTenantScope scope,
        ProductionWorkOrderDraftRequest request,
        int snapshotRevision,
        CancellationToken cancellationToken)
    {
        if (request.PlannedQty <= 0m)
        {
            return new BuiltSnapshot { Error = "Planned quantity must be positive." };
        }

        var effective = request.SnapshotAsOfDate == default
            ? request.PlannedStartDate.Date
            : request.SnapshotAsOfDate.Date;
        var built = await _snapshotBuilder.BuildAsync(new WorkOrderSnapshotRequest
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode ?? string.Empty,
            LocationCode = scope.LocationCode,
            ProductCode = request.ProductCode,
            PlannedQty = request.PlannedQty,
            DefinitionEffectiveDate = effective,
            PlannedStartDateTime = request.PlannedStartDate,
            PlannedCompletionDateTime = request.PlannedCompletionDate,
            SchedulingDirection = string.IsNullOrWhiteSpace(request.SchedulingDirection)
                ? ProductionSchedulingDirections.Forward
                : Normalize(request.SchedulingDirection),
            SourceType = string.IsNullOrWhiteSpace(request.SourceType) ? ProductionSourceTypes.Manual : Normalize(request.SourceType),
            SourceReference = TrimTo(request.SourceReference, SourceReferenceMax),
            Remark = TrimTo(request.Remark, RemarkMax),
            SnapshotRevision = snapshotRevision
        }, cancellationToken);

        if (!built.Succeeded || built.WorkOrder is null)
        {
            var message = string.IsNullOrWhiteSpace(built.FailureCode)
                ? built.FailureMessage
                : built.FailureCode + ": " + built.FailureMessage;
            return new BuiltSnapshot { Error = message };
        }

        var scheduled = await _scheduler.ScheduleAsync(built.WorkOrder, cancellationToken);
        if (!scheduled.Succeeded)
        {
            return new BuiltSnapshot { Error = scheduled.FailureCode + ": " + scheduled.FailureMessage };
        }

        built.WorkOrder.SnapshotHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(built.WorkOrder);
        built.WorkOrder.SnapshotHashVersion = ProductionSnapshotHashVersions.Current;
        return new BuiltSnapshot { WorkOrder = built.WorkOrder };
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
        PlannedQty = entity.PlannedQty,
        SnapshotAsOfDate = entity.DefinitionEffectiveDate,
        PlannedStartDate = entity.PlannedStartDateTime,
        PlannedCompletionDate = entity.PlannedCompletionDateTime,
        SchedulingDirection = entity.SchedulingDirection,
        SourceType = entity.SourceType,
        SourceReference = entity.SourceReference,
        Remark = entity.Remark
    };

    private static void ReplaceSnapshot(AppDbContext db, ProductionWorkOrder target, ProductionWorkOrder source)
    {
        db.ProductionWorkOrderLabours.RemoveRange(target.Operations.SelectMany(o => o.Labours));
        db.ProductionWorkOrderLabours.RemoveRange(target.Operations.SelectMany(o => o.Machines).SelectMany(m => m.Labours));
        db.ProductionWorkOrderMaterials.RemoveRange(target.Materials);
        db.ProductionWorkOrderMachines.RemoveRange(target.Operations.SelectMany(o => o.Machines));
        db.ProductionWorkOrderResources.RemoveRange(target.Operations.SelectMany(o => o.Resources));
        db.ProductionWorkOrderOperations.RemoveRange(target.Operations);
        db.ProductionWorkOrderRouteSteps.RemoveRange(target.RouteSteps);
        foreach (var operation in target.Operations)
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

        target.RouteSteps.Clear();
        target.Operations.Clear();
        target.Materials.Clear();

        target.ProductCode = source.ProductCode;
        target.ProductDescription = source.ProductDescription;
        target.OutputUom = source.OutputUom;
        target.SourceBomHdrId = source.SourceBomHdrId;
        target.SourceBomVersion = source.SourceBomVersion;
        target.BomBaseQty = source.BomBaseQty;
        target.BomBaseUom = source.BomBaseUom;
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

    private static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed) DiffSnapshot(
        ProductionWorkOrder current,
        ProductionWorkOrder next)
    {
        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<string>();

        Diff(
            current.RouteSteps.Select(s => (Key: s.SourceRouteStepKey?.ToString("D") ?? s.WorkCentreCode + ":" + s.StageSequence, Label: $"Route {s.StageSequence} {s.WorkCentreCode}", Sig: $"{s.OutputItemCode}|{s.OutputBaseQty}")),
            next.RouteSteps.Select(s => (Key: s.SourceRouteStepKey?.ToString("D") ?? s.WorkCentreCode + ":" + s.StageSequence, Label: $"Route {s.StageSequence} {s.WorkCentreCode}", Sig: $"{s.OutputItemCode}|{s.OutputBaseQty}")),
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

    private static IvMasterOperationResult<T> Concurrency<T>() =>
        IvMasterOperationResult<T>.Fail(
            IvMasterErrorCode.Concurrency,
            ProductionReadinessErrorCodes.ConcurrencyConflict + ": This Work Order was changed by another user. Reload it.");
}
