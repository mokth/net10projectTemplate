using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionDailyOutputSchemaTests
{
    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\ModelOnly;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        using var db = new AppDbContext(options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Production_bal_lot_table_and_uniques_are_mapped()
    {
        var entity = Model.FindEntityType(typeof(ProductionBalLot))
            ?? throw new InvalidOperationException("ProductionBalLot is not mapped.");
        Assert.Equal("PrProductionBalLot", entity.GetTableName());
        var indexes = entity.GetIndexes().Select(x => x.GetDatabaseName()).ToHashSet();
        Assert.Contains("UQ_PrProductionBalLot_MaterialIn", indexes);
        Assert.Contains("UQ_PrProductionBalLot_Wip", indexes);
    }

    [Fact]
    public void Production_bal_lot_movement_types_include_reversals()
    {
        var entity = Model.FindEntityType(typeof(ProductionBalLotMovement))
            ?? throw new InvalidOperationException("ProductionBalLotMovement is not mapped.");
        Assert.Equal("PrProductionBalLotMovement", entity.GetTableName());
        var checks = entity.GetCheckConstraints().Select(x => x.Name).ToHashSet();
        Assert.Contains("CK_PrProductionBalLotMovement_Type", checks);
    }

    [Fact]
    public void Production_output_statuses_and_document_table_are_mapped()
    {
        var entity = Model.FindEntityType(typeof(ProductionOutput))
            ?? throw new InvalidOperationException("ProductionOutput is not mapped.");
        Assert.Equal("PrProductionOutput", entity.GetTableName());
        Assert.Equal(ProductionOutputStatuses.New, "NEW");
        Assert.Equal(ProductionOutputStatuses.Posted, "POSTED");
        Assert.Equal(ProductionOutputStatuses.Reversed, "REVERSED");
        Assert.Equal(ProductionPostingCommandTypes.OutputPost, "OUTPUT_POST");
        Assert.Equal(ProductionPostingCommandTypes.OutputRollback, "OUTPUT_ROLLBACK");
        Assert.Equal(RunningNumberKeys.ProductionDailyOutput, "PR_DAILY_OUTPUT");
    }

    [Fact]
    public void Material_movement_supports_consume_reversal_and_filtered_uniques()
    {
        var entity = Model.FindEntityType(typeof(ProductionMaterialMovement))
            ?? throw new InvalidOperationException("ProductionMaterialMovement is not mapped.");
        var checks = string.Join(' ', entity.GetCheckConstraints().Select(x => x.Sql ?? x.Name ?? ""));
        Assert.Contains("CONSUME_REVERSAL", checks, StringComparison.Ordinal);
        var indexes = entity.GetIndexes().ToDictionary(x => x.GetDatabaseName()!);
        Assert.True(indexes["UQ_PrMaterialMovement_PostingInventoryLine"].IsUnique);
        Assert.True(indexes["UQ_PrMaterialMovement_PostingBalLotLine"].IsUnique);
        Assert.Contains("InventoryBatchDetailID", indexes["UQ_PrMaterialMovement_PostingInventoryLine"].GetFilter()!, StringComparison.Ordinal);
        Assert.Contains("ProductionBalLotMovementID", indexes["UQ_PrMaterialMovement_PostingBalLotLine"].GetFilter()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_hash_current_is_v3()
    {
        Assert.Equal(3, ProductionSnapshotHashVersions.Current);
        Assert.Equal(3, ProductionSnapshotHashVersions.RouteOutputContractV3);
    }
}
