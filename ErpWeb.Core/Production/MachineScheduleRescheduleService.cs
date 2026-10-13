using System.Security.Cryptography;
using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class MachineScheduleRescheduleRequest
{
    public long WorkOrderId { get; init; }
    public long OperationId { get; init; }
    public long WorkOrderMachineId { get; init; }
    public string MachineCode { get; init; } = string.Empty;
    public DateTime RequestedStart { get; init; }
    public int ExpectedWorkOrderScheduleRevision { get; init; }
    public byte[] ExpectedWorkOrderRowVersion { get; init; } = [];
    public int ExpectedMachineScheduleRevision { get; init; }
    public byte[] ExpectedMachineRowVersion { get; init; } = [];
    public string Reason { get; init; } = string.Empty;
    public string? PreviewToken { get; init; }
}

public sealed class MachineScheduleRescheduleChange
{
    public long OperationId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationDescription { get; init; }
    public string? WorkCentreCode { get; init; }
    public string? MachineCode { get; init; }
    public DateTime BeforeStart { get; init; }
    public DateTime BeforeCompletion { get; init; }
    public DateTime AfterStart { get; init; }
    public DateTime AfterCompletion { get; init; }
}

public sealed class MachineScheduleReschedulePreview
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public long TargetOperationId { get; init; }
    public string TargetMachineCode { get; init; } = string.Empty;
    public DateTime CurrentStart { get; init; }
    public DateTime CurrentCompletion { get; init; }
    public DateTime? ProposedStart { get; init; }
    public DateTime? ProposedCompletion { get; init; }
    public DateTime WorkOrderStartBefore { get; init; }
    public DateTime WorkOrderCompletionBefore { get; init; }
    public DateTime? WorkOrderStartAfter { get; init; }
    public DateTime? WorkOrderCompletionAfter { get; init; }
    public IReadOnlyList<MachineScheduleRescheduleChange> Changes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public string? PreviewToken { get; init; }
    public bool CanCommit => Blockers.Count == 0 && !string.IsNullOrWhiteSpace(PreviewToken);
}

public sealed class MachineScheduleRescheduleCommitResult
{
    public long WorkOrderId { get; init; }
    public long AuditEventId { get; init; }
    public int NewWorkOrderScheduleRevision { get; init; }
    public IReadOnlyList<long> AffectedOperationIds { get; init; } = [];
    public DateTime? CommittedStart { get; init; }
    public DateTime? CommittedCompletion { get; init; }
    public string Message { get; init; } = string.Empty;
}

public interface IMachineScheduleRescheduleService
{
    Task<IvMasterOperationResult<MachineScheduleReschedulePreview>> PreviewRescheduleAsync(
        MachineScheduleRescheduleRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MachineScheduleRescheduleCommitResult>> CommitRescheduleAsync(
        MachineScheduleRescheduleRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-authoritative preview and commit for same-machine forward moves. Preview is read-only;
/// Commit repeats the calculation under machine and execution locks and persists only schedule
/// fields, revisions, allocation fragments, and audit evidence.
/// </summary>
public sealed class MachineScheduleRescheduleService : IMachineScheduleRescheduleService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly IMachineSchedulePlannerReadinessService _readiness;
    private readonly IProductionCalendarScheduleDataLoader _machineCalendars;
    private readonly IWorkOrderCalendarProvider _calendarProvider;

    public MachineScheduleRescheduleService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        IMachineSchedulePlannerReadinessService readiness,
        IProductionCalendarScheduleDataLoader machineCalendars,
        IWorkOrderCalendarProvider calendarProvider)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _readiness = readiness;
        _machineCalendars = machineCalendars;
        _calendarProvider = calendarProvider;
    }

    public async Task<IvMasterOperationResult<MachineScheduleReschedulePreview>> PreviewRescheduleAsync(
        MachineScheduleRescheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleReschedulePreview>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);

        var invalid = ValidateRequest(request, requireToken: false);
        if (invalid is not null)
            return IvMasterOperationResult<MachineScheduleReschedulePreview>.Fail(IvMasterErrorCode.Validation, invalid);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var order = await LoadOrderAsync(db, authorization.Scope!, request.WorkOrderId, tracking: false, cancellationToken);
        if (order is null)
            return IvMasterOperationResult<MachineScheduleReschedulePreview>.Fail(
                IvMasterErrorCode.NotFound, "Work Order was not found in the current company and branch.");

        var versionError = ValidateExpectedVersions(order, request);
        if (versionError is not null)
            return IvMasterOperationResult<MachineScheduleReschedulePreview>.Fail(IvMasterErrorCode.Concurrency, versionError);

