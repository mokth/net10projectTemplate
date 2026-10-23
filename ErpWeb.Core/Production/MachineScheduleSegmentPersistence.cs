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

    public static ProductionMachineScheduleSegmentSet BuildCurrentSet(
        ProductionWorkOrder order,
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine machine,
        string mode,
        string planningState,
        decimal plannedQtyBasis,
        string? reason,
        string userId,
        DateTime now,
        AppDbContext db) => new()
    {
        CompanyCode = order.CompanyCode,
        BranchCode = order.BranchCode,
        WorkOrderId = order.Uid,
        WorkOrderOperationId = operation.Uid,
        WorkOrderMachineId = machine.Uid,
        MachineCode = machine.MachineCode,
        Mode = mode,
        PlanningState = planningState,
        IsCurrent = true,
        PlannedQtyBasis = plannedQtyBasis,
        OutputUom = operation.PlannedOutputUom,
        CalendarHorizonStart = machine.CalendarHorizonStart,
        CalendarHorizonEnd = machine.CalendarHorizonEnd,
        ScheduleSourceHash = machine.ScheduleSourceHash,
        MachineScheduleRevision = machine.ScheduleRevision,
        Reason = TrimReason(reason),
        CreatedDate = now,
        CreatedBy = TruncateUser(userId),
        RowVersion = NewRowVersion(db)
    };

    public static void SupersedeSets(
        IEnumerable<ProductionMachineScheduleSegmentSet> sets,
        string userId,
        DateTime now)
    {
        foreach (var set in sets.Where(x => x.IsCurrent))
        {
            set.IsCurrent = false;
            set.SupersededDate = now;
            set.SupersededBy = TruncateUser(userId);
            set.ModifiedDate = now;
            set.ModifiedBy = TruncateUser(userId);
        }
    }

    public static async Task RemoveAllocationLinksAsync(
        AppDbContext db,
        IReadOnlyCollection<ProductionMachineScheduleAllocation> allocations,
        CancellationToken cancellationToken)
    {
        var ids = allocations.Select(x => x.Uid).Where(x => x > 0).ToList();
        foreach (var batch in ids.Chunk(800))
        {
            var links = await db.ProductionMachineScheduleAllocationSegments
                .Where(x => batch.Contains(x.AllocationId))
                .ToListAsync(cancellationToken);
            db.ProductionMachineScheduleAllocationSegments.RemoveRange(links);
        }
    }

    public static void AddAllocationLink(
        AppDbContext db,
        ProductionMachineScheduleAllocation allocation,
        ProductionMachineScheduleSegment segment,
        string userId,
        DateTime now)
    {
        if (allocation.Uid <= 0 || segment.Uid <= 0 || allocation.WorkOrderMachineId != segment.WorkOrderMachineId)
            throw new InvalidOperationException("A saved allocation and its segment must belong to the same machine.");
        db.ProductionMachineScheduleAllocationSegments.Add(new ProductionMachineScheduleAllocationSegment
        {
            AllocationId = allocation.Uid,
            SegmentId = segment.Uid,
            CreatedDate = now,
            CreatedBy = TruncateUser(userId),
            RowVersion = NewRowVersion(db)
        });
    }

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
            // The Work Order quantity calculator already converted operation output into the
            // machine's cycle UOM. Reusing its saved values avoids dividing BOX by PCS here.
            PlannedCycleCount = machine.PlannedCycleCount,
            PlannedCycleSlots = machine.PlannedCycleSlots,
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
        Math.Max(0m, operation.RemainingQty);

    public static decimal FuturePlannedQty(IEnumerable<ProductionMachineScheduleSegment> currentSegments) =>
        currentSegments
            .Where(x => x.SegmentStatus == ProductionMachineScheduleSegmentStatuses.Planned)
            .Sum(x => x.PlannedOutputQty);

    public static bool RequiresRemainingReplan(
        decimal remainingQty,
        IEnumerable<ProductionMachineScheduleSegment> currentSegments) =>
        remainingQty > FuturePlannedQty(currentSegments);
}
