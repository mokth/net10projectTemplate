using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Builds an advanced planning proposal. It performs reads only. Commit calls it again on the
/// locked transaction and persists the returned plan.
/// </summary>
internal sealed partial class MachineScheduleAdvancedPlanBuilder
{
    private readonly AppDbContext _db;
    private readonly InventoryTenantScope _scope;
    private readonly IProductionMachineCapacityService _capacity;
    private readonly IUomConversionService _uom;
    private readonly MachineScheduleAdvancedPlacementEngine _engine;
    private readonly bool _tracking;

    public MachineScheduleAdvancedPlanBuilder(
        AppDbContext db,
        InventoryTenantScope scope,
        IProductionMachineCapacityService capacity,
        IUomConversionService uom,
        MachineScheduleAdvancedPlacementEngine engine,
        bool tracking)
    {
        _db = db;
        _scope = scope;
        _capacity = capacity;
        _uom = uom;
        _engine = engine;
        _tracking = tracking;
    }

    public async Task<MachineScheduleAdvancedPlan> BuildAsync(
        MachineScheduleAdvancedRequest request,
        CancellationToken cancellationToken)
    {
        var orders = await LoadOrdersAsync([request.WorkOrderId], cancellationToken);
        var order = orders.SingleOrDefault();
        if (order is null)
        {
            var missing = NewPlan(request);
            missing.Blockers.Add("Work Order was not found in the current company and branch.");
            missing.Preview = BuildPreview(missing);
            return missing;
        }

        var plan = NewPlan(request);
        plan.TargetOrder = order;
        var target = ResolveTarget(order, request);
        if (target.Operation is null || target.Machine is null || target.Step is null)
        {
            plan.Blockers.Add(target.Error ?? "The selected machine operation is unavailable.");
            plan.Preview = BuildPreview(plan);
            return plan;
        }

        plan.TargetOperation = target.Operation;
        plan.TargetMachine = target.Machine;
        plan.TargetStep = target.Step;
        plan.RequestedSplit = request.RequestedStart ?? DateTime.Now;
        plan.ResolvedSplit = plan.RequestedSplit;
        ValidateCommon(plan);

        var currentSet = await _db.ProductionMachineScheduleSegmentSets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkOrderMachineId == plan.TargetMachine.Uid
                && x.IsCurrent, cancellationToken);
        var hasCurrentTargetSegment = await _db.ProductionMachineScheduleSegments
            .AsNoTracking()
            .AnyAsync(x => x.CompanyCode == _scope.CompanyCode
                && x.BranchCode == _scope.BranchCode
                && x.WorkOrderMachineId == plan.TargetMachine.Uid
                && (currentSet == null ? x.MachineScheduleRevision == plan.TargetMachine.ScheduleRevision
                    : x.SegmentSetId == currentSet.Uid)
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded,
                cancellationToken);
        var canReplanEmptySet = request.Action == MachineScheduleAdvancedAction.ReplanRemaining
            && currentSet is not null && currentSet.PlanningState is
                ProductionMachineSchedulePlanningStates.HeldUnscheduled or
                ProductionMachineSchedulePlanningStates.MaintenanceUnscheduled or
                ProductionMachineSchedulePlanningStates.Completed;
        if (!hasCurrentTargetSegment && !canReplanEmptySet)
        {
            plan.Blockers.Add(
                "The active machine schedule has no current business segment. Run segment backfill "
                + "before using advanced split, hold, or replan actions.");
            plan.Preview = BuildPreview(plan);
            return plan;
        }

        var branchSegments = await LoadBranchSegmentsAsync(cancellationToken);
        if (currentSet is not null)
        {
            var futureSegments = branchSegments.Where(x => x.SegmentSetId == currentSet.Uid).ToList();
            if ((currentSet.PlanningState == ProductionMachineSchedulePlanningStates.ReplanRequired
                || currentSet.PlanningState == ProductionMachineSchedulePlanningStates.Scheduled
                    && MachineScheduleSegmentPersistence.RequiresRemainingReplan(
                        plan.TargetOperation.RemainingQty, futureSegments))
                && request.Action != MachineScheduleAdvancedAction.ReplanRemaining)
                plan.Blockers.Add("Replan Remaining required: actual remaining quantity exceeds the current future plan.");
        }
        var machineIds = branchSegments.Select(x => x.WorkOrderMachineId)
            .Append(plan.TargetMachine.Uid)
            .ToHashSet();
        var allocations = await LoadAllocationsAsync(machineIds, branchSegments, cancellationToken);
        var context = new PlanContext(plan, branchSegments, allocations);