        var proposal = await BuildProposalAsync(db, order, request, cancellationToken);
        return IvMasterOperationResult<MachineScheduleReschedulePreview>.Ok(proposal.Preview);
    }

    public async Task<IvMasterOperationResult<MachineScheduleRescheduleCommitResult>> CommitRescheduleAsync(
        MachineScheduleRescheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);

        var invalid = ValidateRequest(request, requireToken: true);
        if (invalid is not null)
            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(IvMasterErrorCode.Validation, invalid);

        var readiness = await _readiness.GetReadinessAsync(cancellationToken);
        if (!readiness.IsWriteReady)
            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                IvMasterErrorCode.Validation,
                "Machine schedule changes are disabled until allocation backfill, calendar evidence, and release readiness pass. "
                + string.Join(" ", readiness.BlockingReasons));

        var scope = authorization.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var seed = await LoadOrderAsync(db, scope, request.WorkOrderId, tracking: false, cancellationToken);
            if (seed is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order was not found in the current company and branch.");
            }

            var seedVersionError = ValidateExpectedVersions(seed, request);
            if (seedVersionError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency, seedVersionError);
            }

            var seedTarget = ResolveTarget(seed, request);
            if (seedTarget.Operation is null || seedTarget.Machine is null || seedTarget.Step is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                    IvMasterErrorCode.Validation, seedTarget.Error ?? "The selected machine operation is unavailable.");
            }

            var seedAffected = GetAffectedOperations(seed, seedTarget.Step, seedTarget.Operation);
            var machineCodes = seedAffected
                .SelectMany(x => x.Machines.Where(m => m.IsSelected).Select(m => m.MachineCode))
                .Append(seedTarget.Machine.MachineCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await ProductionMachineScheduleLock.AcquireManyAsync(
                db, scope.CompanyCode, machineCodes, cancellationToken);
            await WorkOrderSchedulingLock.AcquireAsync(
                db, scope.CompanyCode, exclusive: false, cancellationToken);
            await ProductionWorkOrderExecutionLock.AcquireAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.WorkOrderId, cancellationToken);

            var order = await LoadOrderAsync(db, scope, request.WorkOrderId, tracking: true, cancellationToken);
            if (order is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order was not found in the current company and branch.");
            }

            var versionError = ValidateExpectedVersions(order, request);
            if (versionError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(IvMasterErrorCode.Concurrency, versionError);
            }

            var currentReadiness = await _readiness.GetReadinessAsync(cancellationToken);
            if (!currentReadiness.IsWriteReady)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                    IvMasterErrorCode.Validation,
                    "Machine schedule changes are disabled until runtime readiness passes. "
                    + string.Join(" ", currentReadiness.BlockingReasons));
            }

            var proposal = await BuildProposalAsync(db, order, request, cancellationToken);
            if (!proposal.Preview.CanCommit)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                    IvMasterErrorCode.Validation,
                    string.Join(" ", proposal.Preview.Blockers));
            }

            if (!string.Equals(proposal.Preview.PreviewToken, request.PreviewToken, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency, "Schedule inputs changed after preview. Refresh the preview before committing.");
            }

            var audit = ApplyProposal(db, order, scope, request, proposal);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Ok(new MachineScheduleRescheduleCommitResult
            {
                WorkOrderId = order.Uid,
                AuditEventId = audit.Uid,
                NewWorkOrderScheduleRevision = order.ScheduleRevision,
                AffectedOperationIds = proposal.Changes.Select(x => x.Operation.Uid).Distinct().Order().ToList(),
                CommittedStart = proposal.TargetOperation.PlannedStartDateTime,
                CommittedCompletion = proposal.TargetOperation.PlannedCompletionDateTime,
                Message = "Machine schedule updated."
            });
        }
        catch (ProductionMachineScheduleLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (ProductionWorkOrderExecutionLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleRescheduleCommitResult>.Fail(
                IvMasterErrorCode.Concurrency, "The Work Order changed while the schedule was being committed. Refresh and retry.");
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

    private static string? ValidateRequest(MachineScheduleRescheduleRequest request, bool requireToken)
    {
        if (request is null || request.WorkOrderId <= 0 || request.OperationId <= 0 || request.WorkOrderMachineId <= 0)
            return "A valid Work Order, operation, and selected machine are required.";
        if (string.IsNullOrWhiteSpace(request.MachineCode))
            return "A selected machine code is required.";
        if (request.RequestedStart == default)
            return "A requested start date and time is required.";
        if (request.ExpectedWorkOrderScheduleRevision < 0 || request.ExpectedMachineScheduleRevision < 0)
            return "Expected schedule revisions cannot be negative.";
        if (request.ExpectedWorkOrderRowVersion.Length == 0 || request.ExpectedMachineRowVersion.Length == 0)
            return "Refresh the board before previewing this schedule change.";
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            return "Enter a reason of no more than 500 characters.";
        if (requireToken && string.IsNullOrWhiteSpace(request.PreviewToken))
            return "Create a fresh preview before committing the schedule change.";
        return null;
    }

    private static string? ValidateExpectedVersions(ProductionWorkOrder order, MachineScheduleRescheduleRequest request)
    {
        if (order.ScheduleRevision != request.ExpectedWorkOrderScheduleRevision
            || !order.RowVersion.AsSpan().SequenceEqual(request.ExpectedWorkOrderRowVersion))
            return "Work Order schedule changed since the board was loaded. Refresh and preview again.";

        var target = ResolveTarget(order, request);
        if (target.Operation is null || target.Machine is null)
            return target.Error ?? "The selected machine operation is no longer available.";
        if (target.Machine.ScheduleRevision != request.ExpectedMachineScheduleRevision
            || !target.Machine.RowVersion.AsSpan().SequenceEqual(request.ExpectedMachineRowVersion))
            return "Machine schedule changed since the board was loaded. Refresh and preview again.";
        return null;
    }

    private static (ProductionWorkOrderOperation? Operation, ProductionWorkOrderMachine? Machine,
        ProductionWorkOrderRouteStep? Step, string? Error) ResolveTarget(
        ProductionWorkOrder order,
        MachineScheduleRescheduleRequest request)
    {
        var operation = order.RouteSteps.SelectMany(x => x.Operations)
            .SingleOrDefault(x => x.Uid == request.OperationId);
        var machine = operation?.Machines.SingleOrDefault(x => x.Uid == request.WorkOrderMachineId && x.IsSelected);
        var step = order.RouteSteps.SingleOrDefault(x => x.Uid == operation?.RouteStepId);
        if (operation is null || machine is null || step is null)
            return (null, null, null, "The selected operation or machine no longer belongs to this Work Order.");
        if (!PrProcessTypes.SupportsMachine(operation.ProcessType))
            return (null, null, null, "Only a machine-based operation can be moved on this board.");
        if (!string.Equals(Normalize(machine.MachineCode), Normalize(request.MachineCode), StringComparison.Ordinal))
            return (null, null, null, "Cross-machine schedule changes are not supported.");
        return (operation, machine, step, null);
    }

    private async Task<ScheduleProposal> BuildProposalAsync(
        AppDbContext db,
        ProductionWorkOrder order,
        MachineScheduleRescheduleRequest request,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(order, request);
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (target.Operation is null || target.Machine is null || target.Step is null)
        {
            blockers.Add(target.Error ?? "The selected machine operation is unavailable.");
            return ScheduleProposal.Blocked(EmptyPreview(order, request, blockers));
        }

        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            blockers.Add("Only Released or In Progress Work Orders can be rescheduled.");
        if (string.Equals(order.SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.OrdinalIgnoreCase))
            blockers.Add("Backward-scheduled Work Orders remain locked until backward placement is proven.");
        if (order.SnapshotFormatVersion != ProductionSnapshotFormatVersions.Current)
            blockers.Add("Legacy Work Order snapshots are not eligible for schedule-only changes.");
        if (order.GoodQty > 0m || order.ScrapQty > 0m || order.RejectQty > 0m || order.HoldQty > 0m
            || order.ApprovedVarianceQty > 0m)
            blockers.Add("Work Order execution quantities show that production has started.");

        var affected = GetAffectedOperations(order, target.Step, target.Operation);
        var workOrderStartBefore = order.PlannedStartDateTime;
        var workOrderCompletionBefore = order.PlannedCompletionDateTime;
        if (affected.Count == 0)
            blockers.Add("No operation schedule would change.");
        if (affected.Any(HasExecutionQuantity))
            blockers.Add("One or more affected operations already contain execution quantities.");

        var affectedIds = affected.Select(x => x.Uid).ToList();
        var outputRows = await db.ProductionOutputs.AsNoTracking()
            .Where(x => affectedIds.Contains(x.WorkOrderOperationId)
                && ((x.Status == ProductionOutputStatuses.New && x.DeletedAtUtc == null)
                    || x.Status == ProductionOutputStatuses.Posted
                    || x.Status == ProductionOutputStatuses.Reversed))
            .Select(x => new { x.WorkOrderOperationId, x.Status })
            .ToListAsync(cancellationToken);
        foreach (var group in outputRows.GroupBy(x => x.Status, StringComparer.OrdinalIgnoreCase))
        {
            var status = group.Key;
            var label = status switch
            {
                ProductionOutputStatuses.New => "NEW Daily Production drafts",
                ProductionOutputStatuses.Posted => "POSTED Daily Production history",
                ProductionOutputStatuses.Reversed => "REVERSED Daily Production history",
                _ => status
            };
            blockers.Add($"{group.Select(x => x.WorkOrderOperationId).Distinct().Count()} affected operation(s) have {label}; resolve them before rescheduling.");
        }

        var affectedMachinePairs = affected
            .SelectMany(operation => operation.Machines.Where(machine => machine.IsSelected)
                .Select(machine => (Operation: operation, Machine: machine)))
            .ToList();
        var excludedMachineIds = affectedMachinePairs.Select(x => x.Machine.Uid).ToHashSet();
        var machineCodes = affectedMachinePairs.Select(x => x.Machine.MachineCode)
            .Append(target.Machine.MachineCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var capacity = await LoadExistingCapacityAsync(
            db, order.CompanyCode, order.BranchCode, machineCodes, excludedMachineIds, cancellationToken);
        blockers.AddRange(capacity.Blockers);
        warnings.AddRange(capacity.Warnings);
        if (blockers.Count > 0)
            return ScheduleProposal.Blocked(EmptyPreview(order, request, blockers, warnings));

        var before = affected.ToDictionary(
            x => x.Uid,
            x => (Start: x.PlannedStartDateTime, Completion: x.PlannedCompletionDateTime));
        var placements = new List<OperationPlacement>();
        var completionByStep = new Dictionary<long, DateTime>();
        var requestedStart = DateTime.SpecifyKind(request.RequestedStart, DateTimeKind.Unspecified);
        var groupAnchor = order.RouteSteps
            .Where(x => x.StageSequence < target.Step.StageSequence)
            .Select(StepCompletion)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty(requestedStart)
            .Max();

        var stages = order.RouteSteps.GroupBy(x => x.StageSequence).OrderBy(x => x.Key).ToList();
        foreach (var stage in stages)
        {
            var stageCompletions = new List<DateTime>();
            foreach (var step in stage.OrderBy(x => x.WorkCentreCode, StringComparer.Ordinal).ThenBy(x => x.Uid))
            {
                var isTargetStep = step.Uid == target.Step.Uid;
                var isLaterStage = step.StageSequence > target.Step.StageSequence;
                if (step.StageSequence < target.Step.StageSequence || (!isTargetStep && !isLaterStage))
                {
                    var unchangedCompletion = StepCompletion(step);
                    if (unchangedCompletion is null)
                    {
                        blockers.Add($"Route step {step.StageSequence} has no saved completion time.");
                        continue;
                    }
                    completionByStep[step.Uid] = unchangedCompletion.Value;
                    stageCompletions.Add(unchangedCompletion.Value);
                    continue;
                }

                var cursor = groupAnchor;
                var operations = step.Operations.OrderBy(x => x.ProcessSequence)
                    .ThenBy(x => x.OperationCode, StringComparer.Ordinal).ThenBy(x => x.Uid).ToList();
                foreach (var processGroup in operations.GroupBy(x => x.ProcessSequence).OrderBy(x => x.Key))
                {
                    var parallelCompletions = new List<DateTime>();
                    foreach (var operation in processGroup)
                    {
                        var reschedule = isLaterStage
                            || (isTargetStep && operation.ProcessSequence > target.Operation.ProcessSequence)
                            || operation.Uid == target.Operation.Uid;
                        if (!reschedule)
                        {
                            if (operation.PlannedCompletionDateTime is null)
                            {
                                blockers.Add($"Operation {operation.OperationCode} has no saved completion time.");
                                continue;
                            }
                            parallelCompletions.Add(operation.PlannedCompletionDateTime.Value);
                            continue;
                        }

                        var earliest = ResolveEarliest(operation, cursor, completionByStep);
                        if (operation.Uid == target.Operation.Uid && requestedStart > earliest)
                            earliest = requestedStart;

                        var placement = await PlaceOperationAsync(
                            order, operation, earliest, capacity.OccupiedByMachine, cancellationToken);
                        if (placement.Error is not null)
                        {
                            blockers.Add(placement.Error);
                            continue;
                        }

                        operation.PlannedStartDateTime = placement.Start;
                        operation.PlannedCompletionDateTime = placement.Completion;
                        if (placement.Machine is not null)
                        {
                            ApplyMachineProvenance(operation, placement.Machine, placement);
                            capacity.OccupiedByMachine[Normalize(placement.Machine.MachineCode)]
                                .AddRange(placement.ConsumedIntervals);
                        }
                        else
                        {
                            operation.CalendarSourceType = ProductionCalendarSourceTypes.PlantDefault;
                            operation.CalendarSourceId = placement.CalendarSourceId;
                            operation.CalendarSourceLastModified = placement.CalendarSourceLastModified;
                            operation.CalendarHorizonStart = placement.HorizonStart.ToDateTime(TimeOnly.MinValue);
                            operation.CalendarHorizonEnd = placement.HorizonEnd.ToDateTime(TimeOnly.MinValue);
                            operation.ScheduleSourceHash = placement.SourceHash;
                        }
                        placements.Add(new OperationPlacement(operation, placement));
                        parallelCompletions.Add(placement.Completion);
                    }

                    if (parallelCompletions.Count > 0)
                        cursor = parallelCompletions.Max();
                }

                var starts = step.Operations.Where(x => x.PlannedStartDateTime is not null)
                    .Select(x => x.PlannedStartDateTime!.Value).ToList();
                var ends = step.Operations.Where(x => x.PlannedCompletionDateTime is not null)
                    .Select(x => x.PlannedCompletionDateTime!.Value).ToList();
                if (starts.Count == 0 || ends.Count == 0)
                {
                    blockers.Add($"Route step {step.StageSequence} has incomplete operation dates.");
                    continue;
                }
                step.PlannedStartDateTime = starts.Min();
                step.PlannedCompletionDateTime = ends.Max();
                completionByStep[step.Uid] = step.PlannedCompletionDateTime.Value;
                stageCompletions.Add(step.PlannedCompletionDateTime.Value);
            }

            if (stageCompletions.Count > 0)
                groupAnchor = stageCompletions.Max();
        }

        var hasScheduleChanges = placements.Any(x =>
        {
            var previous = before[x.Operation.Uid];
            return previous.Start != x.Operation.PlannedStartDateTime
                || previous.Completion != x.Operation.PlannedCompletionDateTime;
        });
        if (blockers.Count > 0 || !hasScheduleChanges)
        {
            if (blockers.Count == 0)
                blockers.Add("The requested start produces no schedule change.");
            return ScheduleProposal.Blocked(EmptyPreview(order, request, blockers, warnings));
        }

        order.PlannedStartDateTime = order.RouteSteps
            .Where(x => x.PlannedStartDateTime is not null)
            .Select(x => x.PlannedStartDateTime!.Value)
            .DefaultIfEmpty(order.PlannedStartDateTime).Min();
        order.PlannedCompletionDateTime = order.RouteSteps
            .Where(x => x.PlannedCompletionDateTime is not null)
            .Select(x => x.PlannedCompletionDateTime!.Value)
            .DefaultIfEmpty(order.PlannedCompletionDateTime).Max();

        var changes = placements.Select(x => new MachineScheduleRescheduleChange
        {
            OperationId = x.Operation.Uid,
            OperationCode = x.Operation.OperationCode,
            OperationDescription = x.Operation.OperationDescription,
            WorkCentreCode = order.RouteSteps.FirstOrDefault(s => s.Uid == x.Operation.RouteStepId)?.WorkCentreCode,
            MachineCode = x.Placement.Machine?.MachineCode,
            BeforeStart = before[x.Operation.Uid].Start ?? default,
            BeforeCompletion = before[x.Operation.Uid].Completion ?? default,
            AfterStart = x.Operation.PlannedStartDateTime ?? default,
            AfterCompletion = x.Operation.PlannedCompletionDateTime ?? default
        }).OrderBy(x => x.BeforeStart).ThenBy(x => x.OperationId).ToList();

        var targetPlacement = placements.SingleOrDefault(x => x.Operation.Uid == target.Operation.Uid);
        if (targetPlacement is null)
        {
            blockers.Add("The target operation could not be placed.");
            return ScheduleProposal.Blocked(EmptyPreview(order, request, blockers, warnings));
        }

        var token = ComputePreviewToken(order, target.Operation, target.Machine, request, changes,
            capacity.SourceStamps, placements);
        var preview = new MachineScheduleReschedulePreview
        {
            WorkOrderId = order.Uid,
            WorkOrderNo = order.WorkOrderNo,
            TargetOperationId = target.Operation.Uid,
            TargetMachineCode = target.Machine.MachineCode,
            CurrentStart = before[target.Operation.Uid].Start ?? default,
            CurrentCompletion = before[target.Operation.Uid].Completion ?? default,
            ProposedStart = targetPlacement.Placement.Start,
            ProposedCompletion = targetPlacement.Placement.Completion,
            WorkOrderStartBefore = workOrderStartBefore,
            WorkOrderCompletionBefore = workOrderCompletionBefore,
            WorkOrderStartAfter = order.PlannedStartDateTime,
            WorkOrderCompletionAfter = order.PlannedCompletionDateTime,
            Changes = changes,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            Blockers = [],
            PreviewToken = token
        };
        return new ScheduleProposal(preview, placements, target.Operation, target.Machine);
    }

    private async Task<CapacityLoad> LoadExistingCapacityAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<string> machineCodes,
        IReadOnlySet<long> excludedMachineIds,
        CancellationToken cancellationToken)
    {
        var normalizedCodes = machineCodes.Select(Normalize).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var result = new CapacityLoad();
        foreach (var machineCode in normalizedCodes)
            result.OccupiedByMachine[machineCode] = [];
        if (normalizedCodes.Count == 0)
            return result;

        var activeRows = await db.ProductionWorkOrderMachines.AsNoTracking()
            .Where(machine => machine.IsSelected
                && normalizedCodes.Contains(machine.MachineCode)
                && machine.Operation != null
                && machine.Operation.WorkOrder != null
                && machine.Operation.WorkOrder.CompanyCode == companyCode
                && (machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && (machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds
                    + machine.PlannedRunMinutes * 60m) > 0m)
            .Select(machine => new ActiveMachineSchedule
            {
                CompanyCode = machine.Operation!.WorkOrder!.CompanyCode,
                WorkOrderId = machine.Operation!.WorkOrder!.Uid,
                WorkOrderNo = machine.Operation.WorkOrder.WorkOrderNo,
                BranchCode = machine.Operation.WorkOrder.BranchCode,
                SchedulingDirection = machine.Operation.WorkOrder.SchedulingDirection,
                MachineId = machine.Uid,
                MachineCode = machine.MachineCode,
                OperationId = machine.OperationId,
                OperationCode = machine.Operation.OperationCode,
                OperationStart = machine.Operation.PlannedStartDateTime,
                OperationCompletion = machine.Operation.PlannedCompletionDateTime,
                ScheduleRevision = machine.ScheduleRevision,
                ScheduleSourceHash = machine.ScheduleSourceHash,
                PlannedStart = machine.PlannedStartDateTime,
                PlannedCompletion = machine.PlannedCompletionDateTime,
                CalendarHorizonStart = machine.CalendarHorizonStart,
                CalendarHorizonEnd = machine.CalendarHorizonEnd,
                SetupSeconds = machine.SetupSeconds,
                ConversionSeconds = machine.ConversionSeconds,
                QueueSeconds = machine.QueueSeconds,
                PlannedRunMinutes = machine.PlannedRunMinutes
            })
            .ToListAsync(cancellationToken);

        var activeMachineIds = activeRows.Select(x => x.MachineId).ToList();
        var allocationRows = new List<ProductionMachineScheduleAllocation>();
        foreach (var idBatch in activeMachineIds.Chunk(800))
        {
            allocationRows.AddRange(await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode && idBatch.Contains(x.WorkOrderMachineId))
                .ToListAsync(cancellationToken));
        }
        var allocationsByMachineRevision = allocationRows
            .GroupBy(x => (x.WorkOrderMachineId, x.ScheduleRevision))
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.FragmentNo).ToList());

        var preservedIntervals = new List<CapacityInterval>();
        foreach (var row in activeRows.OrderBy(x => x.MachineCode, StringComparer.Ordinal)
                     .ThenBy(x => x.PlannedStart).ThenBy(x => x.MachineId))
        {
            allocationsByMachineRevision.TryGetValue((row.MachineId, row.ScheduleRevision), out var savedAllocations);
            var savedStamp = savedAllocations is null
                ? "missing"
                : string.Join(",", savedAllocations.Select(x =>
                    $"{x.Uid}:{x.FragmentNo}:{x.StartDateTime:O}:{x.EndDateTime:O}:{x.BranchCode}:{x.WorkOrderOperationId}:{x.ScheduleSourceHash}"));
            result.SourceStamps.Add(
                $"M:{row.MachineId}:{row.ScheduleRevision}:{row.MachineCode}:{row.PlannedStart:O}:{row.PlannedCompletion:O}:{row.ScheduleSourceHash}:{savedStamp}");

            // The current Work Order's affected operations are being replaced by this preview.
            // Their old fragments do not consume capacity in the proposed placement.
            if (excludedMachineIds.Contains(row.MachineId))
                continue;

            var rowLabel = string.Equals(row.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase)
                ? $"Work Order {row.WorkOrderNo}"
                : "An active Work Order in another branch";
            var reconstructed = await ReconstructActiveScheduleAsync(row, cancellationToken);
            if (reconstructed.Error is not null)
            {
                result.Blockers.Add(string.Equals(row.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase)
                    ? reconstructed.Error
                    : $"{rowLabel} cannot be verified on machine {row.MachineCode}; resolve its saved schedule or calendar provenance first.");
                continue;
            }

            var expected = reconstructed.Result!.ConsumedIntervals.OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
            if (savedAllocations is { Count: > 0 })
            {
                var stored = savedAllocations.Select(x => new TimeInterval(x.StartDateTime, x.EndDateTime))
                    .OrderBy(x => x.Start).ThenBy(x => x.End).ToList();
                if (!IntervalsEqual(stored, expected)
                    || savedAllocations.Any(x => x.ScheduleSourceHash != row.ScheduleSourceHash
                        || !string.Equals(x.CompanyCode, row.CompanyCode, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(x.BranchCode, row.BranchCode, StringComparison.OrdinalIgnoreCase)
                        || x.WorkOrderId != row.WorkOrderId
                        || x.WorkOrderOperationId != row.OperationId
                        || x.WorkOrderMachineId != row.MachineId
                        || x.ScheduleRevision != row.ScheduleRevision
                        || !string.Equals(Normalize(x.MachineCode), Normalize(row.MachineCode), StringComparison.Ordinal)))
                {
                    result.Blockers.Add($"{rowLabel} has allocation fragments that do not match its saved calendar schedule on {row.MachineCode}.");
                    continue;
                }
            }
            else
            {
                result.Warnings.Add($"{rowLabel} has no saved allocation fragments; preview reconstructed its current schedule. Backfill is required before commit.");
            }

            foreach (var interval in expected)
            {
                var key = Normalize(row.MachineCode);
                if (!result.OccupiedByMachine.ContainsKey(key))
                    result.OccupiedByMachine[key] = [];
                preservedIntervals.Add(new CapacityInterval(key, row.WorkOrderId, row.MachineId, interval));
                result.OccupiedByMachine[key].Add(interval);
            }
        }

        foreach (var machineGroup in preservedIntervals.GroupBy(x => x.MachineCode, StringComparer.Ordinal))
        {
            var ordered = machineGroup.OrderBy(x => x.Interval.Start).ThenBy(x => x.Interval.End).ToList();
            if (ordered.Count == 0)
                continue;
            var furthestEnding = ordered[0];
            for (var i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i];
                if (furthestEnding.Interval.End > current.Interval.Start)
                {
                    result.Blockers.Add($"Existing active Work Orders overlap on machine {machineGroup.Key}; resolve the overlap before previewing a move.");
                    break;
                }
                if (current.Interval.End > furthestEnding.Interval.End)
                    furthestEnding = current;
            }
        }

        result.SourceStamps.Sort(StringComparer.Ordinal);
        return result;
    }

    private async Task<(ScheduleMachineResult? Result, string? Error)> ReconstructActiveScheduleAsync(
        ActiveMachineSchedule row,
        CancellationToken cancellationToken)
    {
        if (row.PlannedStart is null || row.PlannedCompletion is null
            || row.OperationStart is null || row.OperationCompletion is null
            || row.CalendarHorizonStart is null || row.CalendarHorizonEnd is null
            || string.IsNullOrWhiteSpace(row.ScheduleSourceHash)
            || row.PlannedCompletion <= row.PlannedStart
            || row.OperationStart != row.PlannedStart
            || row.OperationCompletion != row.PlannedCompletion)
        {
            return (null, $"Work Order {row.WorkOrderNo} has incomplete or inconsistent saved schedule provenance on {row.MachineCode}.");
        }

        try
        {
            var start = DateOnly.FromDateTime(row.CalendarHorizonStart.Value);
            var end = DateOnly.FromDateTime(row.CalendarHorizonEnd.Value);
            if (end < start)
                return (null, $"Work Order {row.WorkOrderNo} has an invalid calendar horizon on {row.MachineCode}.");
            var data = await _machineCalendars.LoadAsync(row.CompanyCode, row.MachineCode, start, end, cancellationToken);
            var hash = WorkOrderScheduleSourceHasher.Compute(data, start, end);
            if (!string.Equals(hash, row.ScheduleSourceHash, StringComparison.Ordinal))
                return (null, $"Work Order {row.WorkOrderNo} uses changed calendar inputs on {row.MachineCode}; resolve the calendar impact first.");
            var direction = string.Equals(row.SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.OrdinalIgnoreCase)
                ? ScheduleDirection.Backward
                : ScheduleDirection.Forward;
            var duration = TimeSpan.FromSeconds((double)(row.SetupSeconds + row.ConversionSeconds + row.QueueSeconds))
                + TimeSpan.FromMinutes((double)row.PlannedRunMinutes);
            if (duration <= TimeSpan.Zero)
                return (null, $"Work Order {row.WorkOrderNo} has no positive productive duration on {row.MachineCode}.");
            var anchor = direction == ScheduleDirection.Backward ? row.PlannedCompletion.Value : row.PlannedStart.Value;
            var scheduled = ProductionCalendarScheduler.ScheduleMachine(data, anchor, duration, direction);
            if (scheduled.ActualStart != row.PlannedStart || scheduled.ActualCompletion != row.PlannedCompletion)
                return (null, $"Work Order {row.WorkOrderNo} cannot be reproduced from its saved calendar on {row.MachineCode}.");
            return (scheduled, null);
        }
        catch (ScheduleFailureException ex)
        {
            return (null, $"Work Order {row.WorkOrderNo} schedule cannot be reconstructed on {row.MachineCode}: {ex.Message}");
        }
    }

    private async Task<Placement> PlaceOperationAsync(
        ProductionWorkOrder order,
        ProductionWorkOrderOperation operation,
        DateTime anchor,
        Dictionary<string, List<TimeInterval>> occupiedByMachine,
        CancellationToken cancellationToken)
    {
        var machine = PrProcessTypes.SupportsMachine(operation.ProcessType)
            ? operation.Machines.SingleOrDefault(x => x.IsSelected)
            : null;
        if (PrProcessTypes.SupportsMachine(operation.ProcessType) && machine is null)
            return Placement.Failed($"Operation {operation.OperationCode} has no selected machine.");

        var duration = machine is null
            ? TimeSpan.FromMinutes((double)operation.StandardDurationMinutes)
            : TimeSpan.FromSeconds((double)(machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds))
                + TimeSpan.FromMinutes((double)machine.PlannedRunMinutes);
        if (duration <= TimeSpan.Zero)
            return Placement.Failed($"Operation {operation.OperationCode} has no positive schedule duration.");

        var span = WorkOrderSchedulingHorizon.InitialDays;
        while (true)
        {
            DateOnly horizonStart;
            DateOnly horizonEnd;
            try
            {
                var anchorDate = DateOnly.FromDateTime(anchor);
                horizonStart = anchorDate.AddDays(-WorkOrderSchedulingHorizon.OvernightBufferDays);
                horizonEnd = anchorDate.AddDays(span);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Placement.Failed($"Operation {operation.OperationCode} is outside the supported scheduling horizon.");
            }

            try
            {
                ScheduleMachineData data;
                long? calendarSourceId;
                DateTime? lastModified;
                if (machine is not null)
                {
                    data = await _machineCalendars.LoadAsync(order.CompanyCode, machine.MachineCode,
                        horizonStart, horizonEnd, cancellationToken);
                    calendarSourceId = null;
                    lastModified = null;
                }
                else
                {
                    var slice = await _calendarProvider.LoadPlantAsync(order.CompanyCode, order.BranchCode,
                        horizonStart, horizonEnd, cancellationToken);
                    data = slice.Data;
                    calendarSourceId = slice.SourceId;
                    lastModified = slice.LastModified;
                }

                var sourceHash = WorkOrderScheduleSourceHasher.Compute(data, horizonStart, horizonEnd);
                var effective = data;
                if (machine is not null)
                {
                    var key = Normalize(machine.MachineCode);
                    if (!occupiedByMachine.TryGetValue(key, out var occupied))
                    {
                        occupied = [];
                        occupiedByMachine[key] = occupied;
                    }
                    effective = ProductionCalendarScheduler.ExcludeOccupiedIntervals(data, occupied);
                }

                var scheduled = ProductionCalendarScheduler.ScheduleMachine(effective, anchor, duration, ScheduleDirection.Forward);
                return new Placement
                {
                    Start = scheduled.ActualStart,
                    Completion = scheduled.ActualCompletion,
                    ConsumedIntervals = scheduled.ConsumedIntervals,
                    Machine = machine,
                    SourceHash = sourceHash,
                    HorizonStart = horizonStart,
                    HorizonEnd = horizonEnd,
                    CalendarSourceId = calendarSourceId,
                    CalendarSourceLastModified = lastModified
                };
            }
            catch (ScheduleFailureException) when (span < WorkOrderSchedulingHorizon.MaxDays)
            {
                span = Math.Min(span + WorkOrderSchedulingHorizon.ExtensionDays, WorkOrderSchedulingHorizon.MaxDays);
            }
            catch (ScheduleFailureException ex)
            {
                return Placement.Failed($"Operation {operation.OperationCode} has no usable calendar capacity within {WorkOrderSchedulingHorizon.MaxDays} days. {ex.Message}");
            }
        }
    }

    private static void ApplyMachineProvenance(
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine machine,
        Placement placement)
    {
        machine.PlannedStartDateTime = placement.Start;
        machine.PlannedCompletionDateTime = placement.Completion;
        machine.CalendarSourceId = placement.CalendarSourceId;
        machine.CalendarSourceLastModified = placement.CalendarSourceLastModified;
        machine.CalendarHorizonStart = placement.HorizonStart.ToDateTime(TimeOnly.MinValue);
        machine.CalendarHorizonEnd = placement.HorizonEnd.ToDateTime(TimeOnly.MinValue);
        machine.ScheduleSourceHash = placement.SourceHash;

        operation.CalendarSourceType = ProductionCalendarSourceTypes.Machine;
        operation.CalendarSourceId = placement.CalendarSourceId;
        operation.CalendarSourceLastModified = placement.CalendarSourceLastModified;
        operation.CalendarHorizonStart = machine.CalendarHorizonStart;
        operation.CalendarHorizonEnd = machine.CalendarHorizonEnd;
        operation.ScheduleSourceHash = placement.SourceHash;
    }

    private ProductionAuditEvent ApplyProposal(
        AppDbContext db,
        ProductionWorkOrder order,
        InventoryTenantScope scope,
        MachineScheduleRescheduleRequest request,
        ScheduleProposal proposal)
    {
        var affectedMachineIds = proposal.Placements
            .Where(x => x.Placement.Machine is not null)
            .Select(x => x.Placement.Machine!.Uid)
            .Distinct().ToHashSet();
        var oldRevision = order.ScheduleRevision;
        var modifiedAt = DateTime.UtcNow;

        // Schedule-only changes intentionally freeze SnapshotRevision and SnapshotHash. The
        // snapshot hash includes planned dates; recomputing it here would turn a placement move
        // into a production-definition amendment and invalidate the released snapshot.
        order.ScheduleRevision++;
        order.ModifiedDate = modifiedAt;
        order.ModifiedBy = scope.UserId;

        foreach (var placement in proposal.Placements)
        {
            placement.Operation.ModifiedDate = modifiedAt;
            placement.Operation.ModifiedBy = scope.UserId;
            var machine = placement.Placement.Machine;
            if (machine is null)
                continue;
            machine.ScheduleRevision++;
            machine.ModifiedDate = modifiedAt;
            machine.ModifiedBy = scope.UserId;
        }

        var affectedStepIds = proposal.Placements.Select(x => x.Operation.RouteStepId)
            .Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
        foreach (var step in order.RouteSteps.Where(x => affectedStepIds.Contains(x.Uid)))
        {
            step.ModifiedDate = modifiedAt;
            step.ModifiedBy = scope.UserId;
        }

        var existing = db.ProductionMachineScheduleAllocations
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.WorkOrderId == order.Uid
                && affectedMachineIds.Contains(x.WorkOrderMachineId))
            .ToList();
        db.ProductionMachineScheduleAllocations.RemoveRange(existing);

        var createdBy = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
        foreach (var placement in proposal.Placements)
        {
            var machine = placement.Placement.Machine;
            if (machine is null)
                continue;
            var fragmentNo = 1;
            foreach (var interval in placement.Placement.ConsumedIntervals)
            {
                db.ProductionMachineScheduleAllocations.Add(new ProductionMachineScheduleAllocation
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = order.BranchCode,
                    MachineCode = machine.MachineCode,
                    WorkOrderId = order.Uid,
                    WorkOrderOperationId = placement.Operation.Uid,
                    WorkOrderMachineId = machine.Uid,
                    ScheduleRevision = machine.ScheduleRevision,
                    FragmentNo = fragmentNo++,
                    StartDateTime = interval.Start,
                    EndDateTime = interval.End,
                    ScheduleSourceHash = placement.Placement.SourceHash,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = createdBy,
                    RowVersion = string.Equals(
                        db.Database.ProviderName,
                        "Microsoft.EntityFrameworkCore.Sqlite",
                        StringComparison.Ordinal)
                        ? Guid.NewGuid().ToByteArray()
                        : []
                });
            }
        }

        if (!db.Database.IsSqlServer())
        {
            var rowVersion = Guid.NewGuid().ToByteArray();
            order.RowVersion = rowVersion;
            foreach (var placement in proposal.Placements)
            {
                placement.Operation.RowVersion = Guid.NewGuid().ToByteArray();
                if (placement.Placement.Machine is not null)
                    placement.Placement.Machine.RowVersion = Guid.NewGuid().ToByteArray();
            }
            foreach (var step in order.RouteSteps.Where(x => proposal.Placements.Any(p => p.Operation.RouteStepId == x.Uid)))
                step.RowVersion = Guid.NewGuid().ToByteArray();
        }

        var audit = new ProductionAuditEvent
        {
            WorkOrderId = order.Uid,
            EventType = ProductionAuditEventTypes.ScheduleChanged,
            FromStatus = order.Status,
            ToStatus = order.Status,
            SnapshotRevision = order.SnapshotRevision,
            Reason = request.Reason.Trim(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                OldScheduleRevision = oldRevision,
                NewScheduleRevision = order.ScheduleRevision,
                TargetOperationId = proposal.TargetOperation.Uid,
                RequestedStart = request.RequestedStart,
                PreviewToken = request.PreviewToken,
                AffectedOperationIds = proposal.Changes.Select(x => x.Operation.Uid).Distinct().Order().ToList(),
                Changes = proposal.Preview.Changes
            }),
            OccurredDate = modifiedAt,
            ActorUserId = scope.UserId
        };
        db.ProductionAuditEvents.Add(audit);
        return audit;
    }

    private static MachineScheduleReschedulePreview EmptyPreview(
        ProductionWorkOrder order,
        MachineScheduleRescheduleRequest request,
        IReadOnlyList<string> blockers,
        IReadOnlyList<string>? warnings = null)
    {
        var target = order.RouteSteps.SelectMany(x => x.Operations).FirstOrDefault(x => x.Uid == request.OperationId);
        var machine = target?.Machines.FirstOrDefault(x => x.Uid == request.WorkOrderMachineId);
        return new MachineScheduleReschedulePreview
        {
            WorkOrderId = order.Uid,
            WorkOrderNo = order.WorkOrderNo,
            TargetOperationId = request.OperationId,
            TargetMachineCode = machine?.MachineCode ?? Normalize(request.MachineCode),
            CurrentStart = target?.PlannedStartDateTime ?? default,
            CurrentCompletion = target?.PlannedCompletionDateTime ?? default,
            WorkOrderStartBefore = order.PlannedStartDateTime,
            WorkOrderCompletionBefore = order.PlannedCompletionDateTime,
            Blockers = blockers.Distinct(StringComparer.Ordinal).ToList(),
            Warnings = warnings?.Distinct(StringComparer.Ordinal).ToList() ?? []
        };
    }

    private static List<ProductionWorkOrderOperation> GetAffectedOperations(
        ProductionWorkOrder order,
        ProductionWorkOrderRouteStep targetStep,
        ProductionWorkOrderOperation targetOperation)
    {
        var stageByOperation = order.RouteSteps
            .SelectMany(step => step.Operations.Select(operation => (operation.Uid, step.StageSequence)))
            .ToDictionary(x => x.Uid, x => x.StageSequence);

        return order.RouteSteps
            .Where(step => step.StageSequence > targetStep.StageSequence || step.Uid == targetStep.Uid)
            .SelectMany(step => step.Operations.Where(operation =>
                step.StageSequence > targetStep.StageSequence
                || operation.Uid == targetOperation.Uid
                || (step.Uid == targetStep.Uid && operation.ProcessSequence > targetOperation.ProcessSequence)))
            .OrderBy(operation => stageByOperation.GetValueOrDefault(operation.Uid))
            .ThenBy(operation => operation.ProcessSequence)
            .ThenBy(operation => operation.Uid)
            .ToList();
    }

    private static DateTime? StepCompletion(ProductionWorkOrderRouteStep step) =>
        step.PlannedCompletionDateTime
        ?? step.Operations.Where(x => x.PlannedCompletionDateTime is not null)
            .Select(x => x.PlannedCompletionDateTime).Max();

    private static DateTime ResolveEarliest(
        ProductionWorkOrderOperation operation,
        DateTime cursor,
        IReadOnlyDictionary<long, DateTime> completionByStep)
    {
        var earliest = cursor;
        foreach (var producer in operation.Materials
                     .Where(x => string.Equals(x.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
                     .Select(x => x.ProducingRouteStep)
                     .Where(x => x is not null).Cast<ProductionWorkOrderRouteStep>().Distinct())
        {
            if (completionByStep.TryGetValue(producer.Uid, out var completion) && completion > earliest)
                earliest = completion;
        }
        return earliest;
    }

    private static bool HasExecutionQuantity(ProductionWorkOrderOperation operation) =>
        operation.ProcessedQty > 0m || operation.GoodQty > 0m || operation.ScrapQty > 0m
        || operation.RejectQty > 0m || operation.HoldQty > 0m || operation.ReworkQty > 0m
        || operation.TransferredQty > 0m;

    private static bool IntervalsEqual(IReadOnlyList<TimeInterval> left, IReadOnlyList<TimeInterval> right) =>
        left.Count == right.Count && left.Zip(right).All(x => x.First.Start == x.Second.Start && x.First.End == x.Second.End);

    private static string ComputePreviewToken(
        ProductionWorkOrder order,
        ProductionWorkOrderOperation targetOperation,
        ProductionWorkOrderMachine targetMachine,
        MachineScheduleRescheduleRequest request,
        IReadOnlyList<MachineScheduleRescheduleChange> changes,
        IReadOnlyList<string> sourceStamps,
        IReadOnlyList<OperationPlacement> placements)
    {
        var payload = new
        {
            order.Uid,
            order.ScheduleRevision,
            WorkOrderRowVersion = Convert.ToBase64String(order.RowVersion),
            TargetOperationId = targetOperation.Uid,
            TargetMachineId = targetMachine.Uid,
            TargetMachineScheduleRevision = targetMachine.ScheduleRevision,
            MachineRowVersion = Convert.ToBase64String(targetMachine.RowVersion),
            RequestedStart = request.RequestedStart,
            MachineCode = Normalize(request.MachineCode),
            Reason = request.Reason.Trim(),
            Changes = changes.OrderBy(x => x.OperationId).ToList(),
            SourceStamps = sourceStamps.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Proposed = placements.OrderBy(x => x.Operation.Uid).Select(x => new
            {
                x.Operation.Uid,
                x.Placement.SourceHash,
                x.Placement.Start,
                x.Placement.Completion,
                Fragments = x.Placement.ConsumedIntervals.OrderBy(i => i.Start).ThenBy(i => i.End).ToList()
            }).ToList()
        };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToHexString(SHA256.HashData(serialized));
    }

    private static IQueryable<ProductionWorkOrder> OrderQuery(
        AppDbContext db,
        InventoryTenantScope scope,
        long workOrderId,
        bool tracking)
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
        AppDbContext db,
        InventoryTenantScope scope,
        long workOrderId,
        bool tracking,
        CancellationToken cancellationToken) =>
        OrderQuery(db, scope, workOrderId, tracking).SingleOrDefaultAsync(cancellationToken);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private sealed class ScheduleProposal
    {
        public MachineScheduleReschedulePreview Preview { get; }
        public IReadOnlyList<OperationPlacement> Placements { get; }
        public ProductionWorkOrderOperation TargetOperation { get; }
        public ProductionWorkOrderMachine TargetMachine { get; }
        public IReadOnlyList<OperationPlacement> Changes => Placements;

        public ScheduleProposal(
            MachineScheduleReschedulePreview preview,
            IReadOnlyList<OperationPlacement> placements,
            ProductionWorkOrderOperation targetOperation,
            ProductionWorkOrderMachine targetMachine)
        {
            Preview = preview;
            Placements = placements;
            TargetOperation = targetOperation;
            TargetMachine = targetMachine;
        }

        private ScheduleProposal(MachineScheduleReschedulePreview preview)
        {
            Preview = preview;
            Placements = [];
            TargetOperation = new ProductionWorkOrderOperation();
            TargetMachine = new ProductionWorkOrderMachine();
        }

        public static ScheduleProposal Blocked(MachineScheduleReschedulePreview preview) => new(preview);
    }

    private sealed class CapacityLoad
    {
        public Dictionary<string, List<TimeInterval>> OccupiedByMachine { get; } = new(StringComparer.Ordinal);
        public List<string> SourceStamps { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Blockers { get; } = [];
    }

    private sealed class ActiveMachineSchedule
    {
        public string CompanyCode { get; init; } = string.Empty;
        public long WorkOrderId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
        public string BranchCode { get; init; } = string.Empty;
        public string SchedulingDirection { get; init; } = ProductionSchedulingDirections.Forward;
        public long MachineId { get; init; }
        public string MachineCode { get; init; } = string.Empty;
        public long OperationId { get; init; }
        public string OperationCode { get; init; } = string.Empty;
        public DateTime? OperationStart { get; init; }
        public DateTime? OperationCompletion { get; init; }
        public int ScheduleRevision { get; init; }
        public string? ScheduleSourceHash { get; init; }
        public DateTime? PlannedStart { get; init; }
        public DateTime? PlannedCompletion { get; init; }
        public DateTime? CalendarHorizonStart { get; init; }
        public DateTime? CalendarHorizonEnd { get; init; }
        public decimal SetupSeconds { get; init; }
        public decimal ConversionSeconds { get; init; }
        public decimal QueueSeconds { get; init; }
        public decimal PlannedRunMinutes { get; init; }
    }

    private sealed record CapacityInterval(string MachineCode, long WorkOrderId, long WorkOrderMachineId, TimeInterval Interval);

    private sealed class OperationPlacement
    {
        public ProductionWorkOrderOperation Operation { get; }
        public Placement Placement { get; }
        public OperationPlacement(ProductionWorkOrderOperation operation, Placement placement)
        {
            Operation = operation;
            Placement = placement;
        }
    }

    private sealed class Placement
    {
        public DateTime Start { get; init; }
        public DateTime Completion { get; init; }
        public IReadOnlyList<TimeInterval> ConsumedIntervals { get; init; } = [];
        public ProductionWorkOrderMachine? Machine { get; init; }
        public string SourceHash { get; init; } = string.Empty;
        public DateOnly HorizonStart { get; init; }
        public DateOnly HorizonEnd { get; init; }
        public long? CalendarSourceId { get; init; }
        public DateTime? CalendarSourceLastModified { get; init; }
        public string? Error { get; init; }

        public static Placement Failed(string message) => new() { Error = message };
    }
}
