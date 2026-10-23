using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Core.Production;
using ErpWeb.Core.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;

namespace ErpWeb.Tests.Planning.Transaction;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class MachineScheduleSegmentSetSchemaTests
{
    [Fact]
    public void SqlServer_model_allows_only_one_current_set_per_selected_machine()
    {
        using var db = CreateModelContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(ProductionMachineScheduleSegmentSet))!;

        Assert.Equal("PrMachineScheduleSegmentSet", entity.GetTableName());
        var guard = Assert.Single(entity.GetIndexes(),
            x => x.GetDatabaseName() == "UX_PrMachineScheduleSegmentSet_OneCurrent");
        Assert.True(guard.IsUnique);
        Assert.Equal("[IsCurrent] = 1", guard.GetFilter());
        Assert.Equal(nameof(ProductionMachineScheduleSegmentSet.WorkOrderMachineId),
            Assert.Single(guard.Properties).Name);
    }

    [Fact]
    public void SqlServer_model_has_explicit_unique_allocation_link_and_transitional_set_column()
    {
        using var db = CreateModelContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var link = model.FindEntityType(typeof(ProductionMachineScheduleAllocationSegment))!;
        var segment = model.FindEntityType(typeof(ProductionMachineScheduleSegment))!;

        Assert.Equal("PrMachineScheduleAllocationSegment", link.GetTableName());
        var guard = Assert.Single(link.GetIndexes(),
            x => x.GetDatabaseName() == "UX_PrMachineScheduleAllocationSegment_Allocation");
        Assert.True(guard.IsUnique);
        Assert.Equal(nameof(ProductionMachineScheduleAllocationSegment.AllocationId),
            Assert.Single(guard.Properties).Name);
        Assert.True(segment.FindProperty(nameof(ProductionMachineScheduleSegment.SegmentSetId))!.IsNullable);
    }

    [Fact]
    public void Original_segment_uses_canonical_machine_cycles_after_uom_conversion()
    {
        using var db = CreateModelContext();
        var order = new ProductionWorkOrder
        {
            Uid = 1, CompanyCode = "DEMO", BranchCode = "HQ",
            PlannedStartDateTime = new DateTime(2026, 10, 1, 8, 0, 0),
            PlannedCompletionDateTime = new DateTime(2026, 10, 1, 8, 6, 0)
        };
        var operation = new ProductionWorkOrderOperation
        {
            Uid = 2, PlannedOutputQty = 1m, PlannedOutputUom = "BOX"
        };
        var machine = new ProductionWorkOrderMachine
        {
            Uid = 3, MachineCode = "MC01", OutputPerCycle = 2m,
            OutputPerCycleUom = "PCS", ParallelMachineCount = 1,
            CycleSeconds = 60m, RequiredMachineOutputQty = 12m,
            PlannedCycleCount = 6m, PlannedCycleSlots = 6m,
            PlannedRunMinutes = 6m,
            PlannedStartDateTime = order.PlannedStartDateTime,
            PlannedCompletionDateTime = order.PlannedCompletionDateTime
        };

        var segment = MachineScheduleSegmentPersistence.BuildOriginalSegment(
            order, operation, machine, "TEST", DateTime.UtcNow, db);

        Assert.Equal(1m, segment.PlannedOutputQty);
        Assert.Equal("BOX", segment.OutputUom);
        Assert.Equal(6m, segment.PlannedCycleCount);
        Assert.Equal(6m, segment.PlannedCycleSlots);
        Assert.Equal(6m, segment.ScheduledRunMinutes);
    }

    [Fact]
    public void Future_planned_quantity_excludes_execution_locked_history()
    {
        var segments = new[]
        {
            new ProductionMachineScheduleSegment
            {
                SegmentStatus = ProductionMachineScheduleSegmentStatuses.ExecutionLocked,
                PlannedOutputQty = 7m
            },
            new ProductionMachineScheduleSegment
            {
                SegmentStatus = ProductionMachineScheduleSegmentStatuses.Planned,
                PlannedOutputQty = 3m
            }
        };

        Assert.Equal(3m, MachineScheduleSegmentPersistence.FuturePlannedQty(segments));
        Assert.True(MachineScheduleSegmentPersistence.RequiresRemainingReplan(4m, segments));
        Assert.False(MachineScheduleSegmentPersistence.RequiresRemainingReplan(3m, segments));
    }

    [Fact]
    public async Task Advanced_segment_converts_box_output_to_machine_piece_cycles()
    {
        var uom = new Mock<IUomConversionService>();
        uom.Setup(x => x.ConvertAsync("DEMO", "OP10", 1m, "BOX", "PCS",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(UomConversionResult.Ok(12m, 4));
        var operation = new ProductionWorkOrderOperation
        {
            OperationCode = "OP10", PlannedOutputUom = "BOX"
        };
        var machine = new ProductionWorkOrderMachine
        {
            OutputPerCycle = 2m, OutputPerCycleUom = "PCS",
            ParallelMachineCount = 1, CycleSeconds = 60m
        };

        var result = await MachineScheduleSegmentCalculator.ComputeQuantityAsync(
            "DEMO", operation, machine, 1m, uom.Object);

        Assert.Equal(12m, result.OutputQty);
        Assert.Equal(6m, result.CycleCount);
        Assert.Equal(6m, result.CycleSlots);
        Assert.Equal(6m, result.RunMinutes);
        uom.VerifyAll();
    }

    [Fact]
    public void Productive_cycle_boundary_maps_across_lunch_break()
    {
        var day = new DateTime(2026, 10, 5);
        var fragments = new[]
        {
            new TimeInterval(day.AddHours(11), day.AddHours(12)),
            new TimeInterval(day.AddHours(13), day.AddHours(15))
        };

        var boundary = MachineScheduleSegmentCalculator.ResolveCompletedProductiveBoundary(
            fragments, day.AddHours(14).AddMinutes(30), overheadSeconds: 0m,
            cycleSeconds: 3600m);

        Assert.Equal(2, boundary.CompletedSlots);
        Assert.Equal(day.AddHours(14), boundary.Resolved);
    }

    private static AppDbContext CreateModelContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\ModelOnly;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new AppDbContext(options);
    }
}
