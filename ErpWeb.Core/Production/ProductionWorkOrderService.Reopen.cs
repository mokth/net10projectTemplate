using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService
{
    private static readonly string[] ReopenBlockingChangeOrderStatuses =
    [
        ProductionChangeOrderStatuses.Draft,
        ProductionChangeOrderStatuses.Requested,
        ProductionChangeOrderStatuses.Approved,
        ProductionChangeOrderStatuses.Applied
    ];

    /// <summary>
    /// Returns a Released Work Order to Draft when no production execution has started.
    /// Concurrency: do not UPDLOCK PostingLinks before the Work Order — new MI drafts lock WO then
    /// insert a PostingLink. Use a read-only PostingLink preflight, lock WO, then re-check.
    /// When daily output posts onto this aggregate, extend the guard with WorkOrderID/OperationID
    /// facts; do not infer blockers from legacy PrSchDailyProd.ScheCode text.
    /// </summary>
    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReopenForEditAsync(
        ProductionWorkOrderReopenRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderReopenRequest();

        var auth = await AuthorizeAsync(PermissionCodes.Reopen, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var number = Normalize(request.WorkOrderNo);
        var cleanReason = (request.Reason ?? string.Empty).Trim();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (number.Length == 0)
        {
            errors["WorkOrderNo"] = "Work Order number is required.";
        }

        if (cleanReason.Length == 0)
        {
            errors["Reason"] = "Reopen reason is required.";
        }
        else if (cleanReason.Length > CancellationReasonMax)
        {
            errors["Reason"] = $"Reopen reason must be {CancellationReasonMax} characters or fewer.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation, "Correct the highlighted fields.", errors);
        }

        if (request.RowVersion is not { Length: > 0 })
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency,
                "The Work Order version is missing. Reload before reopening.");
        }

        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var identity = await db.ProductionWorkOrders
                .AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderNo == number)
                .Select(x => new { x.Uid, x.WorkOrderNo })
                .SingleOrDefaultAsync(cancellationToken);
            if (identity is null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            await LockDemandBridgeBeforeWorkOrderAsync(
                db, scope, number, cancellationToken);
            var preflightBlocker = await GetReopenPostingLinkBlockerAsync(db, identity.Uid, cancellationToken);
            if (preflightBlocker is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.InUse, preflightBlocker);
            }

            // Physical machine app locks follow the demand bridge and precede the Work Order
            // aggregate, matching Release and planner commit. Holding them while the released
            // Work Order returns to Draft makes removing its capacity allocations atomic with
            // any concurrent placement on the same machine (plan §15.2).
            var reopenMachineCodes = await db.ProductionWorkOrderMachines.AsNoTracking()
                .Where(x => x.Operation != null
                    && x.Operation.WorkOrderId == identity.Uid
                    && x.IsSelected)
                .Select(x => x.MachineCode)
                .ToListAsync(cancellationToken);
            var reopenOwnedAllocationCodes = await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderId == identity.Uid)
                .Select(x => x.MachineCode)
                .Distinct()
                .ToListAsync(cancellationToken);
            var reopenLockCodes = reopenMachineCodes
                .Concat(reopenOwnedAllocationCodes)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Normalize)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            try
            {
                await ProductionMachineScheduleLock.AcquireManyAsync(
                    db, scope.CompanyCode, reopenLockCodes, cancellationToken);
            }
            catch (ProductionMachineScheduleLockException ex)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Concurrency, ex.Message);
            }

            await WorkOrderSchedulingLock.AcquireAsync(
                db, scope.CompanyCode, exclusive: false, cancellationToken);
            try
            {
                await ProductionWorkOrderExecutionLock.AcquireAsync(
                    db, scope.CompanyCode, scope.BranchCode!, identity.Uid, cancellationToken);
            }
            catch (ProductionWorkOrderExecutionLockException ex)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Concurrency, ex.Message);
            }

            var order = await LockWorkOrderForLifecycleAsync(
                db, scope.CompanyCode, scope.BranchCode!, number, cancellationToken);
            if (order is null
                || !string.Equals(order.CompanyCode, scope.CompanyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(order.BranchCode, scope.BranchCode, StringComparison.OrdinalIgnoreCase))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            if (!ProductionWorkOrderRules.CanReopen(order.Status))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Validation,
                    "Only a Released Work Order can be reopened for editing.");
            }

            if (!order.RowVersion.SequenceEqual(request.RowVersion))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Concurrency,
                    "This Work Order was changed by another user. Reload the latest version before reopening.");
            }

            db.Entry(order).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

            var blocker = await EnsureNoReopenBlockersAsync(db, order, cancellationToken);
            if (blocker is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.InUse, blocker);
            }

            // Reopen needs the full manufacturing graph to verify the frozen released snapshot.
            var aggregate = await LoadAggregateAsync(db, scope, order.WorkOrderNo, tracking: true, cancellationToken);
            if (aggregate is not null)
                order = aggregate;

            var reopenSegments = await db.ProductionMachineScheduleSegments
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderId == order.Uid
                    && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
                .ToListAsync(cancellationToken);
            var hasAdvancedSegments = reopenSegments
                .GroupBy(x => x.WorkOrderMachineId)
                .Any(group => group.Count() > 1)
                || reopenSegments.Any(x => !string.Equals(
                    x.SegmentKind, ProductionMachineScheduleSegmentKinds.Original, StringComparison.Ordinal));
            var reopenSets = await db.ProductionMachineScheduleSegmentSets
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderId == order.Uid
                    && x.IsCurrent)
                .ToListAsync(cancellationToken);
            if (hasAdvancedSegments || reopenSets.Any(x =>
                    x.Mode != ProductionMachineScheduleSegmentSetModes.FullPlan
                    || x.PlanningState != ProductionMachineSchedulePlanningStates.Scheduled))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.InUse,
                    "Advanced-scheduled Work Order cannot be reopened safely. "
                    + "Resolve through the controlled scheduling lifecycle first.");
            }

            if (!string.Equals(order.SnapshotHash,
                    WorkOrderSnapshotHasher.ComputeSnapshotHash(order), StringComparison.Ordinal))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.InUse,
                    "The released Work Order snapshot no longer matches its manufacturing facts. "
                    + "Resolve the discrepancy before reopening.");
            }

            // Removing the released capacity reservation is part of the same transaction that
            // returns the Work Order to Draft: a Draft must never keep active allocations that
            // would block releases on the same physical machine (plan §17).
            var lockedReopenCodes = reopenLockCodes.ToHashSet(StringComparer.Ordinal);
            var allocationCodesNow = await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderId == order.Uid)
                .Select(x => x.MachineCode)
                .Distinct()
                .ToListAsync(cancellationToken);
            var unlockedReopenCode = allocationCodesNow
                .Select(Normalize)
                .FirstOrDefault(code => !lockedReopenCodes.Contains(code));
            if (unlockedReopenCode is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Concurrency,
                    $"Work Order {order.WorkOrderNo} gained an allocation on unlocked machine {unlockedReopenCode}. "
                    + "Retry the reopen.");
            }

            var reopenAllocations = await db.ProductionMachineScheduleAllocations
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderId == order.Uid)
                .ToListAsync(cancellationToken);
            if (reopenAllocations.Count > 0)
            {
                await MachineScheduleSegmentPersistence.RemoveAllocationLinksAsync(
                    db, reopenAllocations, cancellationToken);
                db.ProductionMachineScheduleAllocations.RemoveRange(reopenAllocations);
            }

            var now = DateTime.UtcNow;
            MachineScheduleSegmentPersistence.SupersedeSets(reopenSets, scope.UserId, now);

            MachineScheduleSegmentPersistence.Supersede(
                reopenSegments, scope.UserId, now,
                "Reopen returned the Work Order to the standard Draft schedule.");

            // The released snapshot was verified above. A simple Reopen changes only lifecycle
            // fields and keeps the verified hash; Draft edits will calculate their own revision.
            var oldHash = order.SnapshotHash;
            var oldSnapshotRevision = order.SnapshotRevision;
            order.SnapshotRevision += 1;

            // Lifecycle transition only — do not RequireCurrentSnapshot or rebuild/upgrade snapshot.
            order.Status = ProductionWorkOrderStatuses.Draft;
            order.ReleasedDate = null;
            order.ReleasedBy = null;
            order.ModifiedDate = now;
            order.ModifiedBy = scope.UserId;

            if (IsDeliveryRequestWorkOrder(order.SourceType))
            {
                var (deliveryRequest, _) = await LockRequiredDeliveryRequestAllocationAsync(
                    db, order, scope, deliveryRequestId: null, cancellationToken);
                await ReconcileDeliveryRequestStatusAsync(
                    db, deliveryRequest, scope, cancellationToken);
            }

            db.ProductionAuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrderId = order.Uid,
                EventType = ProductionAuditEventTypes.ReopenedForEdit,
                FromStatus = ProductionWorkOrderStatuses.Released,
                ToStatus = ProductionWorkOrderStatuses.Draft,
                SnapshotRevision = order.SnapshotRevision,
                Reason = cleanReason,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    OldSnapshotRevision = oldSnapshotRevision,
                    NewSnapshotRevision = order.SnapshotRevision,
                    OldSnapshotHash = oldHash,
                    NewSnapshotHash = order.SnapshotHash,
                    NormalizedFromSegments = hasAdvancedSegments,
                    SupersededSegmentCount = reopenSegments.Count
                }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });

            TouchSqliteRowVersions(db, order);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var refreshed = await LoadAggregateAsync(db, scope, order.WorkOrderNo, tracking: false, cancellationToken);
            return refreshed is null
                ? IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound,
                    "Work Order was not found after reopening.")
                : IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(refreshed));
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
                "This Work Order was changed by another user. Reload the latest version before reopening.");
        }
    }

    private static async Task<string?> GetReopenPostingLinkBlockerAsync(
        AppDbContext db,
        long workOrderId,
        CancellationToken cancellationToken)
    {
        var statuses = await db.ProductionPostingLinks
            .AsNoTracking()
            .Where(x => x.WorkOrderId == workOrderId)
            .Select(x => x.Status)
            .Distinct()
            .ToListAsync(cancellationToken);

        return DescribePostingLinkBlocker(statuses);
    }

    private static string? DescribePostingLinkBlocker(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Any(s => string.Equals(s, ProductionPostingLinkStatuses.Draft, StringComparison.OrdinalIgnoreCase)))
        {
            return "A Material Issue draft exists for this Work Order. Cancel the Material Issue draft before reopening the Work Order.";
        }

        if (statuses.Any(s => string.Equals(s, ProductionPostingLinkStatuses.Pending, StringComparison.OrdinalIgnoreCase)))
        {
            return "A production posting is in progress for this Work Order. Retry reopening after it resolves.";
        }

        if (statuses.Any(s =>
                string.Equals(s, ProductionPostingLinkStatuses.Succeeded, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, ProductionPostingLinkStatuses.Reversed, StringComparison.OrdinalIgnoreCase)))
        {
            return "This Work Order has production execution history and cannot be reopened as Draft. Use the controlled Change Order/correction process.";
        }

        return null;
    }

    private static async Task<ProductionWorkOrder?> LockWorkOrderForLifecycleAsync(
        AppDbContext db,
        string company,
        string branch,
        string workOrderNo,
        CancellationToken cancellationToken)
    {
        return db.Database.IsSqlServer()
            ? await db.ProductionWorkOrders
                .FromSqlInterpolated($@"
                    SELECT *
                    FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK)
                    WHERE CompanyCode = {company}
                      AND BranchCode = {branch}
                      AND WorkOrderNo = {workOrderNo}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.ProductionWorkOrders
                .SingleOrDefaultAsync(
                    x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.WorkOrderNo == workOrderNo,
                    cancellationToken);
    }

    private static async Task<string?> EnsureNoReopenBlockersAsync(
        AppDbContext db,
        ProductionWorkOrder order,
        CancellationToken cancellationToken)
    {
        var procurementBlocker = await GetLinkedProcurementBlockerAsync(db, order, cancellationToken);
        if (procurementBlocker is not null)
        {
            return procurementBlocker;
        }

        var postingBlocker = await GetReopenPostingLinkBlockerAsync(db, order.Uid, cancellationToken);
        if (postingBlocker is not null)
        {
            return postingBlocker;
        }

        if (await HasWorkOrderExecutionProjectionAsync(db, order, cancellationToken))
        {
            return "This Work Order has production execution history and cannot be reopened as Draft. Use the controlled Change Order/correction process.";
        }

        var hasOpenChangeOrder = await db.ProductionChangeOrders
            .AsNoTracking()
            .AnyAsync(
                x => x.WorkOrderId == order.Uid
                    && ReopenBlockingChangeOrderStatuses.Contains(x.Status),
                cancellationToken);
        if (hasOpenChangeOrder)
        {
            return "An active or applied Change Order exists for this Work Order. Resolve it before reopening as Draft.";
        }

        var hasActiveHold = await db.ProductionWorkOrderScheduleHolds
            .AsNoTracking()
            .AnyAsync(
                x => x.WorkOrderId == order.Uid
                    && x.Status == ProductionScheduleHoldStatuses.Active,
                cancellationToken);
        if (hasActiveHold)
        {
            return "An active schedule hold exists for this Work Order. Release or cancel the hold before reopening as Draft.";
        }

        return null;
    }

    private static async Task<string?> GetLinkedProcurementBlockerAsync(
        AppDbContext db,
        ProductionWorkOrder order,
        CancellationToken cancellationToken)
    {
        var materialIds = await db.ProductionWorkOrderMaterials.AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid)
            .Select(x => x.Uid)
            .ToListAsync(cancellationToken);
        if (materialIds.Count == 0)
        {
            return null;
        }

        var linked = await db.PoPrDetails.AsNoTracking()
            .Where(x => x.CompanyCode == order.CompanyCode
                && x.BranchCode == order.BranchCode
                && x.WorkOrderMaterialId.HasValue
                && materialIds.Contains(x.WorkOrderMaterialId.Value))
            .OrderBy(x => x.PrNo)
            .ThenBy(x => x.Line)
            .Select(x => new { x.PrNo, x.Line, x.WorkOrderMaterialId })
            .FirstOrDefaultAsync(cancellationToken);
        return linked is null
            ? null
            : $"Work Order material {linked.WorkOrderMaterialId} is referenced by PR {linked.PrNo} line {linked.Line}. Resolve the linked procurement line before changing or deleting the Work Order material snapshot.";
    }

    private static async Task<bool> HasWorkOrderExecutionProjectionAsync(
        AppDbContext db,
        ProductionWorkOrder order,
        CancellationToken cancellationToken)
    {
        var hasMovement = await db.ProductionMaterialMovements
            .AsNoTracking()
            .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken);
        if (hasMovement || HasHeaderExecutionProjection(order))
        {
            return true;
        }

        var operations = await db.ProductionWorkOrderOperations
            .AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid)
            .Select(x => new
            {
                x.InputQty,
                x.ProcessedQty,
                x.GoodQty,
                x.ScrapQty,
                x.RejectQty,
                x.HoldQty,
                x.ReworkQty,
                x.TransferredQty
            })
            .ToListAsync(cancellationToken);
        if (operations.Any(x => HasExecutionProjection(
                x.InputQty, x.ProcessedQty, x.GoodQty, x.ScrapQty,
                x.RejectQty, x.HoldQty, x.ReworkQty, x.TransferredQty)))
        {
            return true;
        }

        var materials = await db.ProductionWorkOrderMaterials
            .AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid)
            .Select(x => new
            {
                x.ReservedQty,
                x.PickedQty,
                x.IssuedQty,
                x.ReturnedQty,
                x.ConsumedQty,
                x.VarianceQty
            })
            .ToListAsync(cancellationToken);
        return materials.Any(x => HasExecutionProjection(
            x.ReservedQty, x.PickedQty, x.IssuedQty,
            x.ReturnedQty, x.ConsumedQty, x.VarianceQty));
    }

    private static bool HasHeaderExecutionProjection(ProductionWorkOrder order) =>
        HasExecutionProjection(
            order.GoodQty,
            order.ScrapQty,
            order.RejectQty,
            order.HoldQty,
            order.ApprovedVarianceQty);

    private static bool HasExecutionProjection(params decimal[] quantities) =>
        quantities.Any(q => q != 0m);
}
