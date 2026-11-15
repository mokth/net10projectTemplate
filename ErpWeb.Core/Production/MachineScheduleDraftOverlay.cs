using ErpWeb.Core.Inventory;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal interface IMachineScheduleDraftOverlay
{
    Task<List<ProductionWorkOrder>> LoadOrdersAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
    ProductionMachineScheduleSegmentSet? GetCurrentSet(long machineId);
    bool HasCurrentTargetSegment(long machineId);
    List<ProductionMachineScheduleSegment> GetBranchSegments();
    Dictionary<long, List<ProductionMachineScheduleAllocation>> GetAllocations(IReadOnlyCollection<long> machineIds);
    ProductionWorkOrderScheduleHold? GetActiveHold(long workOrderId, long machineId, long? holdId);
    void ExcludeDirtyMachines(HashSet<long> excludedMachineIds);
    void AdjustCapacity(
        IReadOnlyCollection<string> machineCodes,
        IReadOnlySet<long> originalExcludedMachineIds,
        Dictionary<string, List<TimeInterval>> occupiedByMachine);
}

internal sealed class MachineScheduleDraftOverlay : IMachineScheduleDraftOverlay
{
    private readonly AppDbContext _db;
    private readonly InventoryTenantScope _scope;
    private readonly IProductionMachineCapacityService _capacity;
    private readonly IUomConversionService _uom;
    private readonly MachineScheduleAdvancedPlacementEngine _engine;

    private readonly Dictionary<long, ProductionWorkOrder> _ordersById = [];
    private readonly Dictionary<long, ProductionMachineScheduleSegmentSet> _currentSetsByMachine = [];
    private readonly List<ProductionMachineScheduleSegment> _branchSegments = [];
    private readonly Dictionary<long, List<ProductionMachineScheduleAllocation>> _allocationsByMachine = [];
    private readonly List<ProductionWorkOrderScheduleHold> _activeHolds = [];

    private readonly Dictionary<long, List<PlannedSegment>> _plannedByMachine = [];
    private readonly Dictionary<long, string> _planningStateByMachine = [];
    private readonly Dictionary<long, string> _modeByMachine = [];
    private readonly Dictionary<long, string> _reasonByMachine = [];
    private readonly Dictionary<long, bool> _unscheduledByMachine = [];

    private long _nextTempId = -1;

    private MachineScheduleDraftOverlay(
        AppDbContext db,
        InventoryTenantScope scope,
        IProductionMachineCapacityService capacity,
        IUomConversionService uom,
        MachineScheduleAdvancedPlacementEngine engine)
    {
        _db = db;
        _scope = scope;
        _capacity = capacity;
        _uom = uom;
        _engine = engine;
    }

    public HashSet<long> DirtyMachineIds { get; } = [];
    public HashSet<long> DirtyOrderIds { get; } = [];
    public HashSet<long> SupersededDbSegmentIds { get; } = [];
    public HashSet<long> ResolvedDbHoldIds { get; } = [];
    public List<(ProductionWorkOrderScheduleHold Hold, string Reason)> AddedHolds { get; } = [];
    public Dictionary<long, string> HoldResolutionReasons { get; } = [];
    public List<ProductionWorkOrder> LoadedOrders => _ordersById.Values.ToList();
    public IReadOnlyDictionary<long, List<PlannedSegment>> PlannedByMachine => _plannedByMachine;
    public IReadOnlyDictionary<long, string> PlanningStateByMachine => _planningStateByMachine;
    public IReadOnlyDictionary<long, string> ModeByMachine => _modeByMachine;
    public IReadOnlyDictionary<long, string> ReasonByMachine => _reasonByMachine;
    public IReadOnlyDictionary<long, bool> UnscheduledByMachine => _unscheduledByMachine;

