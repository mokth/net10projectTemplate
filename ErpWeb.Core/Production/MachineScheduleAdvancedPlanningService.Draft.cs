using System.Security.Cryptography;
using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class MachineScheduleAdvancedPlanningService
{
    public async Task<IvMasterOperationResult<MachineScheduleDraftPreview>> PreviewDraftAsync(
        MachineScheduleDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleDraftPreview>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);

        var invalid = ValidateDraftRequest(request, requireToken: false);
        if (invalid is not null)
            return IvMasterOperationResult<MachineScheduleDraftPreview>.Fail(IvMasterErrorCode.Validation, invalid);

        var writeCheck = await CheckDraftWritesAsync(request.Commands, cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = authorization.Scope!;

        var explicitOrderIds = request.Commands.Select(x => x.WorkOrderId).Distinct().ToList();
        var preloaded = await LoadOrdersByIdsAsync(db, scope, explicitOrderIds, tracking: false, cancellationToken);
        var versionError = ValidateDraftExpectedVersions(preloaded, request);
        if (versionError is not null)
            return IvMasterOperationResult<MachineScheduleDraftPreview>.Fail(IvMasterErrorCode.Concurrency, versionError);

        var engine = new MachineScheduleAdvancedPlacementEngine(_machineCalendars, _calendarProvider);
        var overlay = await MachineScheduleDraftOverlay.InitializeAsync(
            db, scope, _capacity, _uom, engine, explicitOrderIds, cancellationToken);

        var exec = await ExecuteDraftAsync(db, scope, engine, overlay, request, lockedWorkOrderIds: null,
            lockedMachineCodes: null, cancellationToken);
        if (writeCheck.Error is not null && !exec.Blockers.Contains(writeCheck.Error, StringComparer.Ordinal))
            exec.Blockers.Add(writeCheck.Error);

        string? token = null;
        if (exec.Blockers.Count == 0 && exec.Conflicts.Count == 0 && writeCheck.Error is null)
            token = await ComputeDraftTokenAsync(db, scope, request, exec.TouchedOrderIds, exec.TouchedMachineIds, cancellationToken);

        return IvMasterOperationResult<MachineScheduleDraftPreview>.Ok(new MachineScheduleDraftPreview
        {
            BoardBaseFingerprint = request.BoardBaseFingerprint,
            ResultingSegments = exec.ResultingSegments,
            AffectedWorkOrders = exec.AffectedWorkOrders,
            Conflicts = exec.Conflicts,
            Warnings = exec.Warnings,
            Blockers = exec.Blockers,
            PreviewToken = token
        });
    }

    public async Task<IvMasterOperationResult<MachineScheduleDraftCommitResult>> CommitDraftAsync(
        MachineScheduleDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);

        var invalid = ValidateDraftRequest(request, requireToken: true);
        if (invalid is not null)
            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(IvMasterErrorCode.Validation, invalid);

        var writeCheck = await CheckDraftWritesAsync(request.Commands, cancellationToken);
        if (writeCheck.Error is not null)
            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(IvMasterErrorCode.Validation, writeCheck.Error);

        var scope = authorization.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var explicitOrderIds = request.Commands.Select(x => x.WorkOrderId).Distinct().ToList();
            var seedOrders = await LoadOrdersByIdsAsync(db, scope, explicitOrderIds, tracking: false, cancellationToken);
            var versionError = ValidateDraftExpectedVersions(seedOrders, request);
            if (versionError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency, versionError);
            }

            var currentSetsByMachine = await db.ProductionMachineScheduleSegmentSets.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.IsCurrent)
                .Select(x => new { x.Uid, x.WorkOrderMachineId })
                .ToDictionaryAsync(x => x.WorkOrderMachineId, x => x.Uid, cancellationToken);
            var branchSegments = await db.ProductionMachineScheduleSegments.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
                .Select(x => new { x.WorkOrderId, x.WorkOrderMachineId, x.SegmentSetId, x.MachineCode })
                .ToListAsync(cancellationToken);
            var activeBranchSegments = branchSegments
                .Where(x => !currentSetsByMachine.TryGetValue(x.WorkOrderMachineId, out var setId) || x.SegmentSetId == setId)
                .ToList();

            var seedMachineCodes = seedOrders
                .SelectMany(o => o.RouteSteps.SelectMany(s => s.Operations.SelectMany(op => op.Machines.Where(m => m.IsSelected).Select(m => m.MachineCode))))
                .Concat(request.Commands.Select(c => c.MachineCode))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(MachineScheduleAdvancedPlacementEngine.Normalize)
                .ToHashSet(StringComparer.Ordinal);

            var victimIds = activeBranchSegments
                .Where(s => seedMachineCodes.Contains(MachineScheduleAdvancedPlacementEngine.Normalize(s.MachineCode)))
                .Select(s => s.WorkOrderId)
                .Distinct()
                .ToList();

            if (victimIds.Count > 0)
            {
                var victimMachineCodes = await db.ProductionWorkOrderMachines.AsNoTracking()
                    .Where(m => m.IsSelected && m.Operation != null && m.Operation.WorkOrderId != null
                        && victimIds.Contains(m.Operation.WorkOrderId.Value))
                    .Select(m => m.MachineCode)
                    .ToListAsync(cancellationToken);
                foreach (var code in victimMachineCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
                    seedMachineCodes.Add(MachineScheduleAdvancedPlacementEngine.Normalize(code));
            }

            var lockedWorkOrderIds = explicitOrderIds.Concat(victimIds).Distinct().ToHashSet();
            var lockedMachineCodes = seedMachineCodes;

            await ProductionMachineScheduleLock.AcquireManyAsync(db, scope.CompanyCode, lockedMachineCodes, cancellationToken);
            await WorkOrderSchedulingLock.AcquireAsync(db, scope.CompanyCode, exclusive: false, cancellationToken);
            await ProductionWorkOrderExecutionLock.AcquireManyAsync(db, scope.CompanyCode, scope.BranchCode!, lockedWorkOrderIds, cancellationToken);

            var lockedOrders = await LoadOrdersByIdsAsync(db, scope, explicitOrderIds, tracking: false, cancellationToken);
            var lockedVersionError = ValidateDraftExpectedVersions(lockedOrders, request);
            if (lockedVersionError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency, lockedVersionError);
            }

            var currentWriteCheck = await CheckDraftWritesAsync(request.Commands, cancellationToken);
            if (currentWriteCheck.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(
                    IvMasterErrorCode.Validation, currentWriteCheck.Error);
            }

            var engine = new MachineScheduleAdvancedPlacementEngine(_machineCalendars, _calendarProvider);
            var overlay = await MachineScheduleDraftOverlay.InitializeAsync(
                db, scope, _capacity, _uom, engine, explicitOrderIds, cancellationToken);

            // Execute inside locked tx to validate steps & check §21 lock-scope expansion.
            var exec = await ExecuteDraftAsync(db, scope, engine, overlay, request, lockedWorkOrderIds,
                lockedMachineCodes, cancellationToken);
            if (exec.ScopeExpanded)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency,
                    "Schedule scope expanded after acquiring locks. Refresh the preview before committing.");
            }
            if (exec.Blockers.Count > 0 || exec.Conflicts.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                var message = string.Join(" ", exec.Blockers.Concat(exec.Conflicts.Select(c => c.Message)).Distinct(StringComparer.Ordinal));
                return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(IvMasterErrorCode.Validation, message);
            }

            // Compute token against locked pre-mutation DB rows + commands and verify against PreviewToken.
            var token = await ComputeDraftTokenAsync(db, scope, request, exec.TouchedOrderIds, exec.TouchedMachineIds, cancellationToken);
            if (!string.Equals(token, request.PreviewToken, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(
                    IvMasterErrorCode.Concurrency,
                    "Schedule inputs changed after preview. Refresh the preview before committing.");
            }

            await PersistDraftAsync(db, overlay, exec, scope, request, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Ok(new MachineScheduleDraftCommitResult
            {
                AppliedCommandCount = request.Commands.Count,
                AffectedWorkOrderCount = exec.AffectedWorkOrders.Count,
                AffectedWorkOrderIds = exec.AffectedWorkOrders.Select(x => x.WorkOrderId).ToList(),
                HasUnscheduledRemaining = exec.AffectedWorkOrders.Any(x => x.HasUnscheduledRemaining),
                Warnings = exec.Warnings,
                AffectedWorkOrders = exec.AffectedWorkOrders,
                Message = $"Committed {request.Commands.Count} draft schedule change(s) across {exec.AffectedWorkOrders.Count} Work Order(s)."
            });
        }
        catch (ProductionMachineScheduleLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (ProductionWorkOrderExecutionLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleDraftCommitResult>.Fail(
                IvMasterErrorCode.Concurrency,
                "The Work Order or machine schedule changed while committing. Refresh and retry.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<(bool Ok, string? Error)> CheckDraftWritesAsync(
        IReadOnlyList<MachineScheduleDraftCommand> commands,
        CancellationToken cancellationToken)
    {
        var needsSegmentSetting = commands.Any(x => x.Kind != MachineScheduleDraftCommandKind.Move);
        if (needsSegmentSetting)
        {
            var check = await CheckWritesAsync(cancellationToken);
            return check.Error is null ? (true, null) : (false, check.Error);
        }

        var readiness = await _readiness.GetReadinessAsync(cancellationToken);
        if (!readiness.IsWriteReady)
            return (false, "Machine scheduling writes are disabled until planner readiness passes. " + string.Join(" ", readiness.BlockingReasons));
        return (true, null);
    }

    private static string? ValidateDraftRequest(MachineScheduleDraftRequest request, bool requireToken)
    {
        if (request is null || request.Commands.Count == 0)
            return "Stage at least one schedule change before previewing or committing.";
        if (request.Commands.Count > 50)
            return "Draft batch limit exceeded (maximum 50 commands per commit).";
        foreach (var cmd in request.Commands)
        {
            if (cmd.WorkOrderId <= 0 || cmd.OperationId <= 0 || cmd.WorkOrderMachineId <= 0)
                return "Every draft command must reference a valid Work Order, operation, and selected machine.";
            if (string.IsNullOrWhiteSpace(cmd.MachineCode))
                return "Every draft command requires a machine code.";
            if (string.IsNullOrWhiteSpace(cmd.Reason) || cmd.Reason.Trim().Length > 500)
                return "Every draft command requires a reason of no more than 500 characters.";
            if (cmd.Kind != MachineScheduleDraftCommandKind.MaintenanceReplan && cmd.RequestedStart is null)
                return $"Draft command {cmd.Kind} for {cmd.WorkOrderNo} requires a requested start.";
            if (cmd.Kind == MachineScheduleDraftCommandKind.MaintenanceReplan && cmd.MaintenanceId is null && cmd.DowntimeStart is null)
                return $"Maintenance draft command for {cmd.WorkOrderNo} requires downtime details.";
        }
        if (requireToken && string.IsNullOrWhiteSpace(request.PreviewToken))
            return "Run Preview Changes before committing the draft.";
        return null;
    }

    private static async Task<List<ProductionWorkOrder>> LoadOrdersByIdsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        IReadOnlyCollection<long> ids,
        bool tracking,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return [];
        var result = new List<ProductionWorkOrder>();
        foreach (var batch in ids.Distinct().Chunk(200))
        {
            IQueryable<ProductionWorkOrder> query = db.ProductionWorkOrders
                .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Machines)
                .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Materials).ThenInclude(x => x.ProducingRouteStep)
                .AsSplitQuery()
                .Where(x => batch.Contains(x.Uid) && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
            if (!tracking)
                query = query.AsNoTracking();
            result.AddRange(await query.ToListAsync(cancellationToken));
        }
        return result;
    }

    private static string? ValidateDraftExpectedVersions(
        IReadOnlyList<ProductionWorkOrder> orders,
        MachineScheduleDraftRequest request)
    {
        var orderById = orders.ToDictionary(x => x.Uid);
        foreach (var ver in request.ExpectedVersions)
        {
            if (!orderById.TryGetValue(ver.WorkOrderId, out var order))
                return $"Work Order {ver.WorkOrderId} was not found.";
            if (order.ScheduleRevision != ver.ExpectedWorkOrderScheduleRevision
                || !order.RowVersion.SequenceEqual(ver.ExpectedWorkOrderRowVersion))
                return $"Work Order {order.WorkOrderNo} changed since the board was loaded. Refresh and try again.";

            var machine = order.RouteSteps.SelectMany(x => x.Operations)
                .SelectMany(x => x.Machines)
                .FirstOrDefault(x => x.Uid == ver.WorkOrderMachineId);
            if (machine is null)
                return $"Selected machine for Work Order {order.WorkOrderNo} no longer exists.";
            if (machine.ScheduleRevision != ver.ExpectedMachineScheduleRevision
                || !machine.RowVersion.SequenceEqual(ver.ExpectedMachineRowVersion))
                return $"Machine schedule for {order.WorkOrderNo} changed since the board was loaded. Refresh and try again.";
        }
        return null;
    }

    private sealed class DraftExecutionOutcome
    {
        public List<string> Blockers { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<MachineScheduleDraftConflict> Conflicts { get; set; } = [];
        public List<MachineScheduleAdvancedSegmentPreview> ResultingSegments { get; } = [];
        public List<MachineScheduleAdvancedWorkOrderPreview> AffectedWorkOrders { get; } = [];
        public HashSet<long> TouchedOrderIds { get; } = [];
        public HashSet<long> TouchedMachineIds { get; } = [];
        public Dictionary<long, (DateTime Start, DateTime Completion)> InitialOrderBounds { get; } = [];
        public Dictionary<long, bool> OrderUnscheduledFlags { get; } = [];
        public bool ScopeExpanded { get; set; }
    }

    private async Task<DraftExecutionOutcome> ExecuteDraftAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        MachineScheduleAdvancedPlacementEngine engine,
        MachineScheduleDraftOverlay overlay,
        MachineScheduleDraftRequest request,
        IReadOnlySet<long>? lockedWorkOrderIds,
        IReadOnlySet<string>? lockedMachineCodes,
        CancellationToken cancellationToken)
    {
        var outcome = new DraftExecutionOutcome();
        var touchedMachineCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in overlay.LoadedOrders)
            outcome.InitialOrderBounds[o.Uid] = (o.PlannedStartDateTime, o.PlannedCompletionDateTime);

        var versionByMachine = request.ExpectedVersions.ToDictionary(x => x.WorkOrderMachineId);

        foreach (var cmd in request.Commands)
        {
            MachineScheduleAdvancedPlan stepPlan;
            if (cmd.Kind == MachineScheduleDraftCommandKind.Move)
            {
                stepPlan = await overlay.BuildMoveStepAsync(cmd, cancellationToken);
            }
            else
            {
                var order = (await overlay.LoadOrdersAsync([cmd.WorkOrderId], cancellationToken)).FirstOrDefault();
                var machine = order?.RouteSteps.SelectMany(x => x.Operations).SelectMany(x => x.Machines)
                    .FirstOrDefault(x => x.Uid == cmd.WorkOrderMachineId);
                var expected = versionByMachine.TryGetValue(cmd.WorkOrderMachineId, out var ev)
                    ? ev with
                    {
                        ExpectedWorkOrderScheduleRevision = order?.ScheduleRevision ?? ev.ExpectedWorkOrderScheduleRevision,
                        ExpectedMachineScheduleRevision = machine?.ScheduleRevision ?? ev.ExpectedMachineScheduleRevision
                    }
                    : new MachineScheduleDraftExpectedVersion
                    {
                        WorkOrderId = cmd.WorkOrderId,
                        WorkOrderMachineId = cmd.WorkOrderMachineId,
                        ExpectedWorkOrderScheduleRevision = order?.ScheduleRevision ?? 0,
                        ExpectedWorkOrderRowVersion = order?.RowVersion ?? [],
                        ExpectedMachineScheduleRevision = machine?.ScheduleRevision ?? 0,
                        ExpectedMachineRowVersion = machine?.RowVersion ?? []
                    };
                var advReq = cmd.ToAdvancedRequest(expected);
                var builder = new MachineScheduleAdvancedPlanBuilder(
                    db, scope, _capacity, _uom, engine, tracking: false, overlay);
                stepPlan = await builder.BuildAsync(advReq, cancellationToken);
            }

            if (lockedWorkOrderIds is not null && lockedMachineCodes is not null
                && HasLockScopeExpanded(stepPlan, lockedWorkOrderIds, lockedMachineCodes))
            {
                outcome.ScopeExpanded = true;
                return outcome;
            }

            foreach (var warning in stepPlan.Warnings)
            {
                if (!outcome.Warnings.Contains(warning, StringComparer.Ordinal))
                    outcome.Warnings.Add(warning);
            }

            if (stepPlan.Blockers.Count > 0)
            {
                outcome.Blockers.AddRange(stepPlan.Blockers.Distinct(StringComparer.Ordinal));
                return outcome;
            }

            overlay.ApplyPlan(stepPlan);
            if (stepPlan.HasUnscheduledRemaining)
                outcome.OrderUnscheduledFlags[stepPlan.TargetOrder.Uid] = true;
            foreach (var uid in stepPlan.UnscheduledWorkOrderIds)
                outcome.OrderUnscheduledFlags[uid] = true;

            foreach (var o in stepPlan.Orders)
                outcome.TouchedOrderIds.Add(o.Order.Uid);
            foreach (var m in stepPlan.AffectedMachineIds)
                outcome.TouchedMachineIds.Add(m);
            foreach (var s in stepPlan.AllSegments)
                touchedMachineCodes.Add(MachineScheduleAdvancedPlacementEngine.Normalize(s.MachineCode));
            touchedMachineCodes.Add(MachineScheduleAdvancedPlacementEngine.Normalize(cmd.MachineCode));
        }

        // Enforce blast-radius guard across combined draft.
        if (overlay.DirtyOrderIds.Count > MachineScheduleSegmentPersistence.MaxAffectedWorkOrders)
            outcome.Blockers.Add($"Draft affects {overlay.DirtyOrderIds.Count} Work Orders (limit {MachineScheduleSegmentPersistence.MaxAffectedWorkOrders}).");
        var totalSegments = overlay.PlannedByMachine.Values.Sum(x => x.Count);
        if (totalSegments > MachineScheduleSegmentPersistence.MaxAffectedSegments)
            outcome.Blockers.Add($"Draft produces {totalSegments} schedule segments (limit {MachineScheduleSegmentPersistence.MaxAffectedSegments}).");

        // Final strict zero-overlap verification across all touched machines (including off-filter and other-branch capacity).
        var explicitWorkOrderIds = request.Commands.Select(x => x.WorkOrderId).ToHashSet();
        var conflicts = await overlay.DetectFinalOverlapsAsync(touchedMachineCodes, explicitWorkOrderIds, cancellationToken);
        outcome.Conflicts = conflicts;
        if (conflicts.Count > 0)
        {
            foreach (var conflict in conflicts)
            {
                if (!outcome.Blockers.Contains(conflict.Message, StringComparer.Ordinal))
                    outcome.Blockers.Add(conflict.Message);
            }
        }

        // Build clean summary DTOs.
        foreach (var machineId in overlay.DirtyMachineIds.OrderBy(x => x))
        {
            if (!overlay.PlannedByMachine.TryGetValue(machineId, out var segs))
                continue;
            var seq = 1;
            foreach (var seg in segs.OrderBy(x => x.Start))
            {
                outcome.ResultingSegments.Add(new MachineScheduleAdvancedSegmentPreview
                {
                    WorkOrderId = seg.WorkOrderId,
                    WorkOrderNo = seg.WorkOrderNo,
                    OperationId = seg.OperationId,
                    OperationCode = seg.OperationCode,
                    WorkOrderMachineId = seg.WorkOrderMachineId,
                    MachineCode = seg.MachineCode,
                    Sequence = seq++,
                    Kind = seg.Kind,
                    Status = seg.Status,
                    PlannedOutputQty = seg.Qty,
                    CycleCount = seg.CycleCount,
                    CycleSlots = seg.CycleSlots,
                    RunMinutes = seg.RunMinutes,
                    SetupSeconds = seg.SetupSeconds,
                    ConversionSeconds = seg.ConversionSeconds,
                    QueueSeconds = seg.QueueSeconds,
                    Start = seg.Start,
                    Completion = seg.Completion,
                    Reason = seg.Reason,
                    ProductiveIntervals = seg.Fragments
                        .Select(f => new MachineScheduleAdvancedInterval { Start = f.Start, End = f.End })
                        .ToList()
                });
            }
        }

        var loadedAfter = overlay.LoadedOrders.ToDictionary(x => x.Uid);
        foreach (var orderId in overlay.DirtyOrderIds.OrderBy(x => x))
        {
            if (!loadedAfter.TryGetValue(orderId, out var order))
                continue;
            outcome.TouchedOrderIds.Add(orderId);
            var before = outcome.InitialOrderBounds.TryGetValue(orderId, out var initBounds)
                ? initBounds
                : (order.PlannedStartDateTime, order.PlannedCompletionDateTime);
            var unscheduled = outcome.OrderUnscheduledFlags.TryGetValue(orderId, out var u) && u;
            outcome.AffectedWorkOrders.Add(new MachineScheduleAdvancedWorkOrderPreview
            {
                WorkOrderId = order.Uid,
                WorkOrderNo = order.WorkOrderNo,
                StartBefore = before.Item1,
                CompletionBefore = before.Item2,
                StartAfter = unscheduled ? null : order.PlannedStartDateTime,
                CompletionAfter = unscheduled ? null : order.PlannedCompletionDateTime,
                HasUnscheduledRemaining = unscheduled
            });
        }

        return outcome;
    }

    private async Task<string> ComputeDraftTokenAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        MachineScheduleDraftRequest request,
        IReadOnlyCollection<long> touchedOrderIds,
        IReadOnlyCollection<long> touchedMachineIds,
        CancellationToken cancellationToken)
    {
        var orderIds = touchedOrderIds.Concat(request.Commands.Select(x => x.WorkOrderId)).Distinct().OrderBy(x => x).ToList();
        var orders = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => orderIds.Contains(x.Uid))
            .OrderBy(x => x.Uid)
            .Select(x => new
            {
                x.Uid,
                x.ScheduleRevision,
                RowVersion = Convert.ToBase64String(x.RowVersion),
                x.PlannedStartDateTime,
                x.PlannedCompletionDateTime
            })
            .ToListAsync(cancellationToken);

        var machineIds = touchedMachineIds.Concat(request.Commands.Select(x => x.WorkOrderMachineId)).Distinct().OrderBy(x => x).ToList();
        var machines = await db.ProductionWorkOrderMachines.AsNoTracking()
            .Where(x => machineIds.Contains(x.Uid))
            .OrderBy(x => x.Uid)
            .Select(x => new
            {
                x.Uid,
                x.ScheduleRevision,
                RowVersion = Convert.ToBase64String(x.RowVersion)
            })
            .ToListAsync(cancellationToken);

        var sets = machineIds.Count == 0
            ? []
            : await db.ProductionMachineScheduleSegmentSets.AsNoTracking()
                .Where(x => machineIds.Contains(x.WorkOrderMachineId) && x.IsCurrent)
                .OrderBy(x => x.Uid)
                .Select(x => new { x.Uid, x.WorkOrderMachineId, x.Mode, x.PlanningState, x.MachineScheduleRevision, RowVersion = Convert.ToBase64String(x.RowVersion) })
                .ToListAsync(cancellationToken);

        var segments = machineIds.Count == 0
            ? []
            : await db.ProductionMachineScheduleSegments.AsNoTracking()
                .Where(x => machineIds.Contains(x.WorkOrderMachineId) && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
                .OrderBy(x => x.Uid)
                .Select(x => new { x.Uid, x.WorkOrderMachineId, x.SegmentStatus, x.PlannedStartDateTime, x.PlannedCompletionDateTime, RowVersion = Convert.ToBase64String(x.RowVersion) })
                .ToListAsync(cancellationToken);

        var payload = new
        {
            Commands = request.Commands.Select(c => new
            {
                c.Kind,
                c.WorkOrderId,
                c.OperationId,
                c.WorkOrderMachineId,
                MachineCode = MachineScheduleAdvancedPlacementEngine.Normalize(c.MachineCode),
                c.RequestedStart,
                c.PartialQuantity,
                c.IncludeRestartSetup,
                c.HoldType,
                c.ResumeNotBefore,
                c.HoldId,
                c.MaintenanceId,
                c.DowntimeStart,
                c.DowntimeEnd,
                Reason = c.Reason.Trim()
            }).ToList(),
            Orders = orders,
            Machines = machines,
            SegmentSets = sets,
            Segments = segments,
            Scope = $"{scope.CompanyCode}:{scope.BranchCode}"
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    private async Task PersistDraftAsync(
        AppDbContext db,
        MachineScheduleDraftOverlay overlay,
        DraftExecutionOutcome exec,
        InventoryTenantScope scope,
        MachineScheduleDraftRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var user = MachineScheduleSegmentPersistence.TruncateUser(scope.UserId);
        var dirtyMachineIds = overlay.DirtyMachineIds.OrderBy(x => x).ToList();

        var priorSets = dirtyMachineIds.Count == 0
            ? []
            : await db.ProductionMachineScheduleSegmentSets
                .Where(x => dirtyMachineIds.Contains(x.WorkOrderMachineId) && x.IsCurrent)
                .ToListAsync(cancellationToken);
        var priorSetsByMachine = priorSets.ToDictionary(x => x.WorkOrderMachineId);
        MachineScheduleSegmentPersistence.SupersedeSets(priorSets, scope.UserId, now);

        var priorSegments = dirtyMachineIds.Count == 0
            ? []
            : await db.ProductionMachineScheduleSegments
                .Where(x => dirtyMachineIds.Contains(x.WorkOrderMachineId)
                    && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
                .ToListAsync(cancellationToken);
        MachineScheduleSegmentPersistence.Supersede(priorSegments, scope.UserId, now, "Superseded by Draft Reschedule commit");
        await db.SaveChangesAsync(cancellationToken);

        var entities = new Dictionary<PlannedSegment, ProductionMachineScheduleSegment>();
        foreach (var machineId in dirtyMachineIds)
        {
            if (!overlay.PlannedByMachine.TryGetValue(machineId, out var list))
                continue;
            var ordered = list.OrderBy(x => x.Start).ThenBy(x => x.Kind, StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var planned = ordered[i];
                var entity = new ProductionMachineScheduleSegment
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode!,
                    WorkOrderId = planned.WorkOrderId,
                    WorkOrderOperationId = planned.OperationId,
                    WorkOrderMachineId = planned.WorkOrderMachineId,
                    MachineCode = planned.MachineCode,
                    SegmentSequence = i + 1,
                    SegmentKind = planned.Kind,
                    SegmentStatus = planned.Status,
                    SourceSegmentId = planned.SourceSegmentId,
                    PlannedOutputQty = planned.Qty,
                    OutputUom = planned.Uom,
                    PlannedCycleCount = planned.CycleCount,
                    PlannedCycleSlots = planned.CycleSlots,
                    ScheduledRunMinutes = planned.RunMinutes,
                    ScheduledSetupSeconds = planned.SetupSeconds,
                    ScheduledConversionSeconds = planned.ConversionSeconds,
                    ScheduledQueueSeconds = planned.QueueSeconds,
                    PlannedStartDateTime = planned.Start,
                    PlannedCompletionDateTime = planned.Completion,
                    CalendarHorizonStart = planned.HorizonStart,
                    CalendarHorizonEnd = planned.HorizonEnd,
                    SegmentScheduleSourceHash = planned.SourceHash,
                    Reason = MachineScheduleSegmentPersistence.TrimReason(planned.Reason),
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(db)
                };
                db.ProductionMachineScheduleSegments.Add(entity);
                entities[planned] = entity;
            }
        }
        await db.SaveChangesAsync(cancellationToken);

        var dirtyOrderIds = overlay.DirtyOrderIds.OrderBy(x => x).ToList();
        var trackedOrders = await LoadOrdersByIdsAsync(db, scope, dirtyOrderIds, tracking: true, cancellationToken);
        var overlayOrderById = overlay.LoadedOrders.ToDictionary(x => x.Uid);

        var machineLookup = trackedOrders
            .SelectMany(o => o.RouteSteps.SelectMany(s => s.Operations.SelectMany(op => op.Machines.Select(m => (Order: o, Op: op, Machine: m)))))
            .ToDictionary(x => x.Machine.Uid);

        var setByMachine = new Dictionary<long, ProductionMachineScheduleSegmentSet>();
        var allocationLinks = new List<(ProductionMachineScheduleAllocation Allocation, ProductionMachineScheduleSegment Segment)>();
        var changedMachineIds = new HashSet<long>();

        foreach (var machineId in dirtyMachineIds)
        {
            if (!machineLookup.TryGetValue(machineId, out var pair))
                continue;
            var machine = pair.Machine;
            var operation = pair.Op;
            var order = pair.Order;
            var machinePlanned = overlay.PlannedByMachine.TryGetValue(machineId, out var pList)
                ? pList.OrderBy(x => x.Start).ToList()
                : [];
            var segmentEntities = machinePlanned.Select(x => entities[x]).ToList();
            var oldMachineStart = machine.PlannedStartDateTime;
            machine.ScheduleRevision++;
            changedMachineIds.Add(machineId);

            var machineUnscheduled = overlay.UnscheduledByMachine.TryGetValue(machineId, out var unsch) && unsch;
            if (segmentEntities.Count > 0 && !machineUnscheduled)
            {
                var start = segmentEntities.Min(x => x.PlannedStartDateTime);
                var completion = segmentEntities.Max(x => x.PlannedCompletionDateTime);
                var executed = operation.GoodQty != 0m || operation.ProcessedQty != 0m || operation.ScrapQty != 0m
                    || operation.RejectQty != 0m || operation.HoldQty != 0m || operation.InputQty != 0m
                    || operation.TransferredQty != 0m;
                if (executed && oldMachineStart is DateTime oldStart && oldStart < start)
                    start = oldStart;

                var horizonStart = segmentEntities.Min(x => x.CalendarHorizonStart) ?? start.Date.AddDays(-1);
                var horizonEnd = segmentEntities.Max(x => x.CalendarHorizonEnd) ?? completion.Date.AddDays(1);
                var sourceHash = await ComputeUnionHashAsync(scope.CompanyCode, machine.MachineCode, horizonStart, horizonEnd, cancellationToken);
                machine.PlannedStartDateTime = start;
                machine.PlannedCompletionDateTime = completion;
                machine.CalendarHorizonStart = horizonStart;
                machine.CalendarHorizonEnd = horizonEnd;
                machine.ScheduleSourceHash = sourceHash;
                operation.PlannedStartDateTime = start;
                operation.PlannedCompletionDateTime = completion;
                operation.ScheduleSourceHash = sourceHash;
                operation.CalendarHorizonStart = horizonStart;
                operation.CalendarHorizonEnd = horizonEnd;
                operation.CalendarSourceType = ProductionCalendarSourceTypes.Machine;
                operation.CalendarSourceId = machine.CalendarSourceId;
                operation.CalendarSourceLastModified = machine.CalendarSourceLastModified;
                operation.ModifiedDate = now;
                operation.ModifiedBy = user;
            }

            foreach (var seg in segmentEntities)
            {
                seg.MachineScheduleRevision = machine.ScheduleRevision;
                seg.ModifiedDate = now;
                seg.ModifiedBy = user;
            }

            machine.ModifiedDate = now;
            machine.ModifiedBy = user;

            var mode = overlay.ModeByMachine.TryGetValue(machineId, out var mMode)
                ? mMode
                : priorSetsByMachine.TryGetValue(machineId, out var prevSet)
                    ? prevSet.Mode
                    : ProductionMachineScheduleSegmentSetModes.FullPlan;
            var planningState = overlay.PlanningStateByMachine.TryGetValue(machineId, out var pState)
                ? pState
                : ProductionMachineSchedulePlanningStates.Scheduled;
            var reason = overlay.ReasonByMachine.TryGetValue(machineId, out var r) ? r : "Draft commit";

            var set = MachineScheduleSegmentPersistence.BuildCurrentSet(
                order, operation, machine, mode, planningState,
                segmentEntities.Sum(x => x.PlannedOutputQty), reason, scope.UserId, now, db);
            db.ProductionMachineScheduleSegmentSets.Add(set);
            setByMachine[machineId] = set;

            var existingAllocations = await db.ProductionMachineScheduleAllocations
                .Where(x => x.WorkOrderMachineId == machineId)
                .ToListAsync(cancellationToken);
            await MachineScheduleSegmentPersistence.RemoveAllocationLinksAsync(db, existingAllocations, cancellationToken);
            db.ProductionMachineScheduleAllocations.RemoveRange(existingAllocations);

            var fragNo = 1;
            foreach (var planned in machinePlanned)
            {
                var segment = entities[planned];
                foreach (var interval in planned.Fragments)
                {
                    var alloc = new ProductionMachineScheduleAllocation
                    {
                        CompanyCode = segment.CompanyCode,
                        BranchCode = segment.BranchCode,
                        MachineCode = segment.MachineCode,
                        WorkOrderId = segment.WorkOrderId,
                        WorkOrderOperationId = segment.WorkOrderOperationId,
                        WorkOrderMachineId = segment.WorkOrderMachineId,
                        MachineScheduleSegmentId = segment.Uid,
                        ScheduleRevision = machine.ScheduleRevision,
                        FragmentNo = fragNo++,
                        StartDateTime = interval.Start,
                        EndDateTime = interval.End,
                        ScheduleSourceHash = machine.ScheduleSourceHash,
                        CreatedDate = now,
                        CreatedBy = user,
                        RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(db)
                    };
                    db.ProductionMachineScheduleAllocations.Add(alloc);
                    allocationLinks.Add((alloc, segment));
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var pair in entities)
            pair.Value.SegmentSetId = setByMachine[pair.Key.WorkOrderMachineId].Uid;
        foreach (var (allocation, segment) in allocationLinks)
            MachineScheduleSegmentPersistence.AddAllocationLink(db, allocation, segment, scope.UserId, now);

        foreach (var (hold, _) in overlay.AddedHolds)
        {
            var newHold = new ProductionWorkOrderScheduleHold
            {
                CompanyCode = hold.CompanyCode,
                BranchCode = hold.BranchCode,
                WorkOrderId = hold.WorkOrderId,
                WorkOrderOperationId = hold.WorkOrderOperationId,
                WorkOrderMachineId = hold.WorkOrderMachineId,
                HoldType = hold.HoldType,
                Status = ProductionScheduleHoldStatuses.Active,
                HoldFrom = hold.HoldFrom,
                ResumeNotBefore = hold.ResumeNotBefore,
                Reason = hold.Reason,
                CreatedDate = now,
                CreatedBy = user,
                RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(db)
            };
            db.ProductionWorkOrderScheduleHolds.Add(newHold);
        }

        if (overlay.ResolvedDbHoldIds.Count > 0)
        {
            var toResolve = await db.ProductionWorkOrderScheduleHolds
                .Where(x => overlay.ResolvedDbHoldIds.Contains(x.Uid))
                .ToListAsync(cancellationToken);
            foreach (var h in toResolve)
            {
                h.Status = ProductionScheduleHoldStatuses.Released;
                h.ReleasedDate = now;
                h.ReleasedBy = user;
                h.ResolutionReason = MachineScheduleSegmentPersistence.TrimReason(
                    overlay.HoldResolutionReasons.TryGetValue(h.Uid, out var rr) ? rr : "Draft commit");
                h.RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(db);
            }
        }

        foreach (var order in trackedOrders)
        {
            if (overlayOrderById.TryGetValue(order.Uid, out var simulatedOrder))
            {
                var simOps = simulatedOrder.Operations.ToDictionary(x => x.Uid);
                foreach (var op in order.Operations)
                {
                    if (simOps.TryGetValue(op.Uid, out var simOp))
                    {
                        op.PlannedStartDateTime = simOp.PlannedStartDateTime;
                        op.PlannedCompletionDateTime = simOp.PlannedCompletionDateTime;
                    }
                }
            }

            foreach (var step in order.RouteSteps)
            {
                var starts = step.Operations.Where(x => x.PlannedStartDateTime is not null).Select(x => x.PlannedStartDateTime!.Value).ToList();
                var ends = step.Operations.Where(x => x.PlannedCompletionDateTime is not null).Select(x => x.PlannedCompletionDateTime!.Value).ToList();
                if (starts.Count > 0) step.PlannedStartDateTime = starts.Min();
                if (ends.Count > 0) step.PlannedCompletionDateTime = ends.Max();
                step.ModifiedDate = now;
                step.ModifiedBy = user;
            }
            var oStarts = order.RouteSteps.Where(x => x.PlannedStartDateTime is not null).Select(x => x.PlannedStartDateTime!.Value).ToList();
            var oEnds = order.RouteSteps.Where(x => x.PlannedCompletionDateTime is not null).Select(x => x.PlannedCompletionDateTime!.Value).ToList();
            if (oStarts.Count > 0) order.PlannedStartDateTime = oStarts.Min();
            if (oEnds.Count > 0) order.PlannedCompletionDateTime = oEnds.Max();
            order.ScheduleRevision++;
            order.ModifiedDate = now;
            order.ModifiedBy = user;

            if (!db.Database.IsSqlServer())
            {
                order.RowVersion = Guid.NewGuid().ToByteArray();
                foreach (var step in order.RouteSteps)
                {
                    step.RowVersion = Guid.NewGuid().ToByteArray();
                    foreach (var op in step.Operations)
                    {
                        op.RowVersion = Guid.NewGuid().ToByteArray();
                        foreach (var m in op.Machines.Where(x => changedMachineIds.Contains(x.Uid)))
                            m.RowVersion = Guid.NewGuid().ToByteArray();
                    }
                }
            }

            db.ProductionAuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrderId = order.Uid,
                EventType = ProductionAuditEventTypes.ScheduleChanged,
                FromStatus = order.Status,
                ToStatus = order.Status,
                SnapshotRevision = order.SnapshotRevision,
                Reason = $"Draft Reschedule commit ({request.Commands.Count} commands)",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    CommandCount = request.Commands.Count,
                    request.PreviewToken,
                    Commands = request.Commands.Select(c => new { c.Kind, c.WorkOrderId, c.MachineCode, c.RequestedStart, c.Reason }).ToList()
                }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
        }
    }
}
