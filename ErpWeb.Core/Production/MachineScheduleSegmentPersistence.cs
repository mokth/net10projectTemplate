using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Small, shared write helpers for business schedule segments. Everything here is planning data:
/// no production quantity, stock, WIP, FG, or costing field is touched.
/// </summary>
public static class MachineScheduleSegmentPersistence
{
    public const int MaxAffectedWorkOrders = 20;
    public const int MaxAffectedSegments = 100;

    public static string TruncateUser(string userId) =>
        userId.Length > 10 ? userId[..10] : userId;

    public static byte[] NewRowVersion(AppDbContext db) =>
        db.Database.IsSqlServer() ? [] : Guid.NewGuid().ToByteArray();

    /// <summary>
    /// Builds the release-time <c>ORIGINAL</c> segment for one selected machine. The caller is
    /// responsible for adding it and saving so the generated UID can be linked from allocations.
    /// </summary>
    public static ProductionMachineScheduleSegment BuildOriginalSegment(
        ProductionWorkOrder order,
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine machine,
        string userId,
        DateTime now,
        AppDbContext db)
    {
        var plan = MachineScheduleSegmentCalculator.ComputeQuantity(
            operation.PlannedOutputQty,
            machine.OutputPerCycle,
            machine.ParallelMachineCount,
            machine.CycleSeconds);

        return new ProductionMachineScheduleSegment
        {
            CompanyCode = order.CompanyCode,
            BranchCode = order.BranchCode,
            WorkOrderId = order.Uid,
            WorkOrderOperationId = operation.Uid,
            WorkOrderMachineId = machine.Uid,
            MachineCode = machine.MachineCode,
            SegmentSequence = 1,
            SegmentKind = ProductionMachineScheduleSegmentKinds.Original,
            SegmentStatus = ProductionMachineScheduleSegmentStatuses.Planned,
            PlannedOutputQty = operation.PlannedOutputQty,
            OutputUom = operation.PlannedOutputUom,
            PlannedCycleCount = plan.CycleCount > 0m ? plan.CycleCount : machine.PlannedCycleCount,
            PlannedCycleSlots = plan.CycleSlots > 0m ? plan.CycleSlots : machine.PlannedCycleSlots,
            ScheduledRunMinutes = machine.PlannedRunMinutes,
            ScheduledSetupSeconds = machine.SetupSeconds,
            ScheduledConversionSeconds = machine.ConversionSeconds,
            ScheduledQueueSeconds = machine.QueueSeconds,
            PlannedStartDateTime = machine.PlannedStartDateTime ?? order.PlannedStartDateTime,
            PlannedCompletionDateTime = machine.PlannedCompletionDateTime ?? order.PlannedCompletionDateTime,
            CalendarHorizonStart = machine.CalendarHorizonStart,
            CalendarHorizonEnd = machine.CalendarHorizonEnd,
            SegmentScheduleSourceHash = machine.ScheduleSourceHash,
            MachineScheduleRevision = machine.ScheduleRevision,
            ReasonCode = "RELEASE",
            Reason = "Draft release seeded the canonical one-span plan.",
            CreatedDate = now,
            CreatedBy = TruncateUser(userId),
            RowVersion = NewRowVersion(db)
        };
    }

    /// <summary>Marks existing current segments as historical. Never deletes planning history.</summary>
    public static void Supersede(
        IEnumerable<ProductionMachineScheduleSegment> segments,
        string userId,
        DateTime now,
        string reason)
    {
        foreach (var segment in segments)
        {
            if (string.Equals(segment.SegmentStatus, ProductionMachineScheduleSegmentStatuses.Superseded, StringComparison.Ordinal))
                continue;
            segment.SegmentStatus = ProductionMachineScheduleSegmentStatuses.Superseded;
            segment.SupersededDate = now;
            segment.SupersededBy = TruncateUser(userId);
            segment.ModifiedDate = now;
            segment.ModifiedBy = TruncateUser(userId);
            segment.Reason = TrimReason(reason);
        }
    }

    public static string TrimReason(string? reason)
    {
        var value = (reason ?? string.Empty).Trim();
        return value.Length > 500 ? value[..500] : value;
    }

    /// <summary>True when the operation has any execution fact that freezes its history.</summary>
    public static bool HasExecution(ProductionWorkOrderOperation operation) =>
        operation.InputQty != 0m
        || operation.ProcessedQty != 0m
        || operation.GoodQty != 0m
        || operation.ScrapQty != 0m
        || operation.RejectQty != 0m
        || operation.HoldQty != 0m
        || operation.ReworkQty != 0m
        || operation.TransferredQty != 0m;

    public static decimal RemainingQuantity(ProductionWorkOrderOperation operation) =>
        operation.RemainingQty > 0m
            ? operation.RemainingQty
            : Math.Max(0m, operation.PlannedOutputQty - operation.GoodQty);
}
