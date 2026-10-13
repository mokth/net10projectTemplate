using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionWorkOrderTransferSchemaTests
{
    private static readonly IModel Model = BuildModel();

    [Fact]
    public void Transfer_document_maps_the_hard_source_and_destination_contracts()
    {
        var entity = Model.FindEntityType(typeof(ProductionWorkOrderTransfer))
            ?? throw new InvalidOperationException("ProductionWorkOrderTransfer is not mapped.");
        Assert.Equal("PrWorkOrderTransfer", entity.GetTableName());
        AssertMoney(entity, nameof(ProductionWorkOrderTransfer.TransferTotalCost), 19, 6);
        Assert.Equal(18, entity.FindProperty(nameof(ProductionWorkOrderTransfer.Qty))!.GetPrecision());
        Assert.Equal(4, entity.FindProperty(nameof(ProductionWorkOrderTransfer.Qty))!.GetScale());
        Assert.Equal("SourceBalanceRowVersion", entity.FindProperty(nameof(ProductionWorkOrderTransfer.SourceBalanceRowVersion))!
            .GetColumnName(StoreObjectIdentifier.Table("PrWorkOrderTransfer", null)));
        Assert.Equal("varbinary(8)", entity.FindProperty(nameof(ProductionWorkOrderTransfer.SourceBalanceRowVersion))!.GetColumnType());
        Assert.Equal("SourceLastMovementDate", entity.FindProperty(nameof(ProductionWorkOrderTransfer.SourceLastMovementDate))!
            .GetColumnName(StoreObjectIdentifier.Table("PrWorkOrderTransfer", null)));

        var indexes = entity.GetIndexes().ToDictionary(x => x.GetDatabaseName()!);
        Assert.True(indexes["UQ_PrWorkOrderTransfer_Tenant_No"].IsUnique);
        Assert.True(indexes["UQ_PrWorkOrderTransfer_NewSource"].IsUnique);
        Assert.Contains("Status", indexes["UQ_PrWorkOrderTransfer_NewSource"].GetFilter()!, StringComparison.Ordinal);
        Assert.Contains("DeletedAtUtc", indexes["UQ_PrWorkOrderTransfer_NewSource"].GetFilter()!, StringComparison.Ordinal);
        Assert.Contains(entity.GetCheckConstraints(), x => x.Name == "CK_PrWorkOrderTransfer_Quantities");
        Assert.All(entity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
    }

    [Fact]
    public void Deployment_scripts_are_additive_and_seed_only_available_permissions()
    {
        var schema = File.ReadAllText(FindScript("create-production-work-order-transfer.sql"));
        Assert.Contains("IF OBJECT_ID(N'dbo.PrWorkOrderTransfer', N'U') IS NULL", schema, StringComparison.Ordinal);
        Assert.Contains("(N'SourceLastMovementDate', N'datetime2(7)', 0)", schema, StringComparison.Ordinal);
        Assert.Contains("@TransferColumns", schema, StringComparison.Ordinal);
        Assert.Contains("CK_PrWorkOrderTransfer_Contexts", schema, StringComparison.Ordinal);
        Assert.Contains("FK_PrWorkOrderTransfer_SourceBalance", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_PrWorkOrderTransfer_NewSource", schema, StringComparison.Ordinal);
        Assert.Contains("DocumentRevision", schema, StringComparison.Ordinal);
        Assert.Contains("SourceSnapshotRevision", schema, StringComparison.Ordinal);
        Assert.Contains("PrProductionWorkOrderTransferFact", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_PrProductionWorkOrderTransferFact_ReversesFact", schema, StringComparison.Ordinal);
        Assert.Contains("UQ_PrProductionPostingLink_WorkOrderTransferGeneration", schema, StringComparison.Ordinal);
        Assert.Contains("WHERE Status=N'NEW' AND DeletedAtUtc IS NULL", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX UQ_PrProductionBalLot_Wip", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP INDEX UQ_PrProductionBalLot_V2_Wip", schema, StringComparison.OrdinalIgnoreCase);

        var menu = File.ReadAllText(FindScript("init-planning-work-order-transfer-menu.sql"));
        Assert.Contains("N'VIEW_COST'", menu, StringComparison.Ordinal);
        Assert.Contains("N'ROLLBACK'", menu, StringComparison.Ordinal);
        Assert.Contains("/planning/work-order-transfer", menu, StringComparison.Ordinal);
    }

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\ModelOnly;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        using var db = new AppDbContext(options);
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static void AssertMoney(IEntityType entity, string propertyName, int precision, int scale)
    {
        var property = entity.FindProperty(propertyName)!;
        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
    }

    private static string FindScript(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "scripts", name);
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Cannot locate {name}.");
    }
}

[Trait(TestCategories.Name, TestCategories.Production)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed partial class ProductionWorkOrderTransferSqlServerTests
{
    [ProductionStockSqlServerFact]
    public async Task Transfer_schema_script_can_run_twice_without_changing_wip_identity_indexes()
    {
        var connectionString = Environment.GetEnvironmentVariable(ProductionStockTransactionSqlServerTests.ConnectionVariable);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using (var db = new AppDbContext(options)) await db.Database.EnsureCreatedAsync();

        var script = File.ReadAllText(FindScript("create-production-work-order-transfer.sql"));
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        for (var run = 0; run < 2; run++)
        {
            foreach (var batch in batches)
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 60;
                command.CommandText = batch;
                await command.ExecuteNonQueryAsync();
            }
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT CASE WHEN OBJECT_ID(N'dbo.PrWorkOrderTransfer',N'U') IS NOT NULL
                AND COL_LENGTH(N'dbo.PrWorkOrderTransfer',N'SourceLastStockEventEffectiveAt') IS NOT NULL
                AND EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionBalLot') AND name=N'UQ_PrProductionBalLot_Wip')
                AND EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionBalLot') AND name=N'UQ_PrProductionBalLot_V2_Wip')
                THEN 1 ELSE 0 END;
            """;
        Assert.Equal(1, Convert.ToInt32(await verify.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string FindScript(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "scripts", name);
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Cannot locate {name}.");
    }
}
