using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests.Planning.Master;
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductDefinitionPhase1SchemaTests
{
    private static IModel SqlServerModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ErpWebModelOnly;Trusted_Connection=True")
            .Options;
        using var db = new AppDbContext(options);
        return db.Model;
    }

    [Fact]
    public void Route_steps_have_independent_occurrence_identity_and_stage_index()
    {
        var entity = SqlServerModel().FindEntityType(typeof(PrBomRouteStep));
        Assert.NotNull(entity);

        Assert.Contains(entity!.GetIndexes(), x =>
            x.IsUnique && x.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(PrBomRouteStep.BomHdrId), nameof(PrBomRouteStep.RouteStepKey)]));
        Assert.Contains(entity.GetIndexes(), x =>
            x.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(PrBomRouteStep.BomHdrId), nameof(PrBomRouteStep.StageSequence)]));
    }

    [Fact]
    public void Operation_sequence_is_unique_within_route_occurrence()
    {
        var entity = SqlServerModel().FindEntityType(typeof(PrBomOperation));
        Assert.NotNull(entity);
        var index = Assert.Single(entity!.GetIndexes(), x =>
            x.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(PrBomOperation.RouteStepId), nameof(PrBomOperation.ProcessSequence)]));

        Assert.True(index.IsUnique);
        Assert.Equal("[RouteStepID] IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void Machine_options_enforce_at_most_one_default_and_unique_priority()
    {
        var entity = SqlServerModel().FindEntityType(typeof(PrBomMachineOption));
        Assert.NotNull(entity);
        var defaultIndex = Assert.Single(entity!.GetIndexes(), x =>
            x.GetDatabaseName() == "UX_PrBomMachineOption_OneDefault");
        var priorityIndex = Assert.Single(entity.GetIndexes(), x =>
            x.GetDatabaseName() == "UQ_PrBomMachineOption_Operation_Priority");

        Assert.True(defaultIndex.IsUnique);
        Assert.Equal("[IsPrimary] = 1", defaultIndex.GetFilter());
        Assert.True(priorityIndex.IsUnique);
        Assert.Null(entity.FindProperty(nameof(PrBomMachineOption.IsDefault)));
    }

    [Fact]
    public void Machine_specific_labour_uses_restricted_optional_machine_fk()
    {
        var entity = SqlServerModel().FindEntityType(typeof(PrBomLabourRequirement));
        Assert.NotNull(entity);
        var machineFk = Assert.Single(entity!.GetForeignKeys(), x =>
            x.Properties.Single().Name == nameof(PrBomLabourRequirement.MachineOptionId));

        Assert.False(machineFk.IsRequired);
        Assert.Equal(DeleteBehavior.NoAction, machineFk.DeleteBehavior);
        Assert.Equal(2, entity.GetIndexes().Count(x => x.IsUnique));
    }

    [Fact]
    public void Uom_conversion_has_one_active_directed_pair()
    {
        var entity = SqlServerModel().FindEntityType(typeof(IvItemUomConversion));
        Assert.NotNull(entity);
        var index = Assert.Single(entity!.GetIndexes(), x =>
            x.GetDatabaseName() == "UX_IvItemUomConversion_ActivePair");

        Assert.True(index.IsUnique);
        Assert.Equal("[IsActive] = 1", index.GetFilter());
        Assert.Equal(8, entity.FindProperty(nameof(IvItemUomConversion.FromQty))!.GetScale());
        Assert.Equal(8, entity.FindProperty(nameof(IvItemUomConversion.ToQty))!.GetScale());
    }
}