    public static async Task<MachineScheduleDraftOverlay> InitializeAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        IProductionMachineCapacityService capacity,
        IUomConversionService uom,
        MachineScheduleAdvancedPlacementEngine engine,
        IReadOnlyCollection<long> seedOrderIds,
        CancellationToken cancellationToken)
    {
        var overlay = new MachineScheduleDraftOverlay(db, scope, capacity, uom, engine);
        await overlay.LoadInitialStateAsync(seedOrderIds, cancellationToken);
        return overlay;
    }

    private async Task LoadInitialStateAsync(IReadOnlyCollection<long> seedOrderIds, CancellationToken cancellationToken)
    {
        var sets = await _db.ProductionMachineScheduleSegmentSets.AsNoTracking()
            .Where(x => x.CompanyCode == _scope.CompanyCode
                && x.BranchCode == _scope.BranchCode
                && x.IsCurrent)
            .ToListAsync(cancellationToken);
        foreach (var set in sets)
            _currentSetsByMachine[set.WorkOrderMachineId] = set;

        var currentSetIds = sets.Select(x => x.Uid).ToHashSet();
        var machinesWithSets = sets.Select(x => x.WorkOrderMachineId).ToHashSet();

        var allSegments = await _db.ProductionMachineScheduleSegments.AsNoTracking()
            .Where(x => x.CompanyCode == _scope.CompanyCode
                && x.BranchCode == _scope.BranchCode
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .OrderBy(x => x.MachineCode)
            .ThenBy(x => x.WorkOrderMachineId)
            .ThenBy(x => x.SegmentSequence)
            .ToListAsync(cancellationToken);
        _branchSegments.AddRange(allSegments.Where(x => x.SegmentSetId is long setId
            ? currentSetIds.Contains(setId)
            : !machinesWithSets.Contains(x.WorkOrderMachineId)));

        var holds = await _db.ProductionWorkOrderScheduleHolds.AsNoTracking()
            .Where(x => x.CompanyCode == _scope.CompanyCode
                && x.BranchCode == _scope.BranchCode
                && x.Status == ProductionScheduleHoldStatuses.Active)
            .ToListAsync(cancellationToken);
        _activeHolds.AddRange(holds);

        var machineIds = _branchSegments.Select(x => x.WorkOrderMachineId)
            .Concat(machinesWithSets)
            .Distinct()
            .ToList();
        await LoadOrdersAsync(seedOrderIds.Concat(_branchSegments.Select(x => x.WorkOrderId)).ToList(), cancellationToken);

        foreach (var o in _ordersById.Values)
        {
            foreach (var op in o.Operations)
            {
                foreach (var m in op.Machines.Where(m => m.IsSelected))
                {
                    if (!machineIds.Contains(m.Uid))
                        machineIds.Add(m.Uid);
                }
            }
        }

        if (machineIds.Count > 0)
        {
            var allocations = new List<ProductionMachineScheduleAllocation>();
            foreach (var batch in machineIds.Chunk(800))
            {
                allocations.AddRange(await _db.ProductionMachineScheduleAllocations.AsNoTracking()
                    .Where(x => batch.Contains(x.WorkOrderMachineId))
                    .OrderBy(x => x.WorkOrderMachineId)
                    .ThenBy(x => x.ScheduleRevision)
                    .ThenBy(x => x.FragmentNo)
                    .ToListAsync(cancellationToken));
            }

            var currentSegmentIds = _branchSegments.Where(x => x.SegmentSetId is not null)
                .Select(x => x.Uid)
                .ToHashSet();
            var linkedAllocationIds = new HashSet<long>();
            foreach (var batch in currentSegmentIds.Chunk(800))
            {
                var ids = await _db.ProductionMachineScheduleAllocationSegments.AsNoTracking()
                    .Where(x => batch.Contains(x.SegmentId))
                    .Select(x => x.AllocationId)
                    .ToListAsync(cancellationToken);
                linkedAllocationIds.UnionWith(ids);
            }

            var legacySegments = _branchSegments.Where(x => x.SegmentSetId is null)
                .GroupBy(x => x.WorkOrderMachineId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var filtered = allocations.Where(row => linkedAllocationIds.Contains(row.Uid)
                || legacySegments.TryGetValue(row.WorkOrderMachineId, out var legacy)
                    && legacy.Any(seg => seg.MachineScheduleRevision == row.ScheduleRevision
                        && (row.MachineScheduleSegmentId is null || row.MachineScheduleSegmentId == seg.Uid)))
                .ToList();
            foreach (var group in filtered.GroupBy(x => x.WorkOrderMachineId))
                _allocationsByMachine[group.Key] = group.OrderBy(x => x.ScheduleRevision).ThenBy(x => x.FragmentNo).ToList();
        }
    }

    public async Task<List<ProductionWorkOrder>> LoadOrdersAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        var distinct = ids.Where(x => x > 0).Distinct().ToList();
        var missing = distinct.Where(id => !_ordersById.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            foreach (var batch in missing.Chunk(200))
            {
                var rows = await _db.ProductionWorkOrders
                    .AsNoTracking()
                    .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Machines)
                    .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Materials).ThenInclude(x => x.ProducingRouteStep)
                    .AsSplitQuery()
                    .Where(x => batch.Contains(x.Uid)
                        && x.CompanyCode == _scope.CompanyCode
                        && x.BranchCode == _scope.BranchCode)
                    .ToListAsync(cancellationToken);
                foreach (var row in rows)
                    _ordersById[row.Uid] = row;
            }
        }

        return distinct.Where(_ordersById.ContainsKey).Select(id => _ordersById[id]).ToList();
    }

    public ProductionMachineScheduleSegmentSet? GetCurrentSet(long machineId) =>
        _currentSetsByMachine.TryGetValue(machineId, out var set) ? set : null;

    public bool HasCurrentTargetSegment(long machineId)
    {
        _currentSetsByMachine.TryGetValue(machineId, out var currentSet);
        if (_ordersById.Values
            .SelectMany(o => o.Operations)
            .SelectMany(op => op.Machines)
            .FirstOrDefault(m => m.Uid == machineId) is not { } machine)
            return false;

        return _branchSegments.Any(x => x.WorkOrderMachineId == machineId
            && (currentSet == null
                ? x.MachineScheduleRevision == machine.ScheduleRevision
                : x.SegmentSetId == currentSet.Uid)
            && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded);
    }

    public List<ProductionMachineScheduleSegment> GetBranchSegments() =>
        _branchSegments
            .Where(x => x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .OrderBy(x => x.MachineCode)
            .ThenBy(x => x.WorkOrderMachineId)
            .ThenBy(x => x.SegmentSequence)
            .ToList();

    public Dictionary<long, List<ProductionMachineScheduleAllocation>> GetAllocations(IReadOnlyCollection<long> machineIds)
    {
        var result = new Dictionary<long, List<ProductionMachineScheduleAllocation>>();
        foreach (var id in machineIds)
        {
            if (_allocationsByMachine.TryGetValue(id, out var list))
                result[id] = list.ToList();
        }
        return result;
    }

    public ProductionWorkOrderScheduleHold? GetActiveHold(long workOrderId, long machineId, long? holdId) =>
        _activeHolds
            .Where(x => x.WorkOrderId == workOrderId
                && x.WorkOrderMachineId == machineId
                && x.Status == ProductionScheduleHoldStatuses.Active
                && (holdId == null || x.Uid == holdId))
            .OrderByDescending(x => x.Uid)
            .FirstOrDefault();

    public void ExcludeDirtyMachines(HashSet<long> excludedMachineIds)
    {
        foreach (var id in DirtyMachineIds)
            excludedMachineIds.Add(id);
    }

    public void AdjustCapacity(
        IReadOnlyCollection<string> machineCodes,
        IReadOnlySet<long> originalExcludedMachineIds,
        Dictionary<string, List<TimeInterval>> occupiedByMachine)
    {
        var targetCodes = machineCodes
            .Select(MachineScheduleAdvancedPlacementEngine.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var machineId in DirtyMachineIds)
        {
            if (originalExcludedMachineIds.Contains(machineId))
                continue;
            if (!_allocationsByMachine.TryGetValue(machineId, out var allocs) || allocs.Count == 0)
                continue;
            foreach (var alloc in allocs)
            {
                var key = MachineScheduleAdvancedPlacementEngine.Normalize(alloc.MachineCode);
                if (!targetCodes.Contains(key))
                    continue;
                if (!occupiedByMachine.TryGetValue(key, out var list))
                {
                    list = [];
                    occupiedByMachine[key] = list;
                }
                list.Add(new TimeInterval(alloc.StartDateTime, alloc.EndDateTime));
            }
        }
    }

    public void ApplyPlan(MachineScheduleAdvancedPlan plan)
    {
        foreach (var orderPlan in plan.Orders)
            _ordersById[orderPlan.Order.Uid] = orderPlan.Order;

        foreach (var seg in plan.SegmentsToSupersede)
        {
            if (seg.Uid > 0)
                SupersededDbSegmentIds.Add(seg.Uid);
            _branchSegments.RemoveAll(x => x.Uid == seg.Uid);
            _allocationsByMachine.Remove(seg.WorkOrderMachineId);
        }

        var nextRevisionByMachine = new Dictionary<long, int>();
        var machineLookup = _ordersById.Values
            .SelectMany(o => o.RouteSteps.SelectMany(s => s.Operations.SelectMany(op => op.Machines.Select(m => (o, s, op, m)))))
            .ToDictionary(x => x.m.Uid);

        foreach (var machineId in plan.AffectedMachineIds)
        {
            DirtyMachineIds.Add(machineId);
            if (!machineLookup.TryGetValue(machineId, out var info))
                continue;

            var machine = info.m;
            var operation = info.op;
            var order = info.o;
            DirtyOrderIds.Add(order.Uid);

            var machinePlanned = plan.SegmentsByMachine.TryGetValue(machineId, out var plannedList)
                ? plannedList.OrderBy(x => x.Start).ToList()
                : [];
            _plannedByMachine[machineId] = machinePlanned;

            var machineUnscheduled = plan.UnscheduledMachineIds.Contains(machineId)
                || (plan.HasUnscheduledRemaining && machineId == plan.TargetMachine.Uid);
            _unscheduledByMachine[machineId] = machineUnscheduled;

            var mode = plan.Action == MachineScheduleAdvancedAction.ReplanRemaining
                ? ProductionMachineScheduleSegmentSetModes.RemainingOnly
                : _currentSetsByMachine.TryGetValue(machineId, out var prevSet)
                    ? prevSet.Mode
                    : ProductionMachineScheduleSegmentSetModes.FullPlan;
            _modeByMachine[machineId] = mode;

            var planningState = machineUnscheduled
                ? plan.Action == MachineScheduleAdvancedAction.Hold
                    ? ProductionMachineSchedulePlanningStates.HeldUnscheduled
                    : plan.Action == MachineScheduleAdvancedAction.MaintenanceReplan
                        ? ProductionMachineSchedulePlanningStates.MaintenanceUnscheduled
                        : ProductionMachineSchedulePlanningStates.ReplanRequired
                : machinePlanned.Count == 0 && operation.RemainingQty <= 0m
                    ? ProductionMachineSchedulePlanningStates.Completed
                    : ProductionMachineSchedulePlanningStates.Scheduled;
            _planningStateByMachine[machineId] = planningState;
            _reasonByMachine[machineId] = plan.Request.Reason;

            machine.ScheduleRevision++;
            nextRevisionByMachine[machineId] = machine.ScheduleRevision;

            if (machinePlanned.Count > 0 && !machineUnscheduled)
            {
                var start = machinePlanned.Min(x => x.Start);
                var completion = machinePlanned.Max(x => x.Completion);
                if (HasExecution(operation) && machine.PlannedStartDateTime is DateTime oldStart && oldStart < start)
                    start = oldStart;
                machine.PlannedStartDateTime = start;
                machine.PlannedCompletionDateTime = completion;
                machine.CalendarHorizonStart = machinePlanned.Min(x => x.HorizonStart) ?? start.Date.AddDays(-1);
                machine.CalendarHorizonEnd = machinePlanned.Max(x => x.HorizonEnd) ?? completion.Date.AddDays(1);
                machine.ScheduleSourceHash = machinePlanned[0].SourceHash;
                operation.PlannedStartDateTime = start;
                operation.PlannedCompletionDateTime = completion;
                operation.ScheduleSourceHash = machine.ScheduleSourceHash;
            }

            var newSetId = _nextTempId--;
            _currentSetsByMachine[machineId] = new ProductionMachineScheduleSegmentSet
            {
                Uid = newSetId,
                CompanyCode = order.CompanyCode,
                BranchCode = order.BranchCode,
                WorkOrderId = order.Uid,
                WorkOrderOperationId = operation.Uid,
                WorkOrderMachineId = machineId,
                MachineCode = machine.MachineCode,
                Mode = mode,
                PlanningState = planningState,
                IsCurrent = true,
                PlannedQtyBasis = machinePlanned.Sum(x => x.Qty),
                OutputUom = operation.PlannedOutputUom,
                CalendarHorizonStart = machine.CalendarHorizonStart,
                CalendarHorizonEnd = machine.CalendarHorizonEnd,
                ScheduleSourceHash = machine.ScheduleSourceHash,
                MachineScheduleRevision = machine.ScheduleRevision
            };

            _branchSegments.RemoveAll(x => x.WorkOrderMachineId == machineId);
            var allocs = new List<ProductionMachineScheduleAllocation>();
            var fragNo = 0;
            for (var i = 0; i < machinePlanned.Count; i++)
            {
                var p = machinePlanned[i];
                var segUid = _nextTempId--;
                _branchSegments.Add(new ProductionMachineScheduleSegment
                {
                    Uid = segUid,
                    CompanyCode = order.CompanyCode,
                    BranchCode = order.BranchCode,
                    WorkOrderId = order.Uid,
                    WorkOrderOperationId = operation.Uid,
                    WorkOrderMachineId = machineId,
                    MachineCode = machine.MachineCode,
                    SegmentSetId = newSetId,
                    SegmentSequence = i + 1,
                    SegmentKind = p.Kind,
                    SegmentStatus = ProductionMachineScheduleSegmentStatuses.Planned,
                    PlannedOutputQty = p.Qty,
                    OutputUom = p.Uom,
                    PlannedCycleCount = p.CycleCount,
                    PlannedCycleSlots = p.CycleSlots,
                    ScheduledRunMinutes = p.RunMinutes,
                    ScheduledSetupSeconds = p.SetupSeconds,
                    ScheduledConversionSeconds = p.ConversionSeconds,
                    ScheduledQueueSeconds = p.QueueSeconds,
                    PlannedStartDateTime = p.Start,
                    PlannedCompletionDateTime = p.Completion,
                    CalendarHorizonStart = p.HorizonStart,
                    CalendarHorizonEnd = p.HorizonEnd,
                    SegmentScheduleSourceHash = p.SourceHash,
                    MachineScheduleRevision = machine.ScheduleRevision,
                    Reason = p.Reason
                });
                foreach (var frag in p.Fragments)
                {
                    fragNo++;
                    allocs.Add(new ProductionMachineScheduleAllocation
                    {
                        Uid = _nextTempId--,
                        CompanyCode = order.CompanyCode,
                        BranchCode = order.BranchCode,
                        MachineCode = machine.MachineCode,
                        WorkOrderId = order.Uid,
                        WorkOrderOperationId = operation.Uid,
                        WorkOrderMachineId = machineId,
                        ScheduleRevision = machine.ScheduleRevision,
                        FragmentNo = fragNo,
                        StartDateTime = frag.Start,
                        EndDateTime = frag.End,
                        ScheduleSourceHash = p.SourceHash,
                        MachineScheduleSegmentId = segUid
                    });
                }
            }
            _allocationsByMachine[machineId] = allocs;
        }

        foreach (var orderPlan in plan.Orders)
        {
            var order = orderPlan.Order;
            DirtyOrderIds.Add(order.Uid);
            if (plan.Projections.TryGetValue(order.Uid, out var projection))
            {
                foreach (var opProj in projection.ByOperation.Values)
                {
                    var op = order.Operations.FirstOrDefault(x => x.Uid == opProj.OperationId);
                    if (op is not null)
                    {
                        op.PlannedStartDateTime = opProj.Start;
                        op.PlannedCompletionDateTime = opProj.Completion;
                    }
                }
            }

            foreach (var step in order.RouteSteps)
            {
                var starts = step.Operations.Where(x => x.PlannedStartDateTime is not null)
                    .Select(x => x.PlannedStartDateTime!.Value).ToList();
                var ends = step.Operations.Where(x => x.PlannedCompletionDateTime is not null)
                    .Select(x => x.PlannedCompletionDateTime!.Value).ToList();
                if (starts.Count > 0) step.PlannedStartDateTime = starts.Min();
                if (ends.Count > 0) step.PlannedCompletionDateTime = ends.Max();
            }

            var stepStarts = order.RouteSteps.Where(x => x.PlannedStartDateTime is not null)
                .Select(x => x.PlannedStartDateTime!.Value).ToList();
            var stepEnds = order.RouteSteps.Where(x => x.PlannedCompletionDateTime is not null)
                .Select(x => x.PlannedCompletionDateTime!.Value).ToList();
            if (stepStarts.Count > 0) order.PlannedStartDateTime = stepStarts.Min();
            if (stepEnds.Count > 0) order.PlannedCompletionDateTime = stepEnds.Max();
        }

        foreach (var hold in plan.HoldsToAdd)
        {
            if (hold.Uid == 0) hold.Uid = _nextTempId--;
            _activeHolds.Add(hold);
            AddedHolds.Add((hold, plan.Request.Reason));
        }

        if (plan.HoldToResolve is { } resolved)
        {
            if (resolved.Uid > 0)
            {
                ResolvedDbHoldIds.Add(resolved.Uid);
                HoldResolutionReasons[resolved.Uid] = plan.Request.Reason;
            }
            else
            {
                AddedHolds.RemoveAll(x => x.Hold.Uid == resolved.Uid);
            }
            _activeHolds.RemoveAll(x => x.Uid == resolved.Uid);
        }
    }

    public async Task<MachineScheduleAdvancedPlan> BuildMoveStepAsync(
        MachineScheduleDraftCommand cmd,
        CancellationToken cancellationToken)
    {
        var request = new MachineScheduleAdvancedRequest
        {
            Action = MachineScheduleAdvancedAction.InsertSplit,
            WorkOrderId = cmd.WorkOrderId,
            OperationId = cmd.OperationId,
            WorkOrderMachineId = cmd.WorkOrderMachineId,
            MachineCode = cmd.MachineCode,
            RequestedStart = cmd.RequestedStart,
            Reason = string.IsNullOrWhiteSpace(cmd.Reason) ? "Draft schedule move" : cmd.Reason.Trim()
        };

        var order = (await LoadOrdersAsync([cmd.WorkOrderId], cancellationToken)).SingleOrDefault();
        var plan = new MachineScheduleAdvancedPlan
        {
            Action = MachineScheduleAdvancedAction.InsertSplit,
            Request = request,
            Scope = _scope,
            Db = _db
        };
        if (order is null)
        {
            plan.Blockers.Add("Work Order was not found in the current company and branch.");
            return plan;
        }
        plan.TargetOrder = order;
        var operation = order.RouteSteps.SelectMany(x => x.Operations).SingleOrDefault(x => x.Uid == cmd.OperationId);
        var machine = operation?.Machines.SingleOrDefault(x => x.Uid == cmd.WorkOrderMachineId && x.IsSelected);
        var step = order.RouteSteps.SingleOrDefault(x => x.Uid == operation?.RouteStepId);
        if (operation is null || machine is null || step is null)
        {
            plan.Blockers.Add("The selected operation or machine no longer belongs to this Work Order.");
            return plan;
        }
        plan.TargetOperation = operation;
        plan.TargetMachine = machine;
        plan.TargetStep = step;
        if (cmd.RequestedStart is null)
        {
            plan.Blockers.Add("A requested start date and time is required.");
            return plan;
        }

        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            plan.Blockers.Add("Only Released or In Progress Work Orders can be rescheduled.");
        if (string.Equals(order.SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.OrdinalIgnoreCase))
            plan.Blockers.Add("Backward-scheduled Work Orders remain locked until backward placement is proven.");
        if (order.SnapshotFormatVersion != ProductionSnapshotFormatVersions.Current)
            plan.Blockers.Add("Legacy Work Order snapshots are not eligible for schedule-only changes.");
        if (order.GoodQty > 0m || order.ScrapQty > 0m || order.RejectQty > 0m || order.HoldQty > 0m || order.ApprovedVarianceQty > 0m)
            plan.Blockers.Add("Work Order execution quantities show that production has started.");

        var affectedOps = GetMoveAffectedOperations(order, step, operation);
        if (affectedOps.Any(HasExecution))
            plan.Blockers.Add("One or more affected operations already contain execution quantities.");

        var affectedOpIds = affectedOps.Select(x => x.Uid).ToList();
        var outputs = await _db.ProductionOutputs.AsNoTracking()
            .Where(x => affectedOpIds.Contains(x.WorkOrderOperationId)
                && ((x.Status == ProductionOutputStatuses.New && x.DeletedAtUtc == null)
                    || x.Status == ProductionOutputStatuses.Posted
                    || x.PostingId != null))
            .Select(x => x.WorkOrderOperationId)
            .ToListAsync(cancellationToken);
        if (outputs.Count > 0)
            plan.Blockers.Add("Affected operation(s) have Daily Production records; resolve them before rescheduling.");

        var affectedMachines = affectedOps
            .SelectMany(o => o.Machines.Where(m => m.IsSelected).Select(m => (Operation: o, Machine: m)))
            .ToList();
        var affectedMachineIds = affectedMachines.Select(x => x.Machine.Uid).ToHashSet();

        foreach (var pair in affectedMachines)
        {
            if (_activeHolds.Any(h => h.WorkOrderMachineId == pair.Machine.Uid && h.Status == ProductionScheduleHoldStatuses.Active))
            {
                plan.Blockers.Add("This operation has an active schedule hold. Release the hold before moving it.");
                break;
            }
            var set = GetCurrentSet(pair.Machine.Uid);
            if (set is not null && (set.Mode != ProductionMachineScheduleSegmentSetModes.FullPlan
                || set.PlanningState != ProductionMachineSchedulePlanningStates.Scheduled))
            {
                plan.Blockers.Add("This operation uses an advanced split/execution schedule. Use Replan Remaining or Split/Resume instead.");
                break;
            }
            var segs = _branchSegments.Where(x => x.WorkOrderMachineId == pair.Machine.Uid).ToList();
            if (segs.Count > 1 || segs.Any(x => x.SegmentKind != ProductionMachineScheduleSegmentKinds.Original))
            {
                plan.Blockers.Add("This operation uses an advanced split/execution schedule. Use Advanced Schedule actions.");
                break;
            }
        }

        if (plan.Blockers.Count > 0)
            return plan;

        var orderPlan = new AdvancedOrderPlan
        {
            Order = order,
            TargetStep = step,
            TargetOperation = operation,
            Before = (order.PlannedStartDateTime, order.PlannedCompletionDateTime)
        };
        foreach (var op in affectedOps)
            orderPlan.MovingOperationIds.Add(op.Uid);
        plan.Orders = [orderPlan];

        // Load only external branch + fixed capacity (excluding same-branch machines so temporary draft moves land on exact requested slot; final zero-overlap checks same-branch collisions).
        var machineCodes = affectedMachines.Select(x => x.Machine.MachineCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allBranchMachineIds = _branchSegments.Select(x => x.WorkOrderMachineId)
            .Concat(_allocationsByMachine.Keys)
            .Concat(affectedMachineIds)
            .ToHashSet();
        var evidence = await _capacity.LoadAsync(
            _db, _scope.CompanyCode, machineCodes,
            excludedWorkOrderMachineIds: allBranchMachineIds,
            cancellationToken: cancellationToken);
        var occupied = evidence.OccupiedByMachine.ToDictionary(
            kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);

        var requestedStart = DateTime.SpecifyKind(cmd.RequestedStart.Value, DateTimeKind.Unspecified);
        var fixedDates = new Dictionary<long, MachineScheduleOperationDates>();
        foreach (var s in order.RouteSteps)
        {
            foreach (var op in s.Operations)
            {
                if (orderPlan.MovingOperationIds.Contains(op.Uid))
                    continue;
                if (op.PlannedStartDateTime is DateTime st && op.PlannedCompletionDateTime is DateTime en)
                    fixedDates[op.Uid] = new MachineScheduleOperationDates(op.Uid, st, en);
                else
                {
                    plan.Blockers.Add($"Operation {op.OperationCode} has incomplete saved dates.");
                    return plan;
                }
            }
        }

        var projection = MachineScheduleDateRangeProjector.ProjectForward(
            order,
            order.PlannedStartDateTime,
            (s, op, earliest) =>
            {
                var anchor = op.Uid == operation.Uid && requestedStart > earliest ? requestedStart : earliest;
                if (op.Uid == operation.Uid && requestedStart < earliest)
                    return MachineScheduleProjectionPlacement.Failed(
                        $"Operation {op.OperationCode} cannot start before its upstream predecessor completes at {earliest:yyyy-MM-dd HH:mm}.");

                var opMachine = PrProcessTypes.SupportsMachine(op.ProcessType)
                    ? op.Machines.SingleOrDefault(m => m.IsSelected)
                    : null;
                if (PrProcessTypes.SupportsMachine(op.ProcessType) && opMachine is null)
                    return MachineScheduleProjectionPlacement.Failed($"Operation {op.OperationCode} has no selected machine.");

                var qPlan = opMachine is null
                    ? default
                    : MachineScheduleSegmentCalculator.ComputeQuantityAsync(
                        _scope.CompanyCode, op, opMachine, op.PlannedOutputQty, _uom, cancellationToken)
                        .GetAwaiter().GetResult();
                var duration = opMachine is null
                    ? TimeSpan.FromMinutes((double)op.StandardDurationMinutes)
                    : TimeSpan.FromSeconds((double)(opMachine.SetupSeconds + opMachine.ConversionSeconds + opMachine.QueueSeconds))
                        + TimeSpan.FromMinutes((double)opMachine.PlannedRunMinutes);
                if (duration <= TimeSpan.Zero)
                    return MachineScheduleProjectionPlacement.Failed($"Operation {op.OperationCode} has no positive schedule duration.");

                var placement = (opMachine is null
                        ? _engine.PlacePlantAsync(order.CompanyCode, order.BranchCode, anchor, duration, cancellationToken)
                        : _engine.PlaceMachineAsync(order.CompanyCode, opMachine.MachineCode, anchor, duration, occupied, cancellationToken))
                    .GetAwaiter().GetResult();
                if (!placement.Succeeded)
                    return MachineScheduleProjectionPlacement.Failed(placement.Error!);

                if (opMachine is not null)
                {
                    var key = MachineScheduleAdvancedPlacementEngine.Normalize(opMachine.MachineCode);
                    if (!occupied.TryGetValue(key, out var list))
                    {
                        list = [];
                        occupied[key] = list;
                    }
                    list.AddRange(placement.Fragments);
                    plan.AffectedMachineIds.Add(opMachine.Uid);

                    foreach (var prevSeg in _branchSegments.Where(x => x.WorkOrderMachineId == opMachine.Uid))
                        plan.SegmentsToSupersede.Add(prevSeg);

                    var newSeg = new PlannedSegment
                    {
                        WorkOrderId = order.Uid,
                        OperationId = op.Uid,
                        WorkOrderMachineId = opMachine.Uid,
                        Sequence = 1,
                        MachineCode = opMachine.MachineCode,
                        WorkOrderNo = order.WorkOrderNo,
                        OperationCode = op.OperationCode,
                        Kind = ProductionMachineScheduleSegmentKinds.Original,
                        Status = ProductionMachineScheduleSegmentStatuses.Planned,
                        Qty = op.PlannedOutputQty,
                        Uom = op.PlannedOutputUom,
                        CycleCount = opMachine.PlannedCycleCount > 0m ? opMachine.PlannedCycleCount : qPlan.CycleCount,
                        CycleSlots = opMachine.PlannedCycleSlots > 0m ? opMachine.PlannedCycleSlots : qPlan.CycleSlots,
                        RunMinutes = opMachine.PlannedRunMinutes,
                        SetupSeconds = opMachine.SetupSeconds,
                        ConversionSeconds = opMachine.ConversionSeconds,
                        QueueSeconds = opMachine.QueueSeconds,
                        Start = placement.Start,
                        Completion = placement.Completion,
                        HorizonStart = placement.HorizonStart.ToDateTime(TimeOnly.MinValue),
                        HorizonEnd = placement.HorizonEnd.ToDateTime(TimeOnly.MinValue),
                        SourceHash = placement.SourceHash,
                        Fragments = placement.Fragments,
                        Reason = request.Reason
                    };
                    plan.SegmentsByMachine[opMachine.Uid] = [newSeg];
                }

                return new MachineScheduleProjectionPlacement(placement.Start, placement.Completion);
            },
            fixedDates);

        if (!projection.Succeeded)
        {
            plan.Blockers.AddRange(projection.Blockers);
            return plan;
        }

        orderPlan.AfterStart = projection.WorkOrderStart;
        orderPlan.AfterCompletion = projection.WorkOrderCompletion;
        plan.Projections[order.Uid] = projection;
        return plan;
    }

    public async Task<List<MachineScheduleDraftConflict>> DetectFinalOverlapsAsync(
        IReadOnlyCollection<string> touchedMachineCodes,
        IReadOnlyCollection<long> explicitWorkOrderIds,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<MachineScheduleDraftConflict>();
        var codes = touchedMachineCodes
            .Select(MachineScheduleAdvancedPlacementEngine.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            return conflicts;

        // Collect all current branch allocations on those machine codes (including off-filter untouched jobs).
        var machineOrderLookup = _ordersById.Values.ToDictionary(x => x.Uid, x => x.WorkOrderNo);
        var entriesByMachine = new Dictionary<string, List<(long? WoId, long MachineId, string WoNo, DateTime Start, DateTime End, bool IsExternal)>>(StringComparer.Ordinal);

        foreach (var pair in _allocationsByMachine)
        {
            foreach (var alloc in pair.Value)
            {
                var key = MachineScheduleAdvancedPlacementEngine.Normalize(alloc.MachineCode);
                if (!codes.Contains(key))
                    continue;
                if (!entriesByMachine.TryGetValue(key, out var list))
                {
                    list = [];
                    entriesByMachine[key] = list;
                }
                var woNo = machineOrderLookup.TryGetValue(alloc.WorkOrderId, out var name) ? name : $"WO-{alloc.WorkOrderId}";
                list.Add((alloc.WorkOrderId, alloc.WorkOrderMachineId, woNo, alloc.StartDateTime, alloc.EndDateTime, false));
            }
        }

        // Add external branch active allocations on touched machines.
        var branchCode = _scope.BranchCode ?? string.Empty;
        var externalRows = await _db.ProductionMachineScheduleAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == _scope.CompanyCode
                && a.BranchCode != branchCode
                && codes.Contains(a.MachineCode.ToUpper()))
            .Join(_db.ProductionWorkOrderMachines.AsNoTracking(),
                a => a.WorkOrderMachineId, m => m.Uid, (a, m) => new { a, m })
            .Where(x => x.m.IsSelected
                && x.m.Operation != null
                && x.m.Operation.WorkOrder != null
                && (x.m.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || x.m.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && x.m.ScheduleRevision == x.a.ScheduleRevision)
            .Select(x => new { x.a.MachineCode, x.a.WorkOrderMachineId, x.a.StartDateTime, x.a.EndDateTime })
            .ToListAsync(cancellationToken);

        foreach (var ext in externalRows)
        {
            var key = MachineScheduleAdvancedPlacementEngine.Normalize(ext.MachineCode);
            if (!entriesByMachine.TryGetValue(key, out var list))
            {
                list = [];
                entriesByMachine[key] = list;
            }
            list.Add((null, ext.WorkOrderMachineId, "Other branch reservation", ext.StartDateTime, ext.EndDateTime, true));
        }

        foreach (var pair in entriesByMachine)
        {
            var sorted = pair.Value.OrderBy(x => x.Start).ThenBy(x => x.MachineId).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                var a = sorted[i];
                for (var j = i + 1; j < sorted.Count; j++)
                {
                    var b = sorted[j];
                    if (b.Start >= a.End)
                        break;
                    if (a.MachineId == b.MachineId && !a.IsExternal && !b.IsExternal)
                        continue;

                    var overlapStart = a.Start > b.Start ? a.Start : b.Start;
                    var overlapEnd = a.End < b.End ? a.End : b.End;
                    if (overlapEnd <= overlapStart)
                        continue;

                    var isOffFilter = (!a.IsExternal && a.WoId is long idA && !explicitWorkOrderIds.Contains(idA))
                        || (!b.IsExternal && b.WoId is long idB && !explicitWorkOrderIds.Contains(idB));
                    conflicts.Add(new MachineScheduleDraftConflict
                    {
                        MachineCode = pair.Key,
                        AppointmentIdA = $"m-{a.MachineId}",
                        AppointmentIdB = $"m-{b.MachineId}",
                        WorkOrderIdA = a.WoId,
                        WorkOrderIdB = b.WoId,
                        WorkOrderNoA = a.WoNo,
                        WorkOrderNoB = b.WoNo,
                        OverlapStart = overlapStart,
                        OverlapEnd = overlapEnd,
                        IsOffFilterConflict = isOffFilter,
                        IsExternalOrMaintenanceConflict = a.IsExternal || b.IsExternal,
                        Message = $"{a.WoNo} overlaps {b.WoNo} on {pair.Key} ({overlapStart:yyyy-MM-dd HH:mm}–{overlapEnd:HH:mm})."
                    });
                }
            }
        }

        return conflicts;
    }

    private static List<ProductionWorkOrderOperation> GetMoveAffectedOperations(
        ProductionWorkOrder order,
        ProductionWorkOrderRouteStep targetStep,
        ProductionWorkOrderOperation targetOperation)
    {
        var affected = new List<ProductionWorkOrderOperation>();
        var dependentSteps = CollectDependentSteps(order, targetStep.Uid);
        foreach (var step in order.RouteSteps.OrderBy(x => x.StageSequence).ThenBy(x => x.Uid))
        {
            var isTarget = step.Uid == targetStep.Uid;
            var isLater = step.StageSequence > targetStep.StageSequence || dependentSteps.Contains(step.Uid);
            if (!isTarget && !isLater) continue;

            foreach (var op in step.Operations.OrderBy(x => x.ProcessSequence).ThenBy(x => x.OperationCode, StringComparer.Ordinal).ThenBy(x => x.Uid))
            {
                if (isLater || op.ProcessSequence > targetOperation.ProcessSequence || op.Uid == targetOperation.Uid)
                    affected.Add(op);
            }
        }
        return affected;
    }

    private static HashSet<long> CollectDependentSteps(ProductionWorkOrder order, long sourceStepUid)
    {
        var result = new HashSet<long> { sourceStepUid };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var step in order.RouteSteps)
            {
                if (result.Contains(step.Uid)) continue;
                var linked = step.Operations.SelectMany(o => o.Materials)
                    .Any(m => m.SupplySource == PrMaterialSupplySources.InternalRouteWip
                        && m.ProducingRouteStepId is long pId && result.Contains(pId));
                if (linked && result.Add(step.Uid))
                    changed = true;
            }
        }
        result.Remove(sourceStepUid);
        return result;
    }

    private static bool HasExecution(ProductionWorkOrderOperation operation) =>
        operation.InputQty != 0m || operation.ProcessedQty != 0m || operation.GoodQty != 0m
        || operation.ScrapQty != 0m || operation.RejectQty != 0m || operation.HoldQty != 0m
        || operation.ReworkQty != 0m || operation.TransferredQty != 0m;
}
