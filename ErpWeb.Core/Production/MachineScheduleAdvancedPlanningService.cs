using System.Security.Cryptography;
using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Server-authoritative Preview/Commit for Insert/Split, Hold/Resume, Replan Remaining and
/// Replan After Maintenance. Every commit re-builds the proposal under deterministic machine and
/// Work Order locks, compares a preview token, then persists planning data only.
/// </summary>
public sealed partial class MachineScheduleAdvancedPlanningService : IMachineScheduleAdvancedPlanningService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly IAppSettingService _settings;
    private readonly IMachineSchedulePlannerReadinessService _readiness;
    private readonly IProductionCalendarScheduleDataLoader _machineCalendars;
    private readonly IWorkOrderCalendarProvider _calendarProvider;
    private readonly IProductionMachineCapacityService _capacity;

    public MachineScheduleAdvancedPlanningService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        IAppSettingService settings,
        IMachineSchedulePlannerReadinessService readiness,
        IProductionCalendarScheduleDataLoader machineCalendars,
        IWorkOrderCalendarProvider calendarProvider,
        IProductionMachineCapacityService capacity)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _settings = settings;
        _readiness = readiness;
        _machineCalendars = machineCalendars;
        _calendarProvider = calendarProvider;
        _capacity = capacity;
    }

    public Task<IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewInsertAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        PreviewAsync(request with { Action = MachineScheduleAdvancedAction.InsertSplit }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitInsertAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        CommitAsync(request with { Action = MachineScheduleAdvancedAction.InsertSplit }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        PreviewAsync(request with { Action = MachineScheduleAdvancedAction.Hold }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        CommitAsync(request with { Action = MachineScheduleAdvancedAction.Hold }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewReleaseHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        PreviewAsync(request with { Action = MachineScheduleAdvancedAction.ReleaseHold }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitReleaseHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        CommitAsync(request with { Action = MachineScheduleAdvancedAction.ReleaseHold }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewReplanRemainingAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        PreviewAsync(request with { Action = MachineScheduleAdvancedAction.ReplanRemaining }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitReplanRemainingAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        CommitAsync(request with { Action = MachineScheduleAdvancedAction.ReplanRemaining }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewMaintenanceReplanAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        PreviewAsync(request with { Action = MachineScheduleAdvancedAction.MaintenanceReplan }, cancellationToken);

    public Task<IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitMaintenanceReplanAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default) =>
        CommitAsync(request with { Action = MachineScheduleAdvancedAction.MaintenanceReplan }, cancellationToken);

    private async Task<IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewAsync(
        MachineScheduleAdvancedRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleAdvancedPreview>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);
        var invalid = ValidateRequest(request, requireToken: false);
        if (invalid is not null)
            return IvMasterOperationResult<MachineScheduleAdvancedPreview>.Fail(IvMasterErrorCode.Validation, invalid);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var order = await LoadOrderAsync(db, authorization.Scope!, request.WorkOrderId, tracking: false, cancellationToken);
        if (order is null)
            return IvMasterOperationResult<MachineScheduleAdvancedPreview>.Fail(
                IvMasterErrorCode.NotFound, "Work Order was not found in the current company and branch.");
        var versionError = ValidateExpectedVersions(order, request);
        if (versionError is not null)
            return IvMasterOperationResult<MachineScheduleAdvancedPreview>.Fail(IvMasterErrorCode.Concurrency, versionError);

        var writeCheck = await CheckWritesAsync(cancellationToken);
        var builder = new MachineScheduleAdvancedPlanBuilder(
            db, authorization.Scope!, _capacity,
            new MachineScheduleAdvancedPlacementEngine(_machineCalendars, _calendarProvider),
            tracking: false);
        var plan = await builder.BuildAsync(request, cancellationToken);
        var preview = plan.Preview;
        if (writeCheck.Error is not null)
            preview.Blockers = preview.Blockers.Append(writeCheck.Error).Distinct(StringComparer.Ordinal).ToList();
        if (plan.CanCommit || writeCheck.Error is null)
            preview.PreviewToken = await ComputeTokenAsync(db, plan, cancellationToken);
        return IvMasterOperationResult<MachineScheduleAdvancedPreview>.Ok(preview);
    }

    private async Task<IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitAsync(
        MachineScheduleAdvancedRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);
        var invalid = ValidateRequest(request, requireToken: true);
        if (invalid is not null)
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(IvMasterErrorCode.Validation, invalid);

        var writeCheck = await CheckWritesAsync(cancellationToken);
        if (writeCheck.Error is not null)
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                IvMasterErrorCode.Validation, writeCheck.Error);

        var scope = authorization.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var seed = await LoadOrderAsync(db, scope, request.WorkOrderId, tracking: false, cancellationToken);
            if (seed is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order was not found in the current company and branch.");
            }
            var seedVersionError = ValidateExpectedVersions(seed, request);
            if (seedVersionError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency, seedVersionError);
            }
            var seedTarget = ResolveTarget(seed, request);
            if (seedTarget.Machine is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.Validation, seedTarget.Error ?? "The selected machine operation is unavailable.");
            }

            var targetMachineCode = MachineScheduleAdvancedPlacementEngine.Normalize(seedTarget.Machine.MachineCode);
            var branchSegments = await db.ProductionMachineScheduleSegments.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
                .Select(x => new
                {
                    x.Uid,
                    x.WorkOrderId,
                    x.WorkOrderMachineId,
                    x.MachineCode
                })
                .ToListAsync(cancellationToken);
            var victimIds = branchSegments
                .Where(x => string.Equals(
                        MachineScheduleAdvancedPlacementEngine.Normalize(x.MachineCode),
                        targetMachineCode, StringComparison.Ordinal)
                    && x.WorkOrderMachineId != seedTarget.Machine.Uid)
                .Select(x => x.WorkOrderId)
                .Distinct()
                .ToList();
            var machineCodes = seed.RouteSteps.SelectMany(x => x.Operations)
                .SelectMany(x => x.Machines.Where(m => m.IsSelected).Select(m => m.MachineCode))
                .Append(seedTarget.Machine.MachineCode)
                .ToList();
            if (victimIds.Count > 0)
            {
                var victimMachineCodes = await db.ProductionWorkOrderMachines.AsNoTracking()
                    .Where(x => x.IsSelected
                        && x.Operation != null && x.Operation.WorkOrderId != null
                        && victimIds.Contains(x.Operation.WorkOrderId.Value))
                    .Select(x => x.MachineCode)
                    .ToListAsync(cancellationToken);
                machineCodes.AddRange(victimMachineCodes);
            }

            await ProductionMachineScheduleLock.AcquireManyAsync(
                db, scope.CompanyCode, machineCodes, cancellationToken);
            await WorkOrderSchedulingLock.AcquireAsync(
                db, scope.CompanyCode, exclusive: false, cancellationToken);
            await ProductionWorkOrderExecutionLock.AcquireManyAsync(
                db, scope.CompanyCode, scope.BranchCode!,
                victimIds.Append(request.WorkOrderId), cancellationToken);

            var tracked = await LoadOrderAsync(db, scope, request.WorkOrderId, tracking: true, cancellationToken);
            if (tracked is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order was not found in the current company and branch.");
            }
            var lockedVersionError = ValidateExpectedVersions(tracked, request);
            if (lockedVersionError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency, lockedVersionError);
            }

            var currentWriteCheck = await CheckWritesAsync(cancellationToken);
            if (currentWriteCheck.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.Validation, currentWriteCheck.Error);
            }

            var engine = new MachineScheduleAdvancedPlacementEngine(_machineCalendars, _calendarProvider);
            var builder = new MachineScheduleAdvancedPlanBuilder(
                db, scope, _capacity, engine, tracking: true);
            var plan = await builder.BuildAsync(request, cancellationToken);
            if (!plan.CanCommit)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.Validation,
                    string.Join(" ", plan.Preview.Blockers));
            }

            var token = await ComputeTokenAsync(db, plan, cancellationToken);
            if (!string.Equals(token, request.PreviewToken, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency,
                    "Schedule inputs changed after preview. Refresh the preview before committing.");
            }

            var audit = await PersistAsync(db, plan, scope, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var targetOrderPlan = plan.Orders.FirstOrDefault(x => x.Order.Uid == request.WorkOrderId);
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Ok(
                new MachineScheduleAdvancedCommitResult
                {
                    Action = request.Action,
                    WorkOrderId = request.WorkOrderId,
                    AuditEventId = audit.Uid,
                    HoldId = plan.HoldsToAdd.FirstOrDefault()?.Uid ?? plan.HoldToResolve?.Uid,
                    NewWorkOrderScheduleRevision = plan.TargetOrder.ScheduleRevision,
                    AffectedWorkOrderIds = plan.AffectedWorkOrderIds,
                    CommittedStart = targetOrderPlan?.AfterStart,
                    CommittedCompletion = targetOrderPlan?.AfterCompletion,
                    HasUnscheduledRemaining = plan.HasUnscheduledRemaining,
                    Message = plan.HasUnscheduledRemaining
                        ? "Schedule updated. Remaining quantity is held and completion is unknown."
                        : "Advanced machine schedule updated."
                });
        }
        catch (ProductionMachineScheduleLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (ProductionWorkOrderExecutionLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAdvancedCommitResult>.Fail(
                IvMasterErrorCode.Concurrency,
                "The Work Order or machine schedule changed while committing. Refresh and retry.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<(InventoryTenantScope? Scope, (IvMasterErrorCode Code, string Message)? Error)> AuthorizeAsync(
        CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return (null, (IvMasterErrorCode.InvalidScope, "A company and branch context is required."));
        if (!await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, cancellationToken)
            || !await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Edit, cancellationToken))
            return (null, (IvMasterErrorCode.AccessDenied, "Edit access to Machine Schedule Planner is required."));
        return (scope, null);
    }

    private async Task<(bool SegmentWriteEnabled, string? Error)> CheckWritesAsync(
        CancellationToken cancellationToken)
    {
        var segmentWriteEnabled = await _settings.GetFlagAsync(
            AppSettingModules.Planning,
            AppSettingCatalogue.PlanningKeys.MachineScheduleSegmentWriteEnabled,
            cancellationToken);
        var readiness = await _readiness.GetReadinessAsync(cancellationToken);
        if (!segmentWriteEnabled)
            return (false,
                "Advanced machine scheduling is disabled by MACHINE_SCHEDULE_SEGMENT_WRITE_ENABLED. "
                + "Existing segmented data remains readable.");
        if (!readiness.IsWriteReady)
            return (false,
                "Advanced machine scheduling is disabled until planner readiness passes. "
                + string.Join(" ", readiness.BlockingReasons));
        return (true, null);
    }

    private static string? ValidateRequest(MachineScheduleAdvancedRequest request, bool requireToken)
    {
        if (request is null || request.WorkOrderId <= 0 || request.OperationId <= 0 || request.WorkOrderMachineId <= 0)
            return "A valid Work Order, operation, and selected machine are required.";
        if (string.IsNullOrWhiteSpace(request.MachineCode))
            return "A selected machine code is required.";
        if (request.ExpectedWorkOrderScheduleRevision < 0 || request.ExpectedMachineScheduleRevision < 0)
            return "Expected schedule revisions cannot be negative.";
        if (request.ExpectedWorkOrderRowVersion.Length == 0 || request.ExpectedMachineRowVersion.Length == 0)
            return "Refresh the board before previewing this schedule change.";
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            return "Enter a reason of no more than 500 characters.";
        if (requireToken && string.IsNullOrWhiteSpace(request.PreviewToken))
            return "Create a fresh preview before committing the schedule change.";

        switch (request.Action)
        {
            case MachineScheduleAdvancedAction.InsertSplit:
            case MachineScheduleAdvancedAction.Hold:
            case MachineScheduleAdvancedAction.ReleaseHold:
            case MachineScheduleAdvancedAction.ReplanRemaining:
                if (request.RequestedStart is null)
                    return "A requested start date and time is required.";
                break;
            case MachineScheduleAdvancedAction.MaintenanceReplan:
                if (request.MaintenanceId is null && request.DowntimeStart is null)
                    return "A maintenance record or a downtime window is required.";
                break;
        }

        if (request.Action == MachineScheduleAdvancedAction.Hold
            && !ProductionScheduleHoldTypes.IsKnown(request.HoldType))
            return "Choose a valid hold type.";
        if (request.PartialQuantity is decimal partial && partial < 0m)
            return "Partial quantity cannot be negative.";
        return null;
    }

    private static string? ValidateExpectedVersions(
        ProductionWorkOrder order, MachineScheduleAdvancedRequest request)
    {
        if (order.ScheduleRevision != request.ExpectedWorkOrderScheduleRevision
            || !order.RowVersion.AsSpan().SequenceEqual(request.ExpectedWorkOrderRowVersion))
            return "Work Order schedule changed since the board was loaded. Refresh and preview again.";
        var target = ResolveTarget(order, request);
        if (target.Machine is null)
            return target.Error ?? "The selected machine operation is no longer available.";
        if (target.Machine.ScheduleRevision != request.ExpectedMachineScheduleRevision
            || !target.Machine.RowVersion.AsSpan().SequenceEqual(request.ExpectedMachineRowVersion))
            return "Machine schedule changed since the board was loaded. Refresh and preview again.";
        return null;
    }

    private static (ProductionWorkOrderOperation? Operation, ProductionWorkOrderMachine? Machine,
        ProductionWorkOrderRouteStep? Step, string? Error) ResolveTarget(
        ProductionWorkOrder order, MachineScheduleAdvancedRequest request)
    {
        var operation = order.RouteSteps.SelectMany(x => x.Operations)
            .SingleOrDefault(x => x.Uid == request.OperationId);
        var machine = operation?.Machines.SingleOrDefault(x => x.Uid == request.WorkOrderMachineId && x.IsSelected);
        var step = order.RouteSteps.SingleOrDefault(x => x.Uid == operation?.RouteStepId);
        if (operation is null || machine is null || step is null)
            return (null, null, null, "The selected operation or machine no longer belongs to this Work Order.");
        if (!PrProcessTypes.SupportsMachine(operation.ProcessType))
            return (null, null, null, "Only a machine-based operation can be changed on this board.");
        return (operation, machine, step, null);
    }

    private static IQueryable<ProductionWorkOrder> OrderQuery(
        AppDbContext db, InventoryTenantScope scope, long workOrderId, bool tracking)
    {
        IQueryable<ProductionWorkOrder> query = db.ProductionWorkOrders
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Machines)
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Materials).ThenInclude(x => x.ProducingRouteStep)
            .AsSplitQuery()
            .Where(x => x.Uid == workOrderId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode);
        return tracking ? query : query.AsNoTracking();
    }

    private static Task<ProductionWorkOrder?> LoadOrderAsync(
        AppDbContext db, InventoryTenantScope scope, long workOrderId, bool tracking,
        CancellationToken cancellationToken) =>
        OrderQuery(db, scope, workOrderId, tracking).SingleOrDefaultAsync(cancellationToken);

    private static async Task<string> ComputeTokenAsync(
        AppDbContext db,
        MachineScheduleAdvancedPlan plan,
        CancellationToken cancellationToken)
    {
        var machineIds = plan.AffectedMachineIds
            .Concat(plan.Orders.SelectMany(order => order.Order.RouteSteps)
                .SelectMany(step => step.Operations)
                .SelectMany(operation => operation.Machines.Where(m => m.IsSelected).Select(m => m.Uid)))
            .Distinct()
            .ToList();
        var segments = machineIds.Count == 0
            ? []
            : await db.ProductionMachineScheduleSegments.AsNoTracking()
                .Where(x => machineIds.Contains(x.WorkOrderMachineId))
                .OrderBy(x => x.Uid)
                .Select(x => new
                {
                    x.Uid,
                    x.SegmentStatus,
                    x.MachineScheduleRevision,
                    x.PlannedStartDateTime,
                    x.PlannedCompletionDateTime,
                    x.PlannedOutputQty,
                    RowVersion = Convert.ToBase64String(x.RowVersion)
                })
                .ToListAsync(cancellationToken);
        var allocations = machineIds.Count == 0
            ? []
            : await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => machineIds.Contains(x.WorkOrderMachineId))
                .OrderBy(x => x.Uid)
                .Select(x => new
                {
                    x.Uid,
                    x.FragmentNo,
                    x.ScheduleRevision,
                    x.StartDateTime,
                    x.EndDateTime,
                    x.MachineScheduleSegmentId,
                    x.ScheduleSourceHash
                })
                .ToListAsync(cancellationToken);
        var holds = machineIds.Count == 0
            ? []
            : await db.ProductionWorkOrderScheduleHolds.AsNoTracking()
                .Where(x => machineIds.Contains(x.WorkOrderMachineId)
                    && x.Status == ProductionScheduleHoldStatuses.Active)
                .OrderBy(x => x.Uid)
                .Select(x => new
                {
                    x.Uid,
                    x.Status,
                    x.HoldType,
                    x.HoldFrom,
                    x.ResumeNotBefore,
                    RowVersion = Convert.ToBase64String(x.RowVersion)
                })
                .ToListAsync(cancellationToken);

        var payload = new
        {
            Request = new
            {
                plan.Request.Action,
                plan.Request.WorkOrderId,
                plan.Request.OperationId,
                plan.Request.WorkOrderMachineId,
                MachineCode = MachineScheduleAdvancedPlacementEngine.Normalize(plan.Request.MachineCode),
                plan.Request.RequestedStart,
                plan.Request.PartialQuantity,
                plan.Request.IncludeRestartSetup,
                plan.Request.HoldType,
                plan.Request.ResumeNotBefore,
                plan.Request.HoldId,
                plan.Request.MaintenanceId,
                plan.Request.DowntimeStart,
                plan.Request.DowntimeEnd,
                Reason = plan.Request.Reason.Trim()
            },
            Orders = plan.Orders.OrderBy(x => x.Order.Uid).Select(x => new
            {
                x.Order.Uid,
                x.Order.ScheduleRevision,
                RowVersion = Convert.ToBase64String(x.Order.RowVersion),
                x.Order.PlannedStartDateTime,
                x.Order.PlannedCompletionDateTime
            }).ToList(),
            Proposal = plan.AllSegments.Select(x => new
            {
                x.WorkOrderId,
                x.WorkOrderMachineId,
                x.OperationId,
                x.Kind,
                x.Qty,
                x.Start,
                x.Completion,
                x.SourceHash,
                Fragments = x.Fragments.Select(f => new { f.Start, f.End }).ToList()
            }).ToList(),
            Segments = segments,
            Allocations = allocations,
            Holds = holds,
            Unscheduled = plan.HasUnscheduledRemaining
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }
}
