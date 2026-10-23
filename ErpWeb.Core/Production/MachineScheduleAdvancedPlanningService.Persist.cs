using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class MachineScheduleAdvancedPlanningService
{
    private async Task<ProductionAuditEvent> PersistAsync(
        AppDbContext db,
        MachineScheduleAdvancedPlan plan,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var user = MachineScheduleSegmentPersistence.TruncateUser(scope.UserId);
        var machineIds = plan.AffectedMachineIds
            .Concat(plan.SegmentsByMachine.Keys)
            .Concat(plan.SegmentsToSupersede.Select(x => x.WorkOrderMachineId))
            .Distinct()
            .ToList();
        var priorSets = await db.ProductionMachineScheduleSegmentSets
            .Where(x => machineIds.Contains(x.WorkOrderMachineId) && x.IsCurrent)
            .ToListAsync(cancellationToken);
        var priorSetsByMachine = priorSets.ToDictionary(x => x.WorkOrderMachineId);
        MachineScheduleSegmentPersistence.SupersedeSets(priorSets, scope.UserId, now);
        var priorSegments = await db.ProductionMachineScheduleSegments
            .Where(x => machineIds.Contains(x.WorkOrderMachineId)
                && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
            .ToListAsync(cancellationToken);
        MachineScheduleSegmentPersistence.Supersede(
            priorSegments, scope.UserId, now,
            $"Superseded by {plan.Action}: {MachineScheduleSegmentPersistence.TrimReason(plan.Request.Reason)}");
        // The filtered unique index must see the old current sets superseded first.
        await db.SaveChangesAsync(cancellationToken);

        var entities = new Dictionary<PlannedSegment, ProductionMachineScheduleSegment>();
        foreach (var planned in plan.AllSegments)
        {
            var entity = new ProductionMachineScheduleSegment
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                WorkOrderId = planned.WorkOrderId,
                WorkOrderOperationId = planned.OperationId,
                WorkOrderMachineId = planned.WorkOrderMachineId,
                MachineCode = planned.MachineCode,
                CreatedDate = now,
                CreatedBy = user,
                RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(db)
            };
            db.ProductionMachineScheduleSegments.Add(entity);

            entity.WorkOrderId = planned.WorkOrderId;
            entity.WorkOrderOperationId = planned.OperationId;
            entity.WorkOrderMachineId = planned.WorkOrderMachineId;
            entity.MachineCode = planned.MachineCode;
            entity.SegmentKind = planned.Kind;
            entity.SegmentStatus = planned.Status;
            entity.SourceSegmentId = planned.Existing?.Uid ?? planned.SourceSegmentId;
            entity.PlannedOutputQty = planned.Qty;
            entity.OutputUom = planned.Uom;
            entity.PlannedCycleCount = planned.CycleCount;
            entity.PlannedCycleSlots = planned.CycleSlots;
            entity.ScheduledRunMinutes = planned.RunMinutes;
            entity.ScheduledSetupSeconds = planned.SetupSeconds;
            entity.ScheduledConversionSeconds = planned.ConversionSeconds;
            entity.ScheduledQueueSeconds = planned.QueueSeconds;
            entity.PlannedStartDateTime = planned.Start;
            entity.PlannedCompletionDateTime = planned.Completion;
            entity.CalendarHorizonStart = planned.HorizonStart;
            entity.CalendarHorizonEnd = planned.HorizonEnd;
            entity.SegmentScheduleSourceHash = planned.SourceHash;
            entity.Reason = MachineScheduleSegmentPersistence.TrimReason(planned.Reason);
            entity.ModifiedDate = now;
            entity.ModifiedBy = user;
            entities[planned] = entity;
        }

        foreach (var machineGroup in plan.SegmentsByMachine)
        {
            var ordered = machineGroup.Value.OrderBy(x => x.Start).ThenBy(x => x.Kind, StringComparer.Ordinal).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Sequence = i + 1;
                entities[ordered[i]].SegmentSequence = i + 1;
            }
        }

        // Assign IDs before allocations reference them.
        await db.SaveChangesAsync(cancellationToken);

        // Apply the date projection to the tracked aggregates. Dates only: snapshot, quantities,
        // selected machine and costing fields are never touched.
        foreach (var orderPlan in plan.Orders)
        {
            if (plan.Projections.TryGetValue(orderPlan.Order.Uid, out var projection))
                MachineScheduleDateRangeProjector.Apply(orderPlan.Order, projection);
        }

        var machines = machineIds.Count == 0
            ? new List<ProductionWorkOrderMachine>()
            : await db.ProductionWorkOrderMachines
                .Include(x => x.Operation)
                .ThenInclude(x => x!.WorkOrder)
                .Where(x => machineIds.Contains(x.Uid))
                .ToListAsync(cancellationToken);
        var machineById = machines.ToDictionary(x => x.Uid);
        var changedMachineIds = new HashSet<long>();
        var setByMachine = new Dictionary<long, ProductionMachineScheduleSegmentSet>();
        var allocationLinks = new List<(ProductionMachineScheduleAllocation Allocation,
            ProductionMachineScheduleSegment Segment)>();

        foreach (var machineId in machineIds)
        {
            if (!machineById.TryGetValue(machineId, out var machine))
                continue;
            var machinePlanned = plan.SegmentsByMachine.TryGetValue(machineId, out var plannedSegments)
                ? plannedSegments.OrderBy(x => x.Start).ToList()
                : [];
            var segmentEntities = machinePlanned.Select(x => entities[x]).ToList();
            var oldMachineStart = machine.PlannedStartDateTime;
            machine.ScheduleRevision++;
            changedMachineIds.Add(machineId);

            if (segmentEntities.Count > 0 && !plan.HasUnscheduledRemaining)
            {
                var start = segmentEntities.Min(x => x.PlannedStartDateTime);
                var completion = segmentEntities.Max(x => x.PlannedCompletionDateTime);
                var executed = machine.Operation is not null
                    && (machine.Operation.GoodQty != 0m || machine.Operation.ProcessedQty != 0m
                        || machine.Operation.ScrapQty != 0m || machine.Operation.RejectQty != 0m
                        || machine.Operation.HoldQty != 0m || machine.Operation.InputQty != 0m
                        || machine.Operation.TransferredQty != 0m);
                if (executed && oldMachineStart is DateTime oldStart && oldStart < start)
                    start = oldStart;

                var horizonStart = segmentEntities.Min(x => x.CalendarHorizonStart)
                    ?? start.Date.AddDays(-1);
                var horizonEnd = segmentEntities.Max(x => x.CalendarHorizonEnd)
                    ?? completion.Date.AddDays(1);
                var sourceHash = await ComputeUnionHashAsync(
                    scope.CompanyCode, machine.MachineCode,
                    horizonStart, horizonEnd, cancellationToken);

                machine.PlannedStartDateTime = start;
                machine.PlannedCompletionDateTime = completion;
                machine.CalendarHorizonStart = horizonStart;
                machine.CalendarHorizonEnd = horizonEnd;
                machine.ScheduleSourceHash = sourceHash;

                if (machine.Operation is not null)
                {
                    machine.Operation.PlannedStartDateTime = start;
                    machine.Operation.PlannedCompletionDateTime = completion;
                    machine.Operation.CalendarSourceType = ProductionCalendarSourceTypes.Machine;
                    machine.Operation.CalendarSourceId = machine.CalendarSourceId;
                    machine.Operation.CalendarSourceLastModified = machine.CalendarSourceLastModified;
                    machine.Operation.CalendarHorizonStart = horizonStart;
                    machine.Operation.CalendarHorizonEnd = horizonEnd;
                    machine.Operation.ScheduleSourceHash = sourceHash;
                    machine.Operation.ModifiedDate = now;
                    machine.Operation.ModifiedBy = user;
                }
            }

            if (segmentEntities.Count > 0)
            {
                foreach (var segment in segmentEntities)
                {
                    segment.MachineScheduleRevision = machine.ScheduleRevision;
                    segment.ModifiedDate = now;
                    segment.ModifiedBy = user;
                }
            }

            machine.ModifiedDate = now;
            machine.ModifiedBy = user;

            var operation = machine.Operation
                ?? throw new InvalidOperationException("Affected machine has no Work Order operation.");
            var order = operation.WorkOrder
                ?? throw new InvalidOperationException("Affected machine has no Work Order.");
            var mode = plan.Action == MachineScheduleAdvancedAction.ReplanRemaining
                ? ProductionMachineScheduleSegmentSetModes.RemainingOnly
                : priorSetsByMachine.TryGetValue(machineId, out var previousSet)
                    ? previousSet.Mode
                    : ProductionMachineScheduleSegmentSetModes.FullPlan;
            var planningState = plan.HasUnscheduledRemaining && machineId == plan.TargetMachine.Uid
                ? plan.Action == MachineScheduleAdvancedAction.Hold
                    ? ProductionMachineSchedulePlanningStates.HeldUnscheduled
                    : plan.Action == MachineScheduleAdvancedAction.MaintenanceReplan
                        ? ProductionMachineSchedulePlanningStates.MaintenanceUnscheduled
                        : ProductionMachineSchedulePlanningStates.ReplanRequired
                : segmentEntities.Count == 0 && operation.RemainingQty <= 0m
                    ? ProductionMachineSchedulePlanningStates.Completed
                    : ProductionMachineSchedulePlanningStates.Scheduled;
            var set = MachineScheduleSegmentPersistence.BuildCurrentSet(
                order, operation, machine, mode, planningState,
                segmentEntities.Sum(x => x.PlannedOutputQty),
                plan.Request.Reason, scope.UserId, now, db);
            db.ProductionMachineScheduleSegmentSets.Add(set);
            setByMachine[machineId] = set;

            var existingAllocations = await db.ProductionMachineScheduleAllocations
                .Where(x => x.WorkOrderMachineId == machineId)
                .ToListAsync(cancellationToken);
            await MachineScheduleSegmentPersistence.RemoveAllocationLinksAsync(
                db, existingAllocations, cancellationToken);
            db.ProductionMachineScheduleAllocations.RemoveRange(existingAllocations);

            var fragmentNo = 1;
            var createdBy = user;
            foreach (var planned in machinePlanned)
            {
                var segment = entities[planned];
                foreach (var interval in planned.Fragments)
                {
                    var allocation = new ProductionMachineScheduleAllocation
                    {
                        CompanyCode = segment.CompanyCode,
                        BranchCode = segment.BranchCode,
                        MachineCode = segment.MachineCode,
                        WorkOrderId = segment.WorkOrderId,
                        WorkOrderOperationId = segment.WorkOrderOperationId,
                        WorkOrderMachineId = segment.WorkOrderMachineId,
                        MachineScheduleSegmentId = segment.Uid,
                        ScheduleRevision = machine.ScheduleRevision,
                        FragmentNo = fragmentNo++,
                        StartDateTime = interval.Start,
                        EndDateTime = interval.End,
                        ScheduleSourceHash = machine.ScheduleSourceHash,
                        CreatedDate = now,
                        CreatedBy = createdBy,
                        RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(db)
                    };
                    db.ProductionMachineScheduleAllocations.Add(allocation);
                    allocationLinks.Add((allocation, segment));
                }
            }
        }

        // Assign database identities before attaching each segment to its set and each
        // productive allocation to its segment. The caller's transaction remains open.
        await db.SaveChangesAsync(cancellationToken);
        foreach (var pair in entities)
            pair.Value.SegmentSetId = setByMachine[pair.Key.WorkOrderMachineId].Uid;
        foreach (var (allocation, segment) in allocationLinks)
            MachineScheduleSegmentPersistence.AddAllocationLink(
                db, allocation, segment, scope.UserId, now);

        // Holds are planning rows. The Work Order status never changes.
        foreach (var hold in plan.HoldsToAdd)
        {
            db.ProductionWorkOrderScheduleHolds.Add(hold);
        }
        if (plan.HoldToResolve is not null)
        {
            var resolved = await db.ProductionWorkOrderScheduleHolds
                .FirstOrDefaultAsync(x => x.Uid == plan.HoldToResolve.Uid, cancellationToken);
            if (resolved is not null)
            {
                resolved.Status = plan.HoldResolutionKind ?? ProductionScheduleHoldStatuses.Released;
                resolved.ReleasedDate = now;
                resolved.ReleasedBy = user;
                resolved.ResolutionReason = MachineScheduleSegmentPersistence.TrimReason(plan.Request.Reason);
                resolved.RowVersion = MachineScheduleSegmentPersistence.NewRowVersion(db);
            }
        }

        foreach (var orderPlan in plan.Orders)
        {
            var order = orderPlan.Order;
            order.ScheduleRevision++;
            order.ModifiedDate = now;
            order.ModifiedBy = user;
            if (plan.HasUnscheduledRemaining)
                orderPlan.HasUnscheduledRemaining = true;
        }

        // Recompute route/Work Order bounds from the persisted operation dates. This is the same
        // hierarchy contract as the projector, expressed as a final consistency pass.
        foreach (var orderPlan in plan.Orders)
        {
            foreach (var step in orderPlan.Order.RouteSteps)
            {
                var starts = step.Operations.Where(x => x.PlannedStartDateTime is not null)
                    .Select(x => x.PlannedStartDateTime!.Value).ToList();
                var completions = step.Operations.Where(x => x.PlannedCompletionDateTime is not null)
                    .Select(x => x.PlannedCompletionDateTime!.Value).ToList();
                if (starts.Count > 0)
                    step.PlannedStartDateTime = starts.Min();
                if (completions.Count > 0)
                    step.PlannedCompletionDateTime = completions.Max();
                step.ModifiedDate = now;
                step.ModifiedBy = user;
            }
            var orderStarts = orderPlan.Order.RouteSteps.Where(x => x.PlannedStartDateTime is not null)
                .Select(x => x.PlannedStartDateTime!.Value).ToList();
            var orderCompletions = orderPlan.Order.RouteSteps.Where(x => x.PlannedCompletionDateTime is not null)
                .Select(x => x.PlannedCompletionDateTime!.Value).ToList();
            if (orderStarts.Count > 0)
                orderPlan.Order.PlannedStartDateTime = orderStarts.Min();
            if (orderCompletions.Count > 0)
                orderPlan.Order.PlannedCompletionDateTime = orderCompletions.Max();
            orderPlan.AfterStart = orderPlan.Order.PlannedStartDateTime;
            orderPlan.AfterCompletion = orderPlan.Order.PlannedCompletionDateTime;

            if (!db.Database.IsSqlServer())
            {
                orderPlan.Order.RowVersion = Guid.NewGuid().ToByteArray();
                foreach (var step in orderPlan.Order.RouteSteps)
                {
                    step.RowVersion = Guid.NewGuid().ToByteArray();
                    foreach (var operation in step.Operations)
                    {
                        operation.RowVersion = Guid.NewGuid().ToByteArray();
                        foreach (var machine in operation.Machines.Where(x => changedMachineIds.Contains(x.Uid)))
                            machine.RowVersion = Guid.NewGuid().ToByteArray();
                    }
                }
            }
        }

        if (!db.Database.IsSqlServer())
        {
            foreach (var hold in plan.HoldsToAdd)
                hold.RowVersion = Guid.NewGuid().ToByteArray();
        }

        var audit = new ProductionAuditEvent
        {
            WorkOrderId = plan.TargetOrder.Uid,
            EventType = plan.EventType,
            FromStatus = plan.TargetOrder.Status,
            ToStatus = plan.TargetOrder.Status,
            SnapshotRevision = plan.TargetOrder.SnapshotRevision,
            Reason = MachineScheduleSegmentPersistence.TrimReason(plan.Request.Reason),
            DetailsJson = JsonSerializer.Serialize(new
            {
                plan.Action,
                plan.Request.PreviewToken,
                plan.RequestedSplit,
                plan.ResolvedSplit,
                plan.SplitExplanation,
                plan.RequestedQty,
                plan.ScheduledQty,
                plan.RestartOverheadSeconds,
                plan.HasUnscheduledRemaining,
                AffectedWorkOrderIds = plan.AffectedWorkOrderIds,
                Segments = plan.AllSegments.Select(x => new
                {
                    x.WorkOrderId,
                    x.OperationId,
                    x.WorkOrderMachineId,
                    x.Kind,
                    x.Qty,
                    x.CycleCount,
                    x.SetupSeconds,
                    x.ConversionSeconds,
                    x.QueueSeconds,
                    x.Start,
                    x.Completion,
                    x.Reason
                }).ToList(),
                WorkOrders = plan.Orders.Select(x => new
                {
                    x.Order.Uid,
                    x.Order.WorkOrderNo,
                    Before = new { x.Before.Start, x.Before.Completion },
                    After = new { Start = x.AfterStart, Completion = x.AfterCompletion }
                }).ToList()
            }),
            OccurredDate = now,
            ActorUserId = scope.UserId
        };
        db.ProductionAuditEvents.Add(audit);
        return audit;
    }

    private async Task<string> ComputeUnionHashAsync(
        string companyCode,
        string machineCode,
        DateTime horizonStart,
        DateTime horizonEnd,
        CancellationToken cancellationToken)
    {
        var start = DateOnly.FromDateTime(horizonStart).AddDays(-1);
        var end = DateOnly.FromDateTime(horizonEnd).AddDays(1);
        var data = await _machineCalendars.LoadAsync(companyCode, machineCode, start, end, cancellationToken);
        return WorkOrderScheduleSourceHasher.Compute(data, start, end);
    }
}
