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

public sealed class MachineScheduleAllocationBackfillFinding
{
    public string Code { get; init; } = string.Empty;
    public string? WorkOrderNo { get; init; }
    public string? MachineCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsBlocking { get; init; }
}

public sealed class MachineScheduleAllocationBackfillReport
{
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string PreviewToken { get; init; } = string.Empty;
    public bool CanApply { get; init; }
    public int ActiveMachineOperationCount { get; init; }
    public int AlreadyVerifiedCount { get; init; }
    public int MissingAllocationCount { get; init; }
    public int UnverifiedCount { get; init; }
    public int OverlappingMachineCount { get; init; }
    public IReadOnlyList<MachineScheduleAllocationBackfillFinding> Findings { get; init; } = [];
}

public interface IMachineScheduleAllocationBackfillService
{
    Task<IvMasterOperationResult<MachineScheduleAllocationBackfillReport>> PreviewBackfillAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MachineScheduleAllocationBackfillReport>> ApplyVerifiedBackfillAsync(
        string previewToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Controlled, branch-targeted backfill of already persisted schedules. Preview performs reads
/// only. Apply rechecks under the same machine, scheduling-source, and Work Order execution locks
/// used by release and reschedule commands; it never changes schedule dates or revisions.
/// </summary>
public sealed class MachineScheduleAllocationBackfillService : IMachineScheduleAllocationBackfillService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly IProductionCalendarScheduleDataLoader _machineCalendars;

    public MachineScheduleAllocationBackfillService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        IProductionCalendarScheduleDataLoader machineCalendars)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _machineCalendars = machineCalendars;
    }

    public async Task<IvMasterOperationResult<MachineScheduleAllocationBackfillReport>> PreviewBackfillAsync(
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MachineSchedulePlannerReadinessService.IsAllocationSchemaReadyAsync(db, cancellationToken))
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                IvMasterErrorCode.Validation, "Machine schedule allocation schema is not ready for a backfill dry-run.");
        var report = await BuildReportAsync(db, authorization.Scope!, cancellationToken);
        return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Ok(report.Report);
    }

    public async Task<IvMasterOperationResult<MachineScheduleAllocationBackfillReport>> ApplyVerifiedBackfillAsync(
        string previewToken,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);
        if (string.IsNullOrWhiteSpace(previewToken))
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                IvMasterErrorCode.Validation, "Run a fresh dry-run before applying allocation backfill.");

        var scope = authorization.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MachineSchedulePlannerReadinessService.IsAllocationSchemaReadyAsync(db, cancellationToken))
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                IvMasterErrorCode.Validation, "Machine schedule allocation schema is not ready for backfill.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Discover physical machine and target Work Order locks. The authoritative data is
            // loaded again after locks so this first read is only a lock-set seed.
            var seedRows = await LoadActiveSchedulesAsync(db, scope.CompanyCode, cancellationToken);
            await ProductionMachineScheduleLock.AcquireManyAsync(
                db, scope.CompanyCode, seedRows.Select(x => x.MachineCode), cancellationToken);
            await WorkOrderSchedulingLock.AcquireAsync(
                db, scope.CompanyCode, exclusive: true, cancellationToken);

            foreach (var workOrderId in seedRows
                         .Where(x => SameBranch(x.BranchCode, scope.BranchCode))
                         .Select(x => x.WorkOrderId).Distinct().Order())
            {
                var branchCode = seedRows.First(x => x.WorkOrderId == workOrderId).BranchCode;
                await ProductionWorkOrderExecutionLock.AcquireAsync(
                    db, scope.CompanyCode, branchCode, workOrderId, cancellationToken);
            }

            var current = await BuildReportAsync(db, scope, cancellationToken);
            if (!current.Report.CanApply)
            {
                await transaction.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                    IvMasterErrorCode.Validation,
                    "Backfill is blocked because one or more active schedules could not be verified. Review the dry-run findings.");
            }
            if (!string.Equals(current.Report.PreviewToken, previewToken, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                    IvMasterErrorCode.Concurrency, "Schedule or allocation inputs changed after dry-run. Run the dry-run again.");
            }

            var additions = current.Candidates.Where(x => x.IsMissing).ToList();
            var createdBy = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
            foreach (var addition in additions)
            {
                var fragmentNo = 1;
                foreach (var interval in addition.Intervals)
                {
                    db.ProductionMachineScheduleAllocations.Add(new ProductionMachineScheduleAllocation
                    {
                        CompanyCode = scope.CompanyCode,
                        BranchCode = addition.Schedule.BranchCode,
                        MachineCode = addition.Schedule.MachineCode,
                        WorkOrderId = addition.Schedule.WorkOrderId,
                        WorkOrderOperationId = addition.Schedule.OperationId,
                        WorkOrderMachineId = addition.Schedule.MachineId,
                        ScheduleRevision = addition.Schedule.ScheduleRevision,
                        FragmentNo = fragmentNo++,
                        StartDateTime = interval.Start,
                        EndDateTime = interval.End,
                        ScheduleSourceHash = addition.Schedule.ScheduleSourceHash,
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

            var occurred = DateTime.UtcNow;
            var backfillRunId = Guid.NewGuid();
            foreach (var group in additions.GroupBy(x => x.Schedule.WorkOrderId))
            {
                var schedule = group.First().Schedule;
                db.ProductionAuditEvents.Add(new ProductionAuditEvent
                {
                    WorkOrderId = schedule.WorkOrderId,
                    EventType = ProductionAuditEventTypes.ScheduleAllocationBackfilled,
                    FromStatus = schedule.WorkOrderStatus,
                    ToStatus = schedule.WorkOrderStatus,
                    SnapshotRevision = schedule.SnapshotRevision,
                    Reason = "Verified active schedule allocation backfill",
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        RunId = backfillRunId,
                        ScheduleRevision = schedule.ScheduleRevision,
                        Machines = group.Select(x => new
                        {
                            x.Schedule.MachineId,
                            x.Schedule.MachineCode,
                            x.Schedule.OperationId,
                            Fragments = x.Intervals
                        }).ToList()
                    }),
                    OccurredDate = occurred,
                    ActorUserId = scope.UserId
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            var completed = await BuildReportAsync(db, scope, cancellationToken);
            if (!completed.Report.CanApply || completed.Report.MissingAllocationCount > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(
                    IvMasterErrorCode.Validation,
                    "Backfill verification failed after writing the candidate fragments. The transaction was rolled back.");
            }
            await transaction.CommitAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Ok(completed.Report);
        }
        catch (ProductionMachineScheduleLockException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (ProductionWorkOrderExecutionLockException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleAllocationBackfillReport>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
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

    private async Task<BackfillBuild> BuildReportAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var schedules = await LoadActiveSchedulesAsync(db, scope.CompanyCode, cancellationToken);
        var activeWorkOrders = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && (x.Status == ProductionWorkOrderStatuses.Released
                    || x.Status == ProductionWorkOrderStatuses.InProgress))
            .Select(x => new { x.Uid, x.BranchCode, x.WorkOrderNo })
            .ToListAsync(cancellationToken);
        var activeWorkOrderIds = activeWorkOrders.Select(x => x.Uid).ToList();
        var activeWorkOrderBranches = activeWorkOrders.ToDictionary(x => x.Uid, x => x.BranchCode);
        var allocations = new List<ProductionMachineScheduleAllocation>();
        foreach (var batch in activeWorkOrderIds.Chunk(800))
        {
            allocations.AddRange(await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && batch.Contains(x.WorkOrderId))
                .OrderBy(x => x.WorkOrderId).ThenBy(x => x.WorkOrderMachineId)
                .ThenBy(x => x.ScheduleRevision).ThenBy(x => x.FragmentNo)
                .ToListAsync(cancellationToken));
        }

        var findings = new List<MachineScheduleAllocationBackfillFinding>();
        var candidates = new List<BackfillCandidate>();
        var byMachine = new Dictionary<string, List<(ScheduleRow Schedule, TimeInterval Interval)>>(StringComparer.Ordinal);
        var currentMachineIds = schedules.Select(x => x.MachineId).ToHashSet();
        var allocationsByMachine = allocations.GroupBy(x => x.WorkOrderMachineId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var verifiedInScope = 0;
        var missingInScope = 0;
        var unverifiedInScope = 0;

        foreach (var schedule in schedules.OrderBy(x => Normalize(x.MachineCode), StringComparer.Ordinal)
                     .ThenBy(x => x.PlannedStart).ThenBy(x => x.WorkOrderId).ThenBy(x => x.MachineId))
        {
            var machineAllocations = allocationsByMachine.TryGetValue(schedule.MachineId, out var savedForMachine)
                ? savedForMachine
                : [];

            var reconstructed = await ReconstructAsync(schedule, cancellationToken);
            if (reconstructed.Intervals is null)
            {
                if (SameBranch(schedule.BranchCode, scope.BranchCode))
                    unverifiedInScope++;
                findings.Add(Finding("SCHEDULE_UNVERIFIED", schedule, scope.BranchCode,
                    reconstructed.Error ?? "Saved schedule could not be reconstructed.", blocking: true));
                continue;
            }

            var currentAllocations = machineAllocations
                .Where(x => x.ScheduleRevision == schedule.ScheduleRevision)
                .OrderBy(x => x.FragmentNo).ToList();
            if (machineAllocations.Any(x => x.ScheduleRevision != schedule.ScheduleRevision))
            {
                if (SameBranch(schedule.BranchCode, scope.BranchCode))
                    unverifiedInScope++;
                findings.Add(Finding("STALE_REVISION", schedule, scope.BranchCode,
                    "Allocation rows exist for an obsolete machine schedule revision. Resolve them before backfill.", blocking: true));
            }

            var existingMatches = currentAllocations.Count > 0
                && IntervalsEqual(currentAllocations, reconstructed.Intervals)
                && currentAllocations.All(x => x.ScheduleSourceHash == schedule.ScheduleSourceHash
                    && string.Equals(x.CompanyCode, schedule.CompanyCode, StringComparison.OrdinalIgnoreCase)
                    && SameBranch(x.BranchCode, schedule.BranchCode)
                    && x.WorkOrderId == schedule.WorkOrderId
                    && x.WorkOrderOperationId == schedule.OperationId
                    && string.Equals(Normalize(x.MachineCode), Normalize(schedule.MachineCode), StringComparison.Ordinal));
            var isMissing = machineAllocations.Count == 0;
            if (!isMissing && !existingMatches)
            {
                if (SameBranch(schedule.BranchCode, scope.BranchCode))
                    unverifiedInScope++;
                findings.Add(Finding("ALLOCATION_MISMATCH", schedule, scope.BranchCode,
                    "Existing allocation fragments do not match the persisted schedule and calendar provenance. They were left untouched.", blocking: true));
            }
            else if (isMissing)
            {
                if (SameBranch(schedule.BranchCode, scope.BranchCode))
                {
                    missingInScope++;
                    findings.Add(Finding("MISSING_ALLOCATION", schedule, scope.BranchCode,
                        "Schedule was reproduced; a verified allocation can be added by Apply.", blocking: false));
                    candidates.Add(new BackfillCandidate(schedule, reconstructed.Intervals, IsMissing: true));
                }
                else
                {
                    findings.Add(Finding("OTHER_BRANCH_MISSING_ALLOCATION", schedule, scope.BranchCode,
                        "Schedule is included in company-wide capacity checks. Run backfill from this Work Order's branch to add its fragments.", blocking: false));
                }
            }
            else if (existingMatches && SameBranch(schedule.BranchCode, scope.BranchCode))
            {
                verifiedInScope++;
                candidates.Add(new BackfillCandidate(schedule, reconstructed.Intervals, IsMissing: false));
            }

            var key = Normalize(schedule.MachineCode);
            if (!byMachine.TryGetValue(key, out var intervals))
                byMachine[key] = intervals = [];
            intervals.AddRange(reconstructed.Intervals.Select(x => (schedule, x)));
        }

        foreach (var allocation in allocations.Where(x => !currentMachineIds.Contains(x.WorkOrderMachineId)))
        {
            var inScope = activeWorkOrderBranches.TryGetValue(allocation.WorkOrderId, out var allocationBranch)
                && SameBranch(allocationBranch, scope.BranchCode);
            if (inScope)
                unverifiedInScope++;
            findings.Add(new MachineScheduleAllocationBackfillFinding
            {
                Code = "ORPHANED_ALLOCATION",
                WorkOrderNo = inScope ? activeWorkOrders.First(x => x.Uid == allocation.WorkOrderId).WorkOrderNo : null,
                Message = inScope
                    ? $"Allocation {allocation.Uid} belongs to an active Work Order but no longer matches a selected active machine operation."
                    : $"An allocation row belongs to an active Work Order outside this branch but no longer matches a selected active machine operation.",
                MachineCode = allocation.MachineCode,
                IsBlocking = true
            });
        }

        var overlappingMachines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in byMachine)
        {
            var ordered = group.Value.OrderBy(x => x.Interval.Start).ThenBy(x => x.Interval.End)
                .ThenBy(x => x.Schedule.WorkOrderId).ThenBy(x => x.Schedule.MachineId).ToList();
            if (ordered.Count == 0)
                continue;
            var furthestEnding = ordered[0];
            for (var i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i];
                if (furthestEnding.Interval.End > current.Interval.Start)
                {
                    overlappingMachines.Add(group.Key);
                    var bothInScope = SameBranch(furthestEnding.Schedule.BranchCode, scope.BranchCode)
                        && SameBranch(current.Schedule.BranchCode, scope.BranchCode);
                    findings.Add(new MachineScheduleAllocationBackfillFinding
                    {
                        Code = "ACTIVE_MACHINE_OVERLAP",
                        WorkOrderNo = bothInScope ? current.Schedule.WorkOrderNo : null,
                        MachineCode = current.Schedule.MachineCode,
                        Message = bothInScope
                            ? $"Work Orders {furthestEnding.Schedule.WorkOrderNo} and {current.Schedule.WorkOrderNo} overlap on machine {current.Schedule.MachineCode}."
                            : $"Active Work Order schedules overlap on company machine {current.Schedule.MachineCode}; review the affected branch before backfill.",
                        IsBlocking = true
                    });
                }
                if (current.Interval.End > furthestEnding.Interval.End)
                    furthestEnding = current;
            }
        }

        var blockers = findings.Count(x => x.IsBlocking);
        var reportToken = ComputeToken(scope, schedules, allocations, candidates);
        var report = new MachineScheduleAllocationBackfillReport
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode!,
            PreviewToken = reportToken,
            CanApply = blockers == 0,
            ActiveMachineOperationCount = schedules.Count(x => SameBranch(x.BranchCode, scope.BranchCode)),
            AlreadyVerifiedCount = verifiedInScope,
            MissingAllocationCount = missingInScope,
            UnverifiedCount = unverifiedInScope,
            OverlappingMachineCount = overlappingMachines.Count,
            Findings = findings
        };
        return new BackfillBuild(report, candidates);
    }

    private async Task<List<ScheduleRow>> LoadActiveSchedulesAsync(
        AppDbContext db,
        string companyCode,
        CancellationToken cancellationToken) => await db.ProductionWorkOrderMachines.AsNoTracking()
        .Where(machine => machine.IsSelected
            && machine.Operation != null
            && machine.Operation.WorkOrder != null
            && machine.Operation.WorkOrder.CompanyCode == companyCode
            && (machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                || machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
            && (machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds
                + machine.PlannedRunMinutes * 60m) > 0m)
        .Select(machine => new ScheduleRow
        {
            CompanyCode = machine.Operation!.WorkOrder!.CompanyCode,
            BranchCode = machine.Operation.WorkOrder.BranchCode,
            WorkOrderId = machine.Operation.WorkOrder.Uid,
            WorkOrderNo = machine.Operation.WorkOrder.WorkOrderNo,
            WorkOrderStatus = machine.Operation.WorkOrder.Status,
            SnapshotRevision = machine.Operation.WorkOrder.SnapshotRevision,
            SchedulingDirection = machine.Operation.WorkOrder.SchedulingDirection,
            MachineId = machine.Uid,
            MachineCode = machine.MachineCode,
            OperationId = machine.OperationId,
            OperationCode = machine.Operation.OperationCode,
            OperationStart = machine.Operation.PlannedStartDateTime,
            OperationCompletion = machine.Operation.PlannedCompletionDateTime,
            PlannedStart = machine.PlannedStartDateTime,
            PlannedCompletion = machine.PlannedCompletionDateTime,
            CalendarHorizonStart = machine.CalendarHorizonStart,
            CalendarHorizonEnd = machine.CalendarHorizonEnd,
            ScheduleRevision = machine.ScheduleRevision,
            ScheduleSourceHash = machine.ScheduleSourceHash,
            SetupSeconds = machine.SetupSeconds,
            ConversionSeconds = machine.ConversionSeconds,
            QueueSeconds = machine.QueueSeconds,
            PlannedRunMinutes = machine.PlannedRunMinutes,
            RowVersion = machine.RowVersion,
            WorkOrderScheduleRevision = machine.Operation.WorkOrder.ScheduleRevision,
            WorkOrderRowVersion = machine.Operation.WorkOrder.RowVersion
        })
        .OrderBy(x => x.MachineCode).ThenBy(x => x.PlannedStart).ThenBy(x => x.WorkOrderId)
        .ToListAsync(cancellationToken);

    private async Task<Reconstruction> ReconstructAsync(ScheduleRow schedule, CancellationToken cancellationToken)
    {
        if (!ProductionSchedulingDirections.IsKnown(schedule.SchedulingDirection))
            return Reconstruction.Failed("Saved Work Order scheduling direction is unknown.");
        if (schedule.PlannedStart is null || schedule.PlannedCompletion is null
            || schedule.OperationStart is null || schedule.OperationCompletion is null
            || schedule.CalendarHorizonStart is null || schedule.CalendarHorizonEnd is null
            || string.IsNullOrWhiteSpace(schedule.ScheduleSourceHash)
            || schedule.PlannedCompletion <= schedule.PlannedStart
            || schedule.OperationStart != schedule.PlannedStart
            || schedule.OperationCompletion != schedule.PlannedCompletion)
            return Reconstruction.Failed("Persisted machine and operation dates or schedule provenance are incomplete or inconsistent.");

        try
        {
            var horizonStart = DateOnly.FromDateTime(schedule.CalendarHorizonStart.Value);
            var horizonEnd = DateOnly.FromDateTime(schedule.CalendarHorizonEnd.Value);
            if (horizonEnd < horizonStart)
                return Reconstruction.Failed("Saved calendar horizon is invalid.");

            var data = await _machineCalendars.LoadAsync(
                schedule.CompanyCode, schedule.MachineCode, horizonStart, horizonEnd, cancellationToken);
            var sourceHash = WorkOrderScheduleSourceHasher.Compute(data, horizonStart, horizonEnd);
            if (!string.Equals(sourceHash, schedule.ScheduleSourceHash, StringComparison.Ordinal))
                return Reconstruction.Failed("Saved calendar provenance no longer matches the current machine calendar.");

            var direction = string.Equals(schedule.SchedulingDirection, ProductionSchedulingDirections.Backward,
                StringComparison.OrdinalIgnoreCase) ? ScheduleDirection.Backward : ScheduleDirection.Forward;
            var duration = TimeSpan.FromSeconds((double)(schedule.SetupSeconds + schedule.ConversionSeconds + schedule.QueueSeconds))
                + TimeSpan.FromMinutes((double)schedule.PlannedRunMinutes);
            if (duration <= TimeSpan.Zero)
                return Reconstruction.Failed("Saved productive duration is not positive.");

            var anchor = direction == ScheduleDirection.Backward ? schedule.PlannedCompletion.Value : schedule.PlannedStart.Value;
            var result = ProductionCalendarScheduler.ScheduleMachine(data, anchor, duration, direction);
            if (result.ActualStart != schedule.PlannedStart || result.ActualCompletion != schedule.PlannedCompletion)
                return Reconstruction.Failed("Canonical calendar reconstruction does not exactly match the saved outer schedule dates.");
            if (result.ConsumedIntervals.Count == 0)
                return Reconstruction.Failed("Canonical calendar reconstruction returned no productive fragments.");
            return new Reconstruction(result.ConsumedIntervals.OrderBy(x => x.Start).ThenBy(x => x.End).ToList(), null);
        }
        catch (ScheduleFailureException ex)
        {
            return Reconstruction.Failed($"Canonical schedule reconstruction failed: {ex.Message}");
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reconstruction.Failed("Saved calendar horizon is outside the supported date range.");
        }
    }

    private static bool IntervalsEqual(
        IReadOnlyList<ProductionMachineScheduleAllocation> existing,
        IReadOnlyList<TimeInterval> reconstructed)
    {
        if (existing.Count != reconstructed.Count)
            return false;
        var ordered = existing.OrderBy(x => x.FragmentNo).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].FragmentNo != i + 1
                || ordered[i].StartDateTime != reconstructed[i].Start
                || ordered[i].EndDateTime != reconstructed[i].End)
                return false;
        }
        return true;
    }

    private static MachineScheduleAllocationBackfillFinding Finding(
        string code,
        ScheduleRow schedule,
        string? visibleBranchCode,
        string message,
        bool blocking) => new()
    {
        Code = code,
        WorkOrderNo = SameBranch(schedule.BranchCode, visibleBranchCode) ? schedule.WorkOrderNo : null,
        MachineCode = schedule.MachineCode,
        Message = message,
        IsBlocking = blocking
    };

    private static string ComputeToken(
        InventoryTenantScope scope,
        IReadOnlyList<ScheduleRow> schedules,
        IReadOnlyList<ProductionMachineScheduleAllocation> allocations,
        IReadOnlyList<BackfillCandidate> candidates)
    {
        var payload = new
        {
            scope.CompanyCode,
            scope.BranchCode,
            Schedules = schedules.OrderBy(x => x.MachineId).Select(x => new
            {
                x.WorkOrderId,
                x.WorkOrderScheduleRevision,
                WorkOrderRowVersion = Convert.ToBase64String(x.WorkOrderRowVersion),
                x.BranchCode,
                x.MachineId,
                x.MachineCode,
                x.OperationId,
                x.ScheduleRevision,
                x.PlannedStart,
                x.PlannedCompletion,
                x.OperationStart,
                x.OperationCompletion,
                x.CalendarHorizonStart,
                x.CalendarHorizonEnd,
                x.ScheduleSourceHash,
                RowVersion = Convert.ToBase64String(x.RowVersion)
            }).ToList(),
            Allocations = allocations.OrderBy(x => x.Uid).Select(x => new
            {
                x.Uid,
                x.WorkOrderId,
                x.WorkOrderMachineId,
                x.ScheduleRevision,
                x.FragmentNo,
                x.StartDateTime,
                x.EndDateTime,
                x.BranchCode,
                x.WorkOrderOperationId,
                x.MachineCode,
                x.ScheduleSourceHash
            }).ToList(),
            Candidates = candidates.OrderBy(x => x.Schedule.MachineId).Select(x => new
            {
                x.Schedule.MachineId,
                x.IsMissing,
                Fragments = x.Intervals
            }).ToList()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    private static bool SameBranch(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private sealed class ScheduleRow
    {
        public string CompanyCode { get; init; } = string.Empty;
        public string BranchCode { get; init; } = string.Empty;
        public long WorkOrderId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
        public string WorkOrderStatus { get; init; } = string.Empty;
        public int SnapshotRevision { get; init; }
        public int WorkOrderScheduleRevision { get; init; }
        public byte[] WorkOrderRowVersion { get; init; } = [];
        public string SchedulingDirection { get; init; } = ProductionSchedulingDirections.Forward;
        public long MachineId { get; init; }
        public string MachineCode { get; init; } = string.Empty;
        public long OperationId { get; init; }
        public string OperationCode { get; init; } = string.Empty;
        public DateTime? OperationStart { get; init; }
        public DateTime? OperationCompletion { get; init; }
        public DateTime? PlannedStart { get; init; }
        public DateTime? PlannedCompletion { get; init; }
        public DateTime? CalendarHorizonStart { get; init; }
        public DateTime? CalendarHorizonEnd { get; init; }
        public int ScheduleRevision { get; init; }
        public string? ScheduleSourceHash { get; init; }
        public decimal SetupSeconds { get; init; }
        public decimal ConversionSeconds { get; init; }
        public decimal QueueSeconds { get; init; }
        public decimal PlannedRunMinutes { get; init; }
        public byte[] RowVersion { get; init; } = [];
    }

    private sealed record Reconstruction(IReadOnlyList<TimeInterval>? Intervals, string? Error)
    {
        public static Reconstruction Failed(string error) => new(null, error);
    }

    private sealed record BackfillCandidate(
        ScheduleRow Schedule,
        IReadOnlyList<TimeInterval> Intervals,
        bool IsMissing);

    private sealed record BackfillBuild(
        MachineScheduleAllocationBackfillReport Report,
        IReadOnlyList<BackfillCandidate> Candidates);
}

