using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed partial class MachineScheduleAdvancedPlanBuilder
{
    private async Task BuildMaintenanceAsync(PlanContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        var machineCode = string.IsNullOrWhiteSpace(plan.Request.MachineCode)
            ? plan.TargetMachine.MachineCode
            : plan.Request.MachineCode;
        var downtime = await LoadDowntimeAsync(plan, machineCode, cancellationToken);
        if (downtime is null)
        {
            plan.Blockers.Add("No active or completed breakdown/repair/service downtime was found for this machine.");
            return;
        }
        var (downStart, downEnd, maintenanceType, isOpenEnded) = downtime.Value;
        plan.RequestedSplit = downStart;
        plan.ResolvedSplit = downStart;
        plan.EventType = ProductionAuditEventTypes.ScheduleMaintenanceReplanned;
        plan.Warnings.Add(isOpenEnded
            ? "The maintenance has no known end. Scheduling blocks the machine only to the loaded horizon; unfinished quantity stays unscheduled and no repair completion is invented."
            : $"Maintenance window {downStart:yyyy-MM-dd HH:mm} – {downEnd:yyyy-MM-dd HH:mm} ({maintenanceType}) blocks machine capacity.");

        var machineSegments = context.BranchSegments
            .Where(x => string.Equals(
                MachineScheduleAdvancedPlacementEngine.Normalize(x.MachineCode),
                MachineScheduleAdvancedPlacementEngine.Normalize(machineCode),
                StringComparison.Ordinal))
            .ToList();
        var affected = machineSegments
            .Where(x => x.PlannedStartDateTime < downEnd && x.PlannedCompletionDateTime > downStart)
            .ToList();
        var orderIds = affected.Select(x => x.WorkOrderId)
            .Append(plan.TargetOrder.Uid)
            .Distinct()
            .ToList();
        var orders = await LoadOrdersAsync(orderIds, cancellationToken);
        var orderById = orders.ToDictionary(x => x.Uid);

        var capacity = await LoadCapacityAsync(
            [machineCode], machineSegments.Select(x => x.WorkOrderMachineId).ToHashSet(), cancellationToken);
        var occupied = capacity.OccupiedByMachine;
        plan.Warnings.AddRange(capacity.Warnings);

        foreach (var group in affected
                     .GroupBy(x => x.WorkOrderMachineId)
                     .OrderBy(x => x.Key))
        {
            var segments = group.OrderBy(x => x.SegmentSequence).ToList();
            var first = segments[0];
            if (!orderById.TryGetValue(first.WorkOrderId, out var order))
                continue;
            if (!string.Equals(first.BranchCode, plan.TargetOrder.BranchCode, StringComparison.OrdinalIgnoreCase))
            {
                plan.Blockers.Add($"Machine {machineCode} has a reservation from another branch inside this downtime. Other-branch capacity is authoritative and cannot be displaced.");
                continue;
            }
            var operation = order.RouteSteps.SelectMany(x => x.Operations)
                .FirstOrDefault(x => x.Uid == first.WorkOrderOperationId);
            var machine = operation?.Machines.FirstOrDefault(x => x.Uid == first.WorkOrderMachineId && x.IsSelected);
            var step = order.RouteSteps.FirstOrDefault(x => x.Uid == operation?.RouteStepId);
            if (operation is null || machine is null || step is null)
                continue;
            if (HasExecution(operation) || segments.Any(x =>
                    string.Equals(x.SegmentStatus, ProductionMachineScheduleSegmentStatuses.ExecutionLocked, StringComparison.Ordinal)))
            {
                plan.Blockers.Add($"Work Order {order.WorkOrderNo} operation {operation.OperationCode} already has execution. Replan Remaining must be used with reconciliation before maintenance replanning.");
                continue;
            }

            var totalQty = segments.Sum(x => x.PlannedOutputQty);
            var prefixQty = QuantityBeforeSplit(context, first.WorkOrderMachineId, segments, downStart, machine);
            var planned = new List<PlannedSegment>();
            if (prefixQty > 0m && segments.Count > 0)
            {
                var prefixFragments = context.FragmentsBefore(first.WorkOrderMachineId, downStart);
                if (prefixFragments.Count > 0)
                {
                    var prefixPlan = SegmentCalculator(machine, prefixQty);
                    planned.Add(new PlannedSegment
                    {
                        Existing = first,
                        WorkOrderId = order.Uid,
                        WorkOrderNo = order.WorkOrderNo,
                        OperationId = operation.Uid,
                        OperationCode = operation.OperationCode,
                        WorkOrderMachineId = machine.Uid,
                        MachineCode = machine.MachineCode,
                        Kind = first.SegmentKind,
                        Status = ProductionMachineScheduleSegmentStatuses.Planned,
                        SourceSegmentId = first.SourceSegmentId,
                        Qty = prefixQty,
                        Uom = first.OutputUom,
                        CycleCount = prefixPlan.CycleCount,
                        CycleSlots = prefixPlan.CycleSlots,
                        RunMinutes = prefixPlan.RunMinutes,
                        SetupSeconds = first.ScheduledSetupSeconds,
                        ConversionSeconds = first.ScheduledConversionSeconds,
                        QueueSeconds = first.ScheduledQueueSeconds,
                        Start = first.PlannedStartDateTime,
                        Completion = prefixFragments.Max(x => x.End),
                        SourceHash = first.SegmentScheduleSourceHash ?? string.Empty,
                        HorizonStart = first.CalendarHorizonStart,
                        HorizonEnd = first.CalendarHorizonEnd,
                        Fragments = prefixFragments,
                        Reason = "Prefix kept before maintenance downtime."
                    });
                    context.Supersede(segments.Where(x => x.Uid != first.Uid));
                }
                else
                {
                    prefixQty = 0m;
                    context.Supersede(segments);
                }
            }
            else
            {
                prefixQty = 0m;
                context.Supersede(segments);
            }

            var resumeQty = totalQty - prefixQty;
            if (resumeQty > 0m && isOpenEnded)
            {
                // A repair window without an end cannot produce a trustworthy resume date. Keep
                // only the already-known prefix and leave the rest genuinely unscheduled.
                plan.HasUnscheduledRemaining = true;
                plan.Warnings.Add(
                    $"Work Order {order.WorkOrderNo} operation {operation.OperationCode} has "
                    + $"{resumeQty:n4} {operation.PlannedOutputUom} held behind open-ended maintenance. "
                    + "Completion is unknown until the repair end is recorded.");
            }
            else if (resumeQty > 0m)
            {
                var resumePlan = SegmentCalculator(machine, resumeQty);
                var overhead = MachineScheduleSegmentCalculator.RestartOverheadSeconds(
                    machine, plan.Request.IncludeRestartSetup);
                var duration = overhead + TimeSpan.FromMinutes((double)resumePlan.RunMinutes);
                var anchor = downEnd;
                if (plan.Request.ResumeNotBefore is DateTime resumeNotBefore && resumeNotBefore > anchor)
                    anchor = DateTime.SpecifyKind(resumeNotBefore, DateTimeKind.Unspecified);
                var placement = await _engine.PlaceMachineAsync(
                    order.CompanyCode, machine.MachineCode, anchor, duration, occupied, cancellationToken);
                if (!placement.Succeeded)
                {
                    plan.Blockers.Add(placement.Error!);
                    continue;
                }
                occupied[MachineScheduleAdvancedPlacementEngine.Normalize(machine.MachineCode)]
                    .AddRange(placement.Fragments);
                planned.Add(NewSegment(plan, operation, machine, placement, resumeQty,
                    overhead == TimeSpan.Zero ? 0m : machine.SetupSeconds,
                    overhead == TimeSpan.Zero ? 0m : machine.ConversionSeconds, 0m,
                    ProductionMachineScheduleSegmentKinds.MaintenanceResume,
                    "Resume after maintenance downtime.", first.Uid));
            }

            plan.SegmentsByMachine[machine.Uid] = planned;
            plan.AffectedMachineIds.Add(machine.Uid);
            context.AddOrderPlan(order, step, operation);
        }

        if (affected.Count == 0)
        {
            plan.Warnings.Add("No planned segment overlapped the maintenance window. The downtime is still authoritative capacity for future planning.");
            context.AddOrderPlan(plan.TargetOrder, plan.TargetStep, plan.TargetOperation, includeTarget: false);
        }
    }

    private async Task<(DateTime Start, DateTime End, string Type, bool IsOpenEnded)?> LoadDowntimeAsync(
        MachineScheduleAdvancedPlan plan,
        string machineCode,
        CancellationToken cancellationToken)
    {
        if (plan.Request.MaintenanceId is long maintenanceId && maintenanceId > 0
            && maintenanceId <= int.MaxValue)
        {
            var id = (int)maintenanceId;
            var row = await _db.PrMacMaintenances.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id
                    && x.CompCode == plan.TargetOrder.CompanyCode, cancellationToken);
            if (row?.StartDateTime is DateTime start
                && row.Status is not null
                && row.Status != PrMaintenanceStatuses.Cancelled
                && !string.Equals(row.MType, PrMaintenanceTypes.Preventive, StringComparison.OrdinalIgnoreCase))
            {
                var end = row.EndDateTime
                    ?? DateTime.SpecifyKind(
                        start.AddDays(WorkOrderSchedulingHorizon.InitialDays), DateTimeKind.Unspecified);
                return (DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
                    DateTime.SpecifyKind(end, DateTimeKind.Unspecified),
                    row.MType ?? PrMaintenanceTypes.Other,
                    row.EndDateTime is null && row.Status != PrMaintenanceStatuses.Completed);
            }
        }

        if (plan.Request.DowntimeStart is DateTime requestedStart)
        {
            var end = plan.Request.DowntimeEnd
                ?? DateTime.SpecifyKind(
                    requestedStart.AddDays(WorkOrderSchedulingHorizon.InitialDays), DateTimeKind.Unspecified);
            return (DateTime.SpecifyKind(requestedStart, DateTimeKind.Unspecified),
                DateTime.SpecifyKind(end, DateTimeKind.Unspecified),
                plan.Request.MaintenanceType ?? PrMaintenanceTypes.Other,
                plan.Request.DowntimeEnd is null);
        }

        var windows = await MachineDowntimeWindowReader.LoadAsync(
            _db, plan.TargetOrder.CompanyCode, [machineCode],
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddDays(WorkOrderSchedulingHorizon.InitialDays),
            cancellationToken);
        var window = windows.OrderBy(x => x.Start).FirstOrDefault();
        if (window == default)
            return null;
        return (window.Start, window.End, window.MaintenanceType, window.IsOpenEnded);
    }

    private async Task ProjectAsync(PlanContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        await ValidateExecutionEvidenceAsync(plan, cancellationToken);
        if (plan.Blockers.Count > 0)
            return;
        if (plan.HasUnscheduledRemaining)
        {
            // Unknown hold: the last persisted dates remain "last known plan". No new forecast,
            // no fake completion, and no downstream date rewrite.
            foreach (var orderPlan in plan.Orders)
            {
                orderPlan.AfterStart = orderPlan.Before.Start;
                orderPlan.AfterCompletion = orderPlan.Before.Completion;
            }
            return;
        }

        var segmentByOperation = plan.SegmentsByMachine.Values.SelectMany(x => x)
            .GroupBy(x => x.OperationId)
            .ToDictionary(x => x.Key, x => x.OrderBy(s => s.Start).ToList());
        var occupied = new Dictionary<string, List<TimeInterval>>(StringComparer.Ordinal);
        var machineCodes = plan.AffectedMachineIds
            .Select(context.MachineCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (machineCodes.Count > 0)
        {
            var capacity = await LoadCapacityAsync(machineCodes, plan.AffectedMachineIds, cancellationToken);
            plan.Warnings.AddRange(capacity.Warnings);
            foreach (var pair in capacity.OccupiedByMachine)
                occupied[pair.Key] = [.. pair.Value];
        }

        foreach (var orderPlan in plan.Orders.ToList())
        {
            var order = orderPlan.Order;
            var fixedDates = new Dictionary<long, MachineScheduleOperationDates>();
            foreach (var step in order.RouteSteps)
            {
                foreach (var operation in step.Operations)
                {
                    if (plan.TargetHasNoFutureSegment
                        && operation.Uid == plan.TargetOperation.Uid
                        && operation.PlannedStartDateTime is DateTime heldStart
                        && operation.PlannedCompletionDateTime is DateTime heldCompletion)
                    {
                        fixedDates[operation.Uid] = new MachineScheduleOperationDates(
                            operation.Uid, heldStart, heldCompletion);
                        continue;
                    }
                    if (orderPlan.MovingOperationIds.Contains(operation.Uid))
                        continue;
                    if (operation.PlannedStartDateTime is not DateTime start
                        || operation.PlannedCompletionDateTime is not DateTime completion)
                    {
                        plan.Blockers.Add($"Operation {operation.OperationCode} on Work Order {order.WorkOrderNo} has incomplete saved dates.");
                        continue;
                    }
                    fixedDates[operation.Uid] = new MachineScheduleOperationDates(operation.Uid, start, completion);
                }
            }

            var projection = MachineScheduleDateRangeProjector.ProjectForward(
                order,
                order.PlannedStartDateTime,
                (step, operation, earliest) => ResolvePlacement(
                    context, plan, operation, earliest, occupied, segmentByOperation, cancellationToken),
                fixedDates);
            if (!projection.Succeeded)
            {
                plan.Blockers.AddRange(projection.Blockers);
                continue;
            }

            orderPlan.AfterStart = projection.WorkOrderStart;
            orderPlan.AfterCompletion = projection.WorkOrderCompletion;
            context.Projections[order.Uid] = projection;
            plan.Projections[order.Uid] = projection;
        }
    }

    private async Task ValidateExecutionEvidenceAsync(
        MachineScheduleAdvancedPlan plan, CancellationToken cancellationToken)
    {
        var movingIds = plan.Orders.SelectMany(x => x.MovingOperationIds).Distinct().ToList();
        if (movingIds.Count == 0)
            return;

        var outputs = await _db.ProductionOutputs.AsNoTracking()
            .Where(x => movingIds.Contains(x.WorkOrderOperationId)
                && x.DeletedAtUtc == null
                && (x.Status == ProductionOutputStatuses.New
                    || x.Status == ProductionOutputStatuses.Posted
                    || x.PostingId != null))
            .Select(x => new { x.WorkOrderOperationId, x.Status, x.PostingId })
            .ToListAsync(cancellationToken);

        if (outputs.Any(x => string.Equals(x.Status, ProductionOutputStatuses.New, StringComparison.OrdinalIgnoreCase)))
        {
            plan.Blockers.Add("Affected operations have NEW Daily Production drafts. Resolve or post them before an advanced schedule change.");
            return;
        }

        var posted = outputs
            .Where(x => string.Equals(x.Status, ProductionOutputStatuses.Posted, StringComparison.OrdinalIgnoreCase)
                || x.PostingId is not null)
            .Select(x => x.WorkOrderOperationId)
            .Distinct()
            .ToList();
        if (posted.Count == 0)
            return;

        var allowsTargetExecution = plan.Action is MachineScheduleAdvancedAction.Hold
            or MachineScheduleAdvancedAction.ReleaseHold
            or MachineScheduleAdvancedAction.ReplanRemaining;
        var blocked = posted.Where(id => !allowsTargetExecution || id != plan.TargetOperation.Uid).ToList();
        if (blocked.Count > 0)
        {
            var labels = plan.Orders.SelectMany(x => x.Order.RouteSteps).SelectMany(x => x.Operations)
                .Where(x => blocked.Contains(x.Uid))
                .Select(x => x.OperationCode)
                .Distinct()
                .ToList();
            plan.Blockers.Add(
                $"Downstream operation(s) {string.Join(", ", labels)} already have execution history. "
                + "Automatic date propagation stops here; this complex overlap requires schedule reconciliation.");
        }
    }

    private MachineScheduleProjectionPlacement ResolvePlacement(
        PlanContext context,
        MachineScheduleAdvancedPlan plan,
        ProductionWorkOrderOperation operation,
        DateTime earliest,
        Dictionary<string, List<TimeInterval>> occupied,
        Dictionary<long, List<PlannedSegment>> segmentByOperation,
        CancellationToken cancellationToken)
    {
        if (segmentByOperation.TryGetValue(operation.Uid, out var segments) && segments.Count > 0)
            return new MachineScheduleProjectionPlacement(
                segments.Min(x => x.Start), segments.Max(x => x.Completion));

        var machine = PrProcessTypes.SupportsMachine(operation.ProcessType)
            ? operation.Machines.SingleOrDefault(x => x.IsSelected)
            : null;
        if (PrProcessTypes.SupportsMachine(operation.ProcessType) && machine is null)
            return MachineScheduleProjectionPlacement.Failed($"Operation {operation.OperationCode} has no selected machine.");

        var duration = machine is null
            ? TimeSpan.FromMinutes((double)operation.StandardDurationMinutes)
            : TimeSpan.FromSeconds((double)(machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds))
                + TimeSpan.FromMinutes((double)machine.PlannedRunMinutes);
        if (duration <= TimeSpan.Zero)
            return MachineScheduleProjectionPlacement.Failed($"Operation {operation.OperationCode} has no positive schedule duration.");

        // The projector delegate is synchronous by contract. The server has no synchronization
        // context, so completing the async read here is safe and keeps the hierarchy rules in the
        // single pure projector.
        var placement = (machine is null
                ? _engine.PlacePlantAsync(plan.TargetOrder.CompanyCode, plan.TargetOrder.BranchCode,
                    earliest, duration, cancellationToken)
                : _engine.PlaceMachineAsync(plan.TargetOrder.CompanyCode, machine.MachineCode,
                    earliest, duration, occupied, cancellationToken))
            .GetAwaiter().GetResult();
        if (!placement.Succeeded)
            return MachineScheduleProjectionPlacement.Failed($"Operation {operation.OperationCode}: {placement.Error}");

        plan.Placements[operation.Uid] = placement;
        if (machine is null)
            return new MachineScheduleProjectionPlacement(placement.Start, placement.Completion);

        var key = MachineScheduleAdvancedPlacementEngine.Normalize(machine.MachineCode);
        if (!occupied.TryGetValue(key, out var list))
        {
            list = [];
            occupied[key] = list;
        }
        list.AddRange(placement.Fragments);
        plan.AffectedMachineIds.Add(machine.Uid);

        if (!segmentByOperation.ContainsKey(operation.Uid))
        {
            var quantity = operation.RemainingQty > 0m ? operation.RemainingQty : operation.PlannedOutputQty;
            var segment = NewSegment(plan, operation, machine, placement, quantity,
                machine.SetupSeconds, machine.ConversionSeconds, machine.QueueSeconds,
                ProductionMachineScheduleSegmentKinds.Original,
                "Replanned with the advanced schedule.",
                context.SegmentsForMachine(machine.Uid).FirstOrDefault()?.Uid);
            if (!plan.SegmentsByMachine.TryGetValue(machine.Uid, out var listOfSegments))
            {
                listOfSegments = [];
                plan.SegmentsByMachine[machine.Uid] = listOfSegments;
            }
            listOfSegments.Add(segment);
            segmentByOperation[operation.Uid] = [segment];
            context.Supersede(context.SegmentsForMachine(machine.Uid));
        }

        return new MachineScheduleProjectionPlacement(placement.Start, placement.Completion);
    }

    private decimal QuantityBeforeSplit(
        PlanContext context,
        long machineId,
        IReadOnlyList<ProductionMachineScheduleSegment> segments,
        DateTime split,
        ProductionWorkOrderMachine machine)
    {
        if (segments.Count == 0)
            return 0m;
        var first = segments[0];
        if (split <= first.PlannedStartDateTime)
            return 0m;
        var allFragments = context.FragmentsBefore(machineId, DateTime.MaxValue);
        var prefix = context.FragmentsBefore(machineId, split);
        var totalSeconds = (decimal)allFragments.Sum(x => (x.End - x.Start).TotalSeconds);
        var prefixSeconds = (decimal)prefix.Sum(x => (x.End - x.Start).TotalSeconds);
        if (totalSeconds <= 0m || prefixSeconds <= 0m)
            return 0m;
        var totalQty = segments.Sum(x => x.PlannedOutputQty);
        var totalCycles = totalQty <= 0m || machine.OutputPerCycle <= 0m
            ? 0m
            : Math.Ceiling(totalQty / machine.OutputPerCycle);
        var prefixCycles = Math.Floor(totalCycles * prefixSeconds / totalSeconds);
        if (prefixCycles <= 0m)
            return 0m;
        return Math.Max(0m, Math.Min(prefixCycles * machine.OutputPerCycle, totalQty - machine.OutputPerCycle));
    }

    private sealed class PlanContext
    {
        private readonly Dictionary<long, List<ProductionMachineScheduleAllocation>> _allocations;

        public PlanContext(
            MachineScheduleAdvancedPlan plan,
            List<ProductionMachineScheduleSegment> branchSegments,
            Dictionary<long, List<ProductionMachineScheduleAllocation>> allocations)
        {
            Plan = plan;
            BranchSegments = branchSegments;
            _allocations = allocations;
        }

        public MachineScheduleAdvancedPlan Plan { get; }
        public IReadOnlyList<ProductionMachineScheduleSegment> BranchSegments { get; }
        public Dictionary<long, MachineScheduleDateRangeProjection> Projections { get; } = [];

        public List<ProductionMachineScheduleSegment> SegmentsForMachine(long machineId) =>
            BranchSegments.Where(x => x.WorkOrderMachineId == machineId)
                .OrderBy(x => x.SegmentSequence)
                .ToList();

        public string? MachineCode(long machineId) =>
            BranchSegments.FirstOrDefault(x => x.WorkOrderMachineId == machineId)?.MachineCode
            ?? (Plan.TargetMachine.Uid == machineId ? Plan.TargetMachine.MachineCode : null);

        public List<TimeInterval> FragmentsBefore(long machineId, DateTime split)
        {
            if (!_allocations.TryGetValue(machineId, out var rows))
                return [];
            return rows
                .Where(x => x.StartDateTime < split)
                .Select(x => new TimeInterval(
                    x.StartDateTime,
                    x.EndDateTime < split ? x.EndDateTime : split))
                .Where(x => x.End > x.Start)
                .OrderBy(x => x.Start)
                .ToList();
        }

        public void AddOrderPlan(
            ProductionWorkOrder order,
            ProductionWorkOrderRouteStep step,
            ProductionWorkOrderOperation operation,
            bool includeTarget = true)
        {
            var orderPlan = Plan.Orders.FirstOrDefault(x => x.Order.Uid == order.Uid);
            if (orderPlan is null)
            {
                orderPlan = new AdvancedOrderPlan
                {
                    Order = order,
                    TargetStep = step,
                    TargetOperation = operation,
                    Before = (order.PlannedStartDateTime, order.PlannedCompletionDateTime)
                };
                Plan.Orders = Plan.Orders.Append(orderPlan).ToList();
            }

            if (includeTarget)
                orderPlan.MovingOperationIds.Add(operation.Uid);
            foreach (var downstream in GetDownstream(order, step, operation))
                orderPlan.MovingOperationIds.Add(downstream.Uid);
        }

        public void Supersede(IEnumerable<ProductionMachineScheduleSegment> segments)
        {
            foreach (var segment in segments)
            {
                if (Plan.SegmentsToSupersede.All(x => x.Uid != segment.Uid))
                    Plan.SegmentsToSupersede.Add(segment);
            }
        }

        private static IEnumerable<ProductionWorkOrderOperation> GetDownstream(
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
    }
}
