using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

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

    private static AppDbContext CreateModelContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\ModelOnly;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new AppDbContext(options);
    }
}