        if (plan.Blockers.Count == 0)
        {
            try
            {
                switch (request.Action)
                {
                    case MachineScheduleAdvancedAction.InsertSplit:
                        await BuildInsertAsync(context, cancellationToken);
                        break;
                    case MachineScheduleAdvancedAction.Hold:
                        await BuildHoldAsync(context, cancellationToken);
                        break;
                    case MachineScheduleAdvancedAction.ReleaseHold:
                        await BuildReleaseHoldAsync(context, cancellationToken);
                        break;
                    case MachineScheduleAdvancedAction.ReplanRemaining:
                        await BuildReplanRemainingAsync(context, cancellationToken);
                        break;
                    case MachineScheduleAdvancedAction.MaintenanceReplan:
                        await BuildMaintenanceAsync(context, cancellationToken);
                        break;
                    default:
                        plan.Blockers.Add("Unknown advanced scheduling action.");
                        break;
                }
                if (plan.Blockers.Count == 0)
                    await ProjectAsync(context, cancellationToken);
            }
            catch (MachineScheduleQuantityConversionException ex)
            {
                plan.Blockers.Add(ex.Message);
            }
        }

        EnforceBlastRadius(plan);
        plan.Preview = BuildPreview(plan);
        return plan;
    }

    private MachineScheduleAdvancedPlan NewPlan(MachineScheduleAdvancedRequest request) => new()
    {
        Action = request.Action,
        Request = request,
        Scope = _scope,
        Db = _db
    };

    private void EnforceBlastRadius(MachineScheduleAdvancedPlan plan)
    {
        if (plan.Orders.Count > MachineScheduleSegmentPersistence.MaxAffectedWorkOrders)
            plan.Blockers.Add($"This action would touch {plan.Orders.Count} Work Orders. The command cap is {MachineScheduleSegmentPersistence.MaxAffectedWorkOrders}; use a narrower planning action.");
        var segmentCount = plan.AllSegments.Count + plan.SegmentsToSupersede.Count;
        if (segmentCount > MachineScheduleSegmentPersistence.MaxAffectedSegments)
            plan.Blockers.Add($"This action would touch {segmentCount} schedule segments. The command cap is {MachineScheduleSegmentPersistence.MaxAffectedSegments}; use a narrower planning action.");
    }

    private void ValidateCommon(MachineScheduleAdvancedPlan plan)
    {
        var order = plan.TargetOrder;
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            plan.Blockers.Add("Only Released or In Progress Work Orders can use advanced machine scheduling.");
        if (string.Equals(order.SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.OrdinalIgnoreCase))
            plan.Blockers.Add("Backward-scheduled Work Orders remain locked until backward placement is proven.");
        if (order.SnapshotFormatVersion != ProductionSnapshotFormatVersions.Current)
            plan.Blockers.Add("Legacy Work Order snapshots are not eligible for schedule-only changes.");
        if (!string.Equals(
                MachineScheduleAdvancedPlacementEngine.Normalize(plan.TargetMachine.MachineCode),
                MachineScheduleAdvancedPlacementEngine.Normalize(plan.Request.MachineCode),
                StringComparison.Ordinal))
            plan.Blockers.Add("Cross-machine schedule changes are not supported by this action.");
    }

    private static (ProductionWorkOrderOperation? Operation, ProductionWorkOrderMachine? Machine,
        ProductionWorkOrderRouteStep? Step, string? Error) ResolveTarget(
        ProductionWorkOrder order,
        MachineScheduleAdvancedRequest request)
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

    private async Task<List<ProductionWorkOrder>> LoadOrdersAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        var distinct = ids.Where(x => x > 0).Distinct().ToList();
        if (distinct.Count == 0)
            return [];
        IQueryable<ProductionWorkOrder> query = _db.ProductionWorkOrders
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Machines)
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Materials).ThenInclude(x => x.ProducingRouteStep)
            .AsSplitQuery()
            .Where(x => distinct.Contains(x.Uid)
                && x.CompanyCode == _scope.CompanyCode
                && x.BranchCode == _scope.BranchCode);
        if (!_tracking)
            query = query.AsNoTracking();
        return await query.ToListAsync(cancellationToken);
    }

    private async Task<List<ProductionMachineScheduleSegment>> LoadBranchSegmentsAsync(
        CancellationToken cancellationToken)
    {
        var segments = await _db.ProductionMachineScheduleSegments.AsNoTracking()
            .Where(x => x.CompanyCode == _scope.CompanyCode
                && x.BranchCode == _scope.BranchCode
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .OrderBy(x => x.MachineCode)
            .ThenBy(x => x.WorkOrderMachineId)
            .ThenBy(x => x.SegmentSequence)
            .ToListAsync(cancellationToken);
        var currentSets = await _db.ProductionMachineScheduleSegmentSets.AsNoTracking()
            .Where(x => x.CompanyCode == _scope.CompanyCode
                && x.BranchCode == _scope.BranchCode && x.IsCurrent)
            .Select(x => new { x.Uid, x.WorkOrderMachineId })
            .ToListAsync(cancellationToken);
        var currentSetIds = currentSets.Select(x => x.Uid).ToHashSet();
        var machinesWithSets = currentSets.Select(x => x.WorkOrderMachineId).ToHashSet();
        return segments.Where(x => x.SegmentSetId is long setId
                ? currentSetIds.Contains(setId)
                : !machinesWithSets.Contains(x.WorkOrderMachineId))
            .ToList();
    }

    private async Task<Dictionary<long, List<ProductionMachineScheduleAllocation>>> LoadAllocationsAsync(
        IReadOnlyCollection<long> machineIds,
        IReadOnlyCollection<ProductionMachineScheduleSegment> currentSegments,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, List<ProductionMachineScheduleAllocation>>();
        if (machineIds.Count == 0)
            return result;
        var rows = new List<ProductionMachineScheduleAllocation>();
        foreach (var batch in machineIds.Chunk(800))
        {
            rows.AddRange(await _db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => batch.Contains(x.WorkOrderMachineId))
                .OrderBy(x => x.WorkOrderMachineId)
                .ThenBy(x => x.ScheduleRevision)
                .ThenBy(x => x.FragmentNo)
                .ToListAsync(cancellationToken));
        }
        var currentSegmentIds = currentSegments.Where(x => x.SegmentSetId is not null)
            .Select(x => x.Uid).ToHashSet();
        var linkedAllocationIds = new HashSet<long>();
        foreach (var batch in currentSegmentIds.Chunk(800))
        {
            var ids = await _db.ProductionMachineScheduleAllocationSegments.AsNoTracking()
                .Where(x => batch.Contains(x.SegmentId))
                .Select(x => x.AllocationId)
                .ToListAsync(cancellationToken);
            linkedAllocationIds.UnionWith(ids);
        }
        var legacySegments = currentSegments.Where(x => x.SegmentSetId is null)
            .GroupBy(x => x.WorkOrderMachineId)
            .ToDictionary(x => x.Key, x => x.ToList());
        rows = rows.Where(row => linkedAllocationIds.Contains(row.Uid)
            || legacySegments.TryGetValue(row.WorkOrderMachineId, out var legacy)
                && legacy.Any(segment => segment.MachineScheduleRevision == row.ScheduleRevision
                    && (row.MachineScheduleSegmentId is null
                        || row.MachineScheduleSegmentId == segment.Uid)))
            .ToList();
        foreach (var group in rows.GroupBy(x => x.WorkOrderMachineId))
            result[group.Key] = group.OrderBy(x => x.ScheduleRevision).ThenBy(x => x.FragmentNo).ToList();
        return result;
    }

    private async Task<PlanCapacity> LoadCapacityAsync(
        IReadOnlyCollection<string> machineCodes,
        IReadOnlySet<long> excludedMachineIds,
        CancellationToken cancellationToken)
    {
        var result = new PlanCapacity();
        if (machineCodes.Count == 0)
            return result;
        var evidence = await _capacity.LoadAsync(
            _db, _scope.CompanyCode, machineCodes,
            excludedWorkOrderMachineIds: excludedMachineIds,
            cancellationToken: cancellationToken);
        foreach (var pair in evidence.OccupiedByMachine)
            result.OccupiedByMachine[pair.Key] = [.. pair.Value];
        result.Warnings.AddRange(evidence.Blockers);
        return result;
    }

    private Task<MachineScheduleQuantityPlan> SegmentCalculatorAsync(
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine machine,
        decimal qty,
        CancellationToken cancellationToken) =>
        MachineScheduleSegmentCalculator.ComputeQuantityAsync(
            _scope.CompanyCode, operation, machine, qty, _uom, cancellationToken);

    private static TimeSpan FirstSegmentDuration(ProductionWorkOrderMachine machine, decimal runMinutes) =>
        TimeSpan.FromSeconds((double)(machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds))
        + TimeSpan.FromMinutes((double)runMinutes);

    private static bool HasExecution(ProductionWorkOrderOperation operation) =>
        operation.InputQty != 0m || operation.ProcessedQty != 0m || operation.GoodQty != 0m
        || operation.ScrapQty != 0m || operation.RejectQty != 0m || operation.HoldQty != 0m
        || operation.ReworkQty != 0m || operation.TransferredQty != 0m;

    private static decimal RemainingQuantity(ProductionWorkOrderOperation operation) =>
        Math.Max(0m, operation.RemainingQty);

    private async Task<PlannedSegment> NewSegmentAsync(
        MachineScheduleAdvancedPlan plan,
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine machine,
        MachineSchedulePlacement placement,
        decimal qty,
        decimal setupSeconds,
        decimal conversionSeconds,
        decimal queueSeconds,
        string kind,
        string reason,
        long? sourceSegmentId,
        CancellationToken cancellationToken)
    {
        var quantityPlan = await SegmentCalculatorAsync(operation, machine, qty, cancellationToken);
        return new PlannedSegment
        {
            WorkOrderId = operation.WorkOrderId ?? plan.TargetOrder.Uid,
            WorkOrderNo = plan.Orders.FirstOrDefault(x => x.Order.Uid == operation.WorkOrderId)?.Order.WorkOrderNo
                ?? plan.TargetOrder.WorkOrderNo,
            OperationId = operation.Uid,
            OperationCode = operation.OperationCode,
            WorkOrderMachineId = machine.Uid,
            MachineCode = machine.MachineCode,
            Kind = kind,
            Status = ProductionMachineScheduleSegmentStatuses.Planned,
            SourceSegmentId = sourceSegmentId,
            Qty = qty,
            Uom = operation.PlannedOutputUom,
            CycleCount = quantityPlan.CycleCount,
            CycleSlots = quantityPlan.CycleSlots,
            RunMinutes = quantityPlan.RunMinutes,
            SetupSeconds = setupSeconds,
            ConversionSeconds = conversionSeconds,
            QueueSeconds = queueSeconds,
            Start = placement.Start,
            Completion = placement.Completion,
            SourceHash = placement.SourceHash,
            HorizonStart = placement.HorizonStart.ToDateTime(TimeOnly.MinValue),
            HorizonEnd = placement.HorizonEnd.ToDateTime(TimeOnly.MinValue),
            Fragments = placement.Fragments,
            Reason = MachineScheduleSegmentPersistence.TrimReason(reason)
        };
    }

    private ProductionWorkOrderScheduleHold NewHold(
        MachineScheduleAdvancedPlan plan, string holdType, DateTime from, DateTime? resumeNotBefore) => new()
    {
        CompanyCode = plan.TargetOrder.CompanyCode,
        BranchCode = plan.TargetOrder.BranchCode,
        WorkOrderId = plan.TargetOrder.Uid,
        WorkOrderOperationId = plan.TargetOperation.Uid,
        WorkOrderMachineId = plan.TargetMachine.Uid,
        HoldType = holdType,
        Status = ProductionScheduleHoldStatuses.Active,
        HoldFrom = from,
        ResumeNotBefore = resumeNotBefore,
        Reason = MachineScheduleSegmentPersistence.TrimReason(plan.Request.Reason),
        CreatedDate = DateTime.UtcNow,
        CreatedBy = MachineScheduleSegmentPersistence.TruncateUser(plan.Scope.UserId),
        RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(_db)
    };

    private static MachineScheduleAdvancedPreview BuildPreview(MachineScheduleAdvancedPlan plan)
    {
        var segments = plan.SegmentsByMachine.Values.SelectMany(x => x)
            .OrderBy(x => x.Start)
            .ThenBy(x => x.MachineCode, StringComparer.Ordinal)
            .ThenBy(x => x.OperationCode, StringComparer.Ordinal)
            .ToList();
        var targetSegments = segments
            .Where(x => x.WorkOrderMachineId == plan.TargetMachine.Uid)
            .ToList();
        return new MachineScheduleAdvancedPreview
        {
            Action = plan.Action,
            WorkOrderId = plan.TargetOrder.Uid,
            WorkOrderNo = plan.TargetOrder.WorkOrderNo,
            TargetOperationId = plan.TargetOperation.Uid,
            TargetOperationCode = plan.TargetOperation.OperationCode,
            MachineCode = plan.TargetMachine.MachineCode,
            CurrentStart = plan.TargetOperation.PlannedStartDateTime ?? default,
            CurrentCompletion = plan.TargetOperation.PlannedCompletionDateTime ?? default,
            ProposedStart = targetSegments.Count > 0 ? targetSegments.Min(x => x.Start) : null,
            // Unknown hold / open-ended repair never publishes a fabricated completion, even when
            // a known prefix segment remains before the interruption.
            ProposedCompletion = plan.HasUnscheduledRemaining
                ? null
                : targetSegments.Count > 0 ? targetSegments.Max(x => x.Completion) : null,
            RequestedSplit = plan.RequestedSplit,
            ResolvedSplit = plan.ResolvedSplit,
            SplitExplanation = plan.SplitExplanation,
            RequestedQty = plan.RequestedQty,
            ScheduledQty = plan.ScheduledQty,
            CycleCount = targetSegments.Sum(x => x.CycleCount),
            CycleSlots = targetSegments.Sum(x => x.CycleSlots),
            RestartOverheadSeconds = plan.RestartOverheadSeconds,
            HasUnscheduledRemaining = plan.HasUnscheduledRemaining,
            ForecastCompletion = plan.HasUnscheduledRemaining ? null : plan.ForecastCompletion,
            UnscheduledMessage = plan.HasUnscheduledRemaining
                ? "Completion unknown — remaining quantity is held and not scheduled."
                : null,
            Segments = segments.Select((segment, index) => new MachineScheduleAdvancedSegmentPreview
            {
                SegmentId = segment.Existing?.Uid,
                WorkOrderId = segment.WorkOrderId,
                WorkOrderNo = segment.WorkOrderNo,
                OperationId = segment.OperationId,
                OperationCode = segment.OperationCode,
                WorkOrderMachineId = segment.WorkOrderMachineId,
                MachineCode = segment.MachineCode,
                Sequence = index + 1,
                Kind = segment.Kind,
                Status = segment.Status,
                PlannedOutputQty = segment.Qty,
                CycleCount = segment.CycleCount,
                CycleSlots = segment.CycleSlots,
                RunMinutes = segment.RunMinutes,
                SetupSeconds = segment.SetupSeconds,
                ConversionSeconds = segment.ConversionSeconds,
                QueueSeconds = segment.QueueSeconds,
                Start = segment.Start,
                Completion = segment.Completion,
                Reason = segment.Reason,
                IsExisting = segment.Existing is not null,
                ProductiveIntervals = segment.Fragments.Select(x => new MachineScheduleAdvancedInterval
                {
                    Start = x.Start,
                    End = x.End
                }).ToList()
            }).ToList(),
            AffectedWorkOrders = plan.Orders
                .Where(x => x.Order.Uid > 0)
                .OrderBy(x => x.Order.WorkOrderNo, StringComparer.Ordinal)
                .Select(x => new MachineScheduleAdvancedWorkOrderPreview
                {
                    WorkOrderId = x.Order.Uid,
                    WorkOrderNo = x.Order.WorkOrderNo,
                    StartBefore = x.Before.Start,
                    CompletionBefore = x.Before.Completion,
                    StartAfter = x.AfterStart,
                    CompletionAfter = plan.HasUnscheduledRemaining ? null : x.AfterCompletion,
                    LateBefore = x.Before.Completion < DateTime.Today,
                    LateAfter = !plan.HasUnscheduledRemaining
                        && x.AfterCompletion is DateTime after && after < DateTime.Today,
                    HasUnscheduledRemaining = x.HasUnscheduledRemaining || plan.HasUnscheduledRemaining,
                    DrAdvisory = plan.HasUnscheduledRemaining
                        ? "WO completion is unknown until the interruption is resolved."
                        : x.AfterCompletion is DateTime completion
                            ? $"WO completion {x.Before.Completion:yyyy-MM-dd} → {completion:yyyy-MM-dd}"
                            : null
                }).ToList(),
            Warnings = plan.Warnings.Distinct(StringComparer.Ordinal).ToList(),
            Blockers = plan.Blockers.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private sealed class PlanCapacity
    {
        public Dictionary<string, List<TimeInterval>> OccupiedByMachine { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = [];
    }
}
