using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed partial class MachineScheduleAdvancedPlanBuilder
{
    private async Task BuildInsertAsync(PlanContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        if (HasExecution(plan.TargetOperation))
        {
            plan.Blockers.Add("The target operation already has production execution. Use Replan Remaining instead of inserting it again.");
            return;
        }

        var targetSegments = context.SegmentsForMachine(plan.TargetMachine.Uid);
        var scheduledQty = targetSegments.Count > 0
            ? targetSegments.Sum(x => x.PlannedOutputQty)
            : RemainingQuantity(plan.TargetOperation);
        if (scheduledQty <= 0m)
        {
            plan.Blockers.Add("The target operation has no scheduled quantity to insert.");
            return;
        }

        var qty = plan.Request.PartialQuantity is decimal partial && partial > 0m
            ? Math.Min(partial, scheduledQty)
            : scheduledQty;
        plan.RequestedQty = qty;

        var victimSegments = context.BranchSegments
            .Where(x => string.Equals(
                    MachineScheduleAdvancedPlacementEngine.Normalize(x.MachineCode),
                    MachineScheduleAdvancedPlacementEngine.Normalize(plan.TargetMachine.MachineCode),
                    StringComparison.Ordinal)
                && x.WorkOrderMachineId != plan.TargetMachine.Uid)
            .ToList();
        var victimOrders = await LoadOrdersAsync(
            victimSegments.Select(x => x.WorkOrderId).Distinct().ToList(), cancellationToken);
        var victimOrderById = victimOrders.ToDictionary(x => x.Uid);
        var excluded = victimSegments.Select(x => x.WorkOrderMachineId)
            .Append(plan.TargetMachine.Uid)
            .ToHashSet();
        var capacity = await LoadCapacityAsync([plan.TargetMachine.MachineCode], excluded, cancellationToken);
        plan.Warnings.AddRange(capacity.Warnings);
        var occupied = capacity.OccupiedByMachine;

        var quantityPlan = await SegmentCalculatorAsync(plan.TargetOperation, plan.TargetMachine, qty, cancellationToken);
        var requested = DateTime.SpecifyKind(
            plan.Request.RequestedStart ?? plan.TargetOrder.PlannedStartDateTime, DateTimeKind.Unspecified);
        var targetPlacement = await _engine.PlaceMachineAsync(
            plan.TargetOrder.CompanyCode, plan.TargetMachine.MachineCode, requested,
            FirstSegmentDuration(plan.TargetMachine, quantityPlan.RunMinutes), occupied, cancellationToken);
        if (!targetPlacement.Succeeded)
        {
            plan.Blockers.Add(targetPlacement.Error!);
            return;
        }
        plan.ResolvedSplit = targetPlacement.Start;
        plan.SplitExplanation = targetPlacement.Start == requested
            ? $"Insertion starts at the requested time {requested:yyyy-MM-dd HH:mm}."
            : $"Insertion starts at the first available productive time {targetPlacement.Start:yyyy-MM-dd HH:mm}.";
        occupied[MachineScheduleAdvancedPlacementEngine.Normalize(plan.TargetMachine.MachineCode)]
            .AddRange(targetPlacement.Fragments);

        var planned = new List<PlannedSegment>
        {
            await NewSegmentAsync(plan, plan.TargetOperation, plan.TargetMachine, targetPlacement, qty,
                plan.TargetMachine.SetupSeconds, plan.TargetMachine.ConversionSeconds,
                plan.TargetMachine.QueueSeconds, ProductionMachineScheduleSegmentKinds.UrgentInsert,
                string.IsNullOrWhiteSpace(plan.Request.Reason) ? "Urgent insertion / split." : plan.Request.Reason,
                targetSegments.FirstOrDefault()?.Uid, cancellationToken)
        };

        if (qty < scheduledQty)
        {
            var remainderQty = scheduledQty - qty;
            var remainderPlan = await SegmentCalculatorAsync(plan.TargetOperation, plan.TargetMachine, remainderQty, cancellationToken);
            var remainderAnchor = targetSegments.Count > 0
                ? targetSegments.Max(x => x.PlannedCompletionDateTime)
                : targetPlacement.Completion;
            if (remainderAnchor < targetPlacement.Completion)
                remainderAnchor = targetPlacement.Completion;
            var remainderPlacement = await _engine.PlaceMachineAsync(
                plan.TargetOrder.CompanyCode, plan.TargetMachine.MachineCode, remainderAnchor,
                FirstSegmentDuration(plan.TargetMachine, remainderPlan.RunMinutes), occupied, cancellationToken);
            if (!remainderPlacement.Succeeded)
            {
                plan.Blockers.Add(remainderPlacement.Error!);
                return;
            }
            occupied[MachineScheduleAdvancedPlacementEngine.Normalize(plan.TargetMachine.MachineCode)]
                .AddRange(remainderPlacement.Fragments);
            planned.Add(await NewSegmentAsync(plan, plan.TargetOperation, plan.TargetMachine, remainderPlacement,
                remainderQty, plan.TargetMachine.SetupSeconds, plan.TargetMachine.ConversionSeconds,
                plan.TargetMachine.QueueSeconds, ProductionMachineScheduleSegmentKinds.ManualSplit,
                "Partial insertion; remaining quantity stays planned later.", targetSegments.FirstOrDefault()?.Uid,
                cancellationToken));
            plan.Warnings.Add("Partial quantity inserted early. The remaining quantity stays in its later plan, so downstream operations do not start early.");
        }

        context.Supersede(targetSegments);
        plan.SegmentsByMachine[plan.TargetMachine.Uid] = planned;
        plan.AffectedMachineIds.Add(plan.TargetMachine.Uid);
        context.AddOrderPlan(plan.TargetOrder, plan.TargetStep, plan.TargetOperation);

        plan.RestartOverheadSeconds = plan.Request.IncludeRestartSetup
            ? plan.TargetMachine.SetupSeconds + plan.TargetMachine.ConversionSeconds
            : 0m;

        var victims = victimSegments
            .Where(x => x.PlannedStartDateTime < targetPlacement.Completion
                && x.PlannedCompletionDateTime > targetPlacement.Start)
            .OrderBy(x => x.PlannedStartDateTime)
            .ThenBy(x => x.Uid)
            .ToList();
        await SplitVictimsAsync(context, victims, victimOrderById, targetPlacement, occupied, cancellationToken);
        plan.ScheduledQty = qty;
        plan.EventType = ProductionAuditEventTypes.ScheduleJobInserted;
    }

    private async Task SplitVictimsAsync(
        PlanContext context,
        IReadOnlyList<ProductionMachineScheduleSegment> victims,
        IReadOnlyDictionary<long, ProductionWorkOrder> victimOrderById,
        MachineSchedulePlacement targetPlacement,
        Dictionary<string, List<TimeInterval>> occupied,
        CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        foreach (var group in victims.GroupBy(x => x.WorkOrderMachineId))
        {
            var segments = group.OrderBy(x => x.SegmentSequence).ToList();
            var first = segments[0];
            if (!victimOrderById.TryGetValue(first.WorkOrderId, out var order))
                continue;
            var operation = order.RouteSteps.SelectMany(x => x.Operations)
                .FirstOrDefault(x => x.Uid == first.WorkOrderOperationId);
            var machine = operation?.Machines.FirstOrDefault(x => x.Uid == first.WorkOrderMachineId && x.IsSelected);
            var step = order.RouteSteps.FirstOrDefault(x => x.Uid == operation?.RouteStepId);
            if (operation is null || machine is null || step is null)
            {
                plan.Blockers.Add($"Victim Work Order {order.WorkOrderNo} no longer has a valid selected machine operation.");
                continue;
            }
            if (HasExecution(operation))
            {
                plan.Blockers.Add($"Downstream Work Order {order.WorkOrderNo} already has execution history. Automatic date propagation stops here; this overlap requires schedule reconciliation.");
                continue;
            }

            context.AddOrderPlan(order, step, operation);
            var totalQty = segments.Sum(x => x.PlannedOutputQty);
            var (prefixQty, prefixBoundary) = await QuantityBeforeSplitAsync(context, first.WorkOrderMachineId,
                segments, targetPlacement.Start, operation, machine, cancellationToken);
            var planned = new List<PlannedSegment>();
            if (prefixQty > 0m)
            {
                var prefixFragments = context.FragmentsBefore(first.WorkOrderMachineId, prefixBoundary);
                if (prefixFragments.Count > 0)
                {
                    var prefixPlan = await SegmentCalculatorAsync(operation, machine, prefixQty, cancellationToken);
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
                        Reason = "Prefix kept before the inserted job."
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
                context.Supersede(segments);
            }

            var resumeQty = totalQty - prefixQty;
            if (resumeQty > 0m)
            {
                var resumePlan = await SegmentCalculatorAsync(operation, machine, resumeQty, cancellationToken);
                var overhead = MachineScheduleSegmentCalculator.RestartOverheadSeconds(
                    machine, plan.Request.IncludeRestartSetup);
                var duration = overhead + TimeSpan.FromMinutes((double)resumePlan.RunMinutes);
                var placement = await _engine.PlaceMachineAsync(
                    order.CompanyCode, machine.MachineCode, targetPlacement.Completion,
                    duration, occupied, cancellationToken);
                if (!placement.Succeeded)
                {
                    plan.Blockers.Add(placement.Error!);
                    continue;
                }
                occupied[MachineScheduleAdvancedPlacementEngine.Normalize(machine.MachineCode)]
                    .AddRange(placement.Fragments);
                planned.Add(await NewSegmentAsync(plan, operation, machine, placement, resumeQty,
                    overhead == TimeSpan.Zero ? 0m : machine.SetupSeconds,
                    overhead == TimeSpan.Zero ? 0m : machine.ConversionSeconds, 0m,
                    ProductionMachineScheduleSegmentKinds.Resume,
                    "Resume after an urgent insertion.", first.Uid, cancellationToken));
            }

            plan.SegmentsByMachine[machine.Uid] = planned;
            plan.AffectedMachineIds.Add(machine.Uid);
        }
    }

    private async Task BuildHoldAsync(PlanContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        if (!ProductionScheduleHoldTypes.IsKnown(plan.Request.HoldType))
        {
            plan.Blockers.Add("Choose a valid hold type.");
            return;
        }

        var segments = context.SegmentsForMachine(plan.TargetMachine.Uid);
        var qty = RemainingQuantity(plan.TargetOperation);
        if (qty <= 0m)
        {
            plan.Blockers.Add("The target operation has no remaining quantity to hold.");
            return;
        }

        var holdFrom = DateTime.SpecifyKind(
            plan.Request.RequestedStart ?? plan.TargetMachine.PlannedStartDateTime ?? DateTime.Now,
            DateTimeKind.Unspecified);
        plan.RequestedSplit = holdFrom;
        plan.ResolvedSplit = holdFrom;
        plan.RequestedQty = qty;

        var capacity = await LoadCapacityAsync(
            [plan.TargetMachine.MachineCode], new HashSet<long> { plan.TargetMachine.Uid }, cancellationToken);
        var occupied = capacity.OccupiedByMachine;
        plan.Warnings.AddRange(capacity.Warnings);

        var (prefixQty, resolvedHoldFrom) = await QuantityBeforeSplitAsync(
            context, plan.TargetMachine.Uid, segments, holdFrom,
            plan.TargetOperation, plan.TargetMachine, cancellationToken);
        if (resolvedHoldFrom != holdFrom)
            plan.Warnings.Add($"Hold boundary moved to the last completed productive cycle at {resolvedHoldFrom:yyyy-MM-dd HH:mm}.");
        holdFrom = resolvedHoldFrom;
        plan.ResolvedSplit = holdFrom;
        var planned = new List<PlannedSegment>();
        if (prefixQty > 0m && segments.Count > 0)
        {
            var prefixFragments = context.FragmentsBefore(plan.TargetMachine.Uid, holdFrom);
            if (prefixFragments.Count > 0)
            {
                var first = segments[0];
                var prefixPlan = await SegmentCalculatorAsync(plan.TargetOperation, plan.TargetMachine, prefixQty, cancellationToken);
                planned.Add(new PlannedSegment
                {
                    Existing = first,
                    WorkOrderId = plan.TargetOrder.Uid,
                    WorkOrderNo = plan.TargetOrder.WorkOrderNo,
                    OperationId = plan.TargetOperation.Uid,
                    OperationCode = plan.TargetOperation.OperationCode,
                    WorkOrderMachineId = plan.TargetMachine.Uid,
                    MachineCode = plan.TargetMachine.MachineCode,
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
                    Reason = "Prefix completed before the hold."
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

        var heldQty = qty - prefixQty;
        plan.AffectedMachineIds.Add(plan.TargetMachine.Uid);
        context.AddOrderPlan(plan.TargetOrder, plan.TargetStep, plan.TargetOperation);
        plan.SegmentsByMachine[plan.TargetMachine.Uid] = planned;

        var resumeAt = plan.Request.ResumeNotBefore is DateTime resume
            ? DateTime.SpecifyKind(resume, DateTimeKind.Unspecified)
            : (DateTime?)null;
        plan.HoldsToAdd.Add(NewHold(plan, plan.Request.HoldType!, holdFrom, resumeAt));
        plan.EventType = ProductionAuditEventTypes.ScheduleHoldApplied;

        if (resumeAt is null)
        {
            plan.HasUnscheduledRemaining = true;
            plan.ForecastCompletion = null;
            plan.ScheduledQty = prefixQty;
            plan.Warnings.Add("Completion unknown — remaining quantity is held and not scheduled.");
            return;
        }

        var resumePlan = await SegmentCalculatorAsync(plan.TargetOperation, plan.TargetMachine, heldQty, cancellationToken);
        var overhead = MachineScheduleSegmentCalculator.RestartOverheadSeconds(
            plan.TargetMachine, plan.Request.IncludeRestartSetup);
        var duration = overhead + TimeSpan.FromMinutes((double)resumePlan.RunMinutes);
        var placement = await _engine.PlaceMachineAsync(
            plan.TargetOrder.CompanyCode, plan.TargetMachine.MachineCode, resumeAt.Value,
            duration, occupied, cancellationToken);
        if (!placement.Succeeded)
        {
            plan.Blockers.Add(placement.Error!);
            return;
        }
        occupied[MachineScheduleAdvancedPlacementEngine.Normalize(plan.TargetMachine.MachineCode)]
            .AddRange(placement.Fragments);
        planned.Add(await NewSegmentAsync(plan, plan.TargetOperation, plan.TargetMachine, placement, heldQty,
            overhead == TimeSpan.Zero ? 0m : plan.TargetMachine.SetupSeconds,
            overhead == TimeSpan.Zero ? 0m : plan.TargetMachine.ConversionSeconds, 0m,
            ProductionMachineScheduleSegmentKinds.HoldResume,
            "Known resume date after hold.", segments.FirstOrDefault()?.Uid, cancellationToken));
        plan.SegmentsByMachine[plan.TargetMachine.Uid] = planned;
        plan.ScheduledQty = qty;
        plan.ResolvedSplit = placement.Start;
    }

    private async Task BuildReleaseHoldAsync(PlanContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        var hold = await _db.ProductionWorkOrderScheduleHolds
            .Where(x => x.CompanyCode == plan.TargetOrder.CompanyCode
                && x.BranchCode == plan.TargetOrder.BranchCode
                && x.WorkOrderId == plan.TargetOrder.Uid
                && x.WorkOrderMachineId == plan.TargetMachine.Uid
                && x.Status == ProductionScheduleHoldStatuses.Active
                && (plan.Request.HoldId == null || x.Uid == plan.Request.HoldId))
            .OrderByDescending(x => x.Uid)
            .FirstOrDefaultAsync(cancellationToken);
        if (hold is null)
        {
            plan.Blockers.Add("No active schedule hold was found for this operation.");
            return;
        }

        plan.HoldToResolve = hold;
        plan.HoldResolutionKind = ProductionScheduleHoldStatuses.Released;
        plan.EventType = ProductionAuditEventTypes.ScheduleHoldReleased;
        var qty = RemainingQuantity(plan.TargetOperation);
        if (qty <= 0m)
        {
            plan.Blockers.Add("The target operation has no remaining quantity to resume.");
            return;
        }
        plan.RequestedQty = qty;

        var capacity = await LoadCapacityAsync(
            [plan.TargetMachine.MachineCode], new HashSet<long> { plan.TargetMachine.Uid }, cancellationToken);
        var occupied = capacity.OccupiedByMachine;
        var anchor = DateTime.SpecifyKind(
            plan.Request.RequestedStart ?? plan.Request.ResumeNotBefore ?? hold.ResumeNotBefore ?? DateTime.Now,
            DateTimeKind.Unspecified);
        var qtyPlan = await SegmentCalculatorAsync(plan.TargetOperation, plan.TargetMachine, qty, cancellationToken);
        var overhead = MachineScheduleSegmentCalculator.RestartOverheadSeconds(
            plan.TargetMachine, plan.Request.IncludeRestartSetup);
        var duration = overhead + TimeSpan.FromMinutes((double)qtyPlan.RunMinutes);
        var placement = await _engine.PlaceMachineAsync(
            plan.TargetOrder.CompanyCode, plan.TargetMachine.MachineCode, anchor,
            duration, occupied, cancellationToken);
        if (!placement.Succeeded)
        {
            plan.Blockers.Add(placement.Error!);
            return;
        }
        occupied[MachineScheduleAdvancedPlacementEngine.Normalize(plan.TargetMachine.MachineCode)]
            .AddRange(placement.Fragments);

        var current = context.SegmentsForMachine(plan.TargetMachine.Uid);
        context.Supersede(current);
        plan.SegmentsByMachine[plan.TargetMachine.Uid] =
        [
            await NewSegmentAsync(plan, plan.TargetOperation, plan.TargetMachine, placement, qty,
                overhead == TimeSpan.Zero ? 0m : plan.TargetMachine.SetupSeconds,
                overhead == TimeSpan.Zero ? 0m : plan.TargetMachine.ConversionSeconds, 0m,
                ProductionMachineScheduleSegmentKinds.HoldResume,
                "Hold released; remaining quantity resumed.", current.FirstOrDefault()?.Uid,
                cancellationToken)
        ];
        plan.AffectedMachineIds.Add(plan.TargetMachine.Uid);
        plan.ScheduledQty = qty;
        plan.RequestedSplit = anchor;
        plan.ResolvedSplit = placement.Start;
        context.AddOrderPlan(plan.TargetOrder, plan.TargetStep, plan.TargetOperation);
    }

    private async Task BuildReplanRemainingAsync(PlanContext context, CancellationToken cancellationToken)
    {
        var plan = context.Plan;
        var remaining = RemainingQuantity(plan.TargetOperation);
        plan.RequestedQty = remaining;
        plan.EventType = ProductionAuditEventTypes.ScheduleRemainingReplanned;
        if (!HasExecution(plan.TargetOperation))
            plan.Warnings.Add("This operation has no posted execution yet; Replan Remaining plans the full remaining quantity.");

        var capacity = await LoadCapacityAsync(
            [plan.TargetMachine.MachineCode], new HashSet<long> { plan.TargetMachine.Uid }, cancellationToken);
        var occupied = capacity.OccupiedByMachine;
        var current = context.SegmentsForMachine(plan.TargetMachine.Uid);
        context.Supersede(current);
        context.AddOrderPlan(plan.TargetOrder, plan.TargetStep, plan.TargetOperation);
        plan.AffectedMachineIds.Add(plan.TargetMachine.Uid);

        if (remaining <= 0m)
        {
            plan.SegmentsByMachine[plan.TargetMachine.Uid] = [];
            plan.ScheduledQty = 0m;
            plan.TargetHasNoFutureSegment = true;
            plan.Warnings.Add("No remaining quantity: future capacity was released. Posted production history is unchanged.");
            return;
        }

        var anchor = DateTime.SpecifyKind(
            plan.Request.RequestedStart ?? DateTime.Now, DateTimeKind.Unspecified);
        if (plan.TargetMachine.PlannedStartDateTime is DateTime historical && anchor < historical)
            anchor = historical;
        var qtyPlan = await SegmentCalculatorAsync(plan.TargetOperation, plan.TargetMachine, remaining, cancellationToken);
        var overhead = MachineScheduleSegmentCalculator.RestartOverheadSeconds(
            plan.TargetMachine, plan.Request.IncludeRestartSetup);
        var duration = overhead + TimeSpan.FromMinutes((double)qtyPlan.RunMinutes);
        var placement = await _engine.PlaceMachineAsync(
            plan.TargetOrder.CompanyCode, plan.TargetMachine.MachineCode, anchor,
            duration, occupied, cancellationToken);
        if (!placement.Succeeded)
        {
            plan.Blockers.Add(placement.Error!);
            return;
        }
        occupied[MachineScheduleAdvancedPlacementEngine.Normalize(plan.TargetMachine.MachineCode)]
            .AddRange(placement.Fragments);
        plan.SegmentsByMachine[plan.TargetMachine.Uid] =
        [
            await NewSegmentAsync(plan, plan.TargetOperation, plan.TargetMachine, placement, remaining,
                overhead == TimeSpan.Zero ? 0m : plan.TargetMachine.SetupSeconds,
                overhead == TimeSpan.Zero ? 0m : plan.TargetMachine.ConversionSeconds, 0m,
                ProductionMachineScheduleSegmentKinds.RemainingReplan,
                "Remaining unexecuted quantity replanned.", current.FirstOrDefault()?.Uid,
                cancellationToken)
        ];
        plan.ScheduledQty = remaining;
        plan.RequestedSplit = anchor;
        plan.ResolvedSplit = placement.Start;
    }
}
