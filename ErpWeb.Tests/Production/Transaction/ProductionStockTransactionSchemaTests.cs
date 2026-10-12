using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionStockTransactionSchemaTests
{
    private static readonly Type[] DocumentTypes = [typeof(ProductionStockReturn), typeof(ProductionStockAdjustment)];
    private static readonly Type[] LineTypes = [typeof(ProductionStockReturnLine), typeof(ProductionStockAdjustmentLine)];
    private static readonly Type[] FactTypes = [typeof(ProductionStockReturnFact), typeof(ProductionStockAdjustmentFact)];
    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=(localdb)\\ModelOnly;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True")
        .AddInterceptors(new SuppressDatabaseSave()).Options);

    [Fact]
    public void Document_schema_preserves_precision_snapshot_concurrency_and_restrict_lineage()
    {
        using var db = Context();
        var model = db.GetService<IDesignTimeModel>().Model;
        foreach (var type in DocumentTypes.Concat(LineTypes).Concat(FactTypes).Append(typeof(ProductionStockReturnPriceSnapshot)))
        {
            var entity = model.FindEntityType(type)!;
            Assert.All(entity.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
        }
        foreach (var type in DocumentTypes)
        {
            var entity = model.FindEntityType(type)!;
            Assert.True(entity.FindProperty("RowVersion")!.IsConcurrencyToken);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, entity.FindProperty("RowVersion")!.ValueGenerated);
            Assert.Equal(64, entity.FindProperty("SnapshotHash")!.GetMaxLength());
            Assert.False(entity.FindProperty("SnapshotHash")!.IsUnicode());
            AssertUnique(entity, "PostingId");
            AssertUnique(entity, "ReversalPostingId");
        }
        foreach (var type in LineTypes)
        {
            var entity = model.FindEntityType(type)!;
            Precision(entity, "BaseQty", 18, 4);
            Precision(entity, "SourceFactor", 18, 8);
            AssertUnique(entity, "DetailId");
            AssertUnique(entity, "BatchId", "ProductionBalLotId");
        }
        Precision(model.FindEntityType(typeof(ProductionStockAdjustmentLine))!, "ProposedUnitCost", 19, 6);
        foreach (var type in FactTypes)
        {
            var entity = model.FindEntityType(type)!;
            Precision(entity, "BaseQty", 18, 4);
            Precision(entity, "TotalValue", 19, 6);
            AssertUnique(entity, "ProductionMovementId");
            AssertUnique(entity, "ReversesFactId");
            Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.GetFilter() == "[ReversesFactId] IS NOT NULL");
            Assert.False(entity.IsSqlOutputClauseUsed());
        }
        Precision(model.FindEntityType(typeof(ProductionStockAdjustmentFact))!, "UnitCost", 19, 6);
        Assert.Equal(200, model.FindEntityType(typeof(ProductionStockReturnPriceSnapshot))!.FindProperty("PreviousPriceEvidence")!.GetMaxLength());
    }

    [Fact]
    public void Material_return_reversal_and_independent_primary_posting_uniqueness_are_mapped()
    {
        using var db = Context();
        var model = db.GetService<IDesignTimeModel>().Model;
        Assert.Contains("'RETURN_REVERSAL'", model.FindEntityType(typeof(ProductionMaterialMovement))!
            .GetCheckConstraints().Single(x => x.Name == "CK_PrMaterialMovement_Type").Sql);
        var indexes = model.FindEntityType(typeof(ProductionPostingLink))!.GetIndexes().ToDictionary(x => x.GetDatabaseName()!);
        Assert.Contains("MATERIAL_ISSUE_POST", indexes["UQ_PrProductionPostingLink_MaterialIssueBatch"].GetFilter());
        Assert.Contains("PRODUCTION_STOCK_RETURN_POST", indexes["UQ_PrProductionPostingLink_StockTransactionBatch"].GetFilter());
        Assert.Contains("PRODUCTION_STOCK_ADJUSTMENT_POST", indexes["UQ_PrProductionPostingLink_StockTransactionBatch"].GetFilter());
        Assert.True(indexes["UQ_PrProductionPostingLink_StockTransactionBatch"].IsUnique);
    }

    [Fact]
    public void New_posting_is_disabled_by_default()
    {
        var options = new ProductionStockTransactionOptions();
        Assert.False(options.ReturnPostingEnabled);
        Assert.False(options.AdjustmentPostingEnabled);
    }

    [Theory]
    [InlineData("PRTN")]
    [InlineData("PADJ")]
    public async Task Generic_inventory_writes_cannot_create_or_relabel_production_documents(string type)
    {
        using var db = Context();
        db.IvTrxBatches.Add(new() { TrxType = type });
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var batch = new IvTrxBatch { Id = 123, TrxType = type };
        db.Attach(batch);
        batch.TrxType = "MR";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        var detail = new IvTrxBatchDetail { Id = 456, TrxType = type };
        db.Attach(detail);
        detail.TrxType = "MR";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("PRTN")]
    [InlineData("PADJ")]
    public async Task Capabilities_are_independent_and_do_not_bypass_fact_immutability(string type)
    {
        using var db = Context();
        db.ProductionStockReturnWrite = type == "PRTN";
        db.ProductionStockAdjustmentWrite = type == "PADJ";
        db.IvTrxBatches.Add(new() { TrxType = type });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.IvTrxBatches.Add(new() { TrxType = type == "PRTN" ? "PADJ" : "PRTN" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.ProductionStockReturnWrite = db.ProductionStockAdjustmentWrite = true;
        foreach (var entity in new object[] { new ProductionStockReturnFact { Id = 1 }, new ProductionStockAdjustmentFact { Id = 2 }, new ProductionStockReturnPriceSnapshot { Id = 3 } })
        {
            db.Attach(entity);
            db.Entry(entity).State = EntityState.Modified;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.Entry(entity).State = EntityState.Deleted;
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
            db.ChangeTracker.Clear();
        }
    }

    private static void Precision(IEntityType entity, string name, int precision, int scale)
    {
        Assert.Equal(precision, entity.FindProperty(name)!.GetPrecision());
        Assert.Equal(scale, entity.FindProperty(name)!.GetScale());
    }

    private static void AssertUnique(IEntityType entity, params string[] properties) =>
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(properties));

    private sealed class SuppressDatabaseSave : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) => InterceptionResult<int>.SuppressWithResult(0);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}
