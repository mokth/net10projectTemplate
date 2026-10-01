using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionMaterialIssueSchemaTests
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

    private static IEntityType Entity =>
        Model.FindEntityType(typeof(ProductionMaterialMovement))
        ?? throw new InvalidOperationException("ProductionMaterialMovement is not mapped.");

    private static IEntityType DraftLineEntity =>
        Model.FindEntityType(typeof(ProductionMaterialIssueLine))
        ?? throw new InvalidOperationException("ProductionMaterialIssueLine is not mapped.");

    [Fact]
    public void Movement_has_required_table_key_precision_and_constraints()
    {
        Assert.Equal("PrMaterialMovement", Entity.GetTableName());
        Assert.Equal("UID", Entity.FindProperty(nameof(ProductionMaterialMovement.Uid))!.GetColumnName());
        Assert.Equal(18, Entity.FindProperty(nameof(ProductionMaterialMovement.Qty))!.GetPrecision());
        Assert.Equal(4, Entity.FindProperty(nameof(ProductionMaterialMovement.Qty))!.GetScale());
        Assert.Equal(8, Entity.FindProperty(nameof(ProductionMaterialMovement.ConversionFactorToBase))!.GetScale());

        var checks = Entity.GetCheckConstraints().Select(x => x.Name).ToHashSet();
        Assert.Contains("CK_PrMaterialMovement_Qty", checks);
        Assert.Contains("CK_PrMaterialMovement_Conversion", checks);
        Assert.Contains("CK_PrMaterialMovement_Cost", checks);
        Assert.Contains("CK_PrMaterialMovement_Type", checks);
    }

    [Fact]
    public void Movement_foreign_keys_are_restrict_and_history_is_informational_only()
    {
        var expectedPrincipals = new[]
        {
            typeof(ProductionWorkOrder), typeof(ProductionWorkOrderMaterial),
            typeof(ProductionWorkOrderOperation), typeof(ProductionPostingLink),
            typeof(ProductionMaterialMovement), typeof(IvTrxBatch),
            typeof(IvTrxBatchDetail), typeof(IvBalLoc), typeof(IvLot)
        };

        Assert.Equal(expectedPrincipals.Length, Entity.GetForeignKeys().Count());
        Assert.All(Entity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        Assert.Equal(expectedPrincipals.OrderBy(x => x.Name),
            Entity.GetForeignKeys().Select(x => x.PrincipalEntityType.ClrType).OrderBy(x => x.Name));
        Assert.DoesNotContain(Entity.GetForeignKeys(), x =>
            x.Properties.Any(p => p.Name == nameof(ProductionMaterialMovement.InventoryHistoryId)));
    }

    [Fact]
    public void Movement_has_required_indexes_and_posting_line_is_unique()
    {
        var indexes = Entity.GetIndexes().ToDictionary(x => x.GetDatabaseName()!);
        Assert.Contains("IX_PrMaterialMovement_WorkOrder_Date", indexes.Keys);
        Assert.Contains("IX_PrMaterialMovement_Material_Type", indexes.Keys);
        Assert.Contains("IX_PrMaterialMovement_Operation_Date", indexes.Keys);
        Assert.Contains("IX_PrMaterialMovement_InventoryBatch", indexes.Keys);
        Assert.Contains("IX_PrMaterialMovement_OriginalMovement", indexes.Keys);
        Assert.True(indexes["UQ_PrMaterialMovement_PostingLine"].IsUnique);
    }

    [Fact]
    public void Draft_line_has_allocation_grain_constraints_indexes_and_restrict_foreign_keys()
    {
        Assert.Equal("PrMaterialIssueLine", DraftLineEntity.GetTableName());
        Assert.Equal(18, DraftLineEntity.FindProperty(nameof(ProductionMaterialIssueLine.IssueQty))!.GetPrecision());
        Assert.Equal(4, DraftLineEntity.FindProperty(nameof(ProductionMaterialIssueLine.BaseQty))!.GetScale());
        Assert.All(DraftLineEntity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        Assert.Equal(6, DraftLineEntity.GetForeignKeys().Count());
        var indexes = DraftLineEntity.GetIndexes().ToDictionary(x => x.GetDatabaseName()!);
        Assert.True(indexes["UQ_PrMaterialIssueLine_InventoryDetail"].IsUnique);
        Assert.True(indexes["UQ_PrMaterialIssueLine_PostingLine"].IsUnique);
        Assert.Contains("IX_PrMaterialIssueLine_Batch", indexes.Keys);
        Assert.Contains("IX_PrMaterialIssueLine_Material", indexes.Keys);
        Assert.Contains("IX_PrMaterialIssueLine_Operation", indexes.Keys);
    }

    [Fact]
    public void Posting_link_has_snapshot_fingerprint_and_one_material_issue_link_per_batch()
    {
        var link = Model.FindEntityType(typeof(ProductionPostingLink))!;
        Assert.Equal(64, link.FindProperty(nameof(ProductionPostingLink.SnapshotHash))!.GetMaxLength());
        var index = link.GetIndexes().Single(x => x.GetDatabaseName() == "UQ_PrProductionPostingLink_MaterialIssueBatch");
        Assert.True(index.IsUnique);
        Assert.Contains("MATERIAL_ISSUE_POST", index.GetFilter());
    }
}
