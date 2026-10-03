using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class ProductionStockLedgerSqlServerTests
{
    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    [Fact]
    public async Task SqlServer_coordinator_replays_and_rejects_concurrent_duplicate_revision()
    {
        var cs = TryResolveScratch();
        if (cs is null)
            return;

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        await using (var db = await factory.CreateDbContextAsync())
        {
            if (!await db.StockLedgerEpochs.AnyAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"))
            {
                db.StockLedgerEpochs.Add(new StockLedgerEpoch
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    EffectiveFrom = new DateTime(2026, 10, 1),
                    Version = 2,
                    Status = StockLedgerEpochStatuses.Active,
                    MigrationBatchId = Guid.NewGuid(),
                    ReconciliationManifestHash = new string('A', 64)
                });
                await db.SaveChangesAsync();
            }
        }

        var coordinator = new StockPostingCoordinator(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            new BranchStockTransactionLock(),
            new StockPeriodGuard(),
            new NoActiveStockFreezeGuard());
        var requestId = Guid.NewGuid();
        var command = new StockPostingCommand
        {
            RequestId = requestId,
            CommandType = "TEST_SQL",
            SourceModule = "TEST",
            SourceDocumentType = "FIXTURE",
            SourceDocumentId = requestId.ToString("N"),
            SourceDocumentNo = "SQL-1",
            DocumentRevision = 1,
            EffectiveAt = new DateTime(2026, 10, 2, 8, 0, 0),
            Evidence = StockPostingFingerprint.Create(new { command = "TEST_SQL", qty = 1 }, new { line = 1 })
        };

        var first = await coordinator.ExecuteAsync(command, (_, _) => Task.FromResult(1));
        var replay = await coordinator.ExecuteAsync(command, (_, _) => Task.FromResult(2));
        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(replay.WasReplay);

        var secondToken = command with
        {
            RequestId = Guid.NewGuid(),
            Evidence = StockPostingFingerprint.Create(new { command = "TEST_SQL", qty = 2 }, new { line = 1 })
        };
        var rejected = await coordinator.ExecuteAsync(secondToken, (_, _) => Task.FromResult(3));
        Assert.Equal(StockLedgerErrorCodes.DocumentAlreadyPosted, rejected.Error!.Code);
    }

    private static string? TryResolveScratch()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (RequireSqlServer)
                Assert.Fail("Stock ledger SQL Server tests require ConnectionStrings__SqlServerTestConnection.");
            return null;
        }

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        if (!builder.InitialCatalog.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            if (RequireSqlServer)
                Assert.Fail("Stock ledger SQL Server tests require a database name containing 'test'.");
            return null;
        }

        return cs;
    }
}
