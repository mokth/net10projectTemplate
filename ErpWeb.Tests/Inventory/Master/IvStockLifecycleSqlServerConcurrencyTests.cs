using ErpWeb.Core.Inventory;
using ErpWeb.Core.Inventory.Lifecycle;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Inventory.Master;

/// <summary>
/// SQL Server REQUIRED concurrency evidence for the item lifecycle.
///
/// SAFETY RAIL: runs against <c>ConnectionStrings:SqlServerTestConnection</c> and only when the database
/// name contains "test". A silent early return is reported as PASSED, so only a run with
/// <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> and <c>Skipped: 0</c> proves these actually executed.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMasters)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public class IvStockLifecycleSqlServerConcurrencyTests
{
    private const string Company = "DEMO";
    private static readonly DateTime Today = new(2026, 10, 28);

    private static string? ResolveScratchConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return null;
        }

        var database = new SqlConnectionStringBuilder(cs).InitialCatalog;
        if (string.IsNullOrWhiteSpace(database)
            || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cs;
    }

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"), "1", StringComparison.Ordinal);

    private static string? TryResolveScratch()
    {
        var cs = ResolveScratchConnectionString();
        if (cs is null && RequireSqlServer)
        {
            Assert.Fail(
                "Lifecycle SQL Server tests require ConnectionStrings__SqlServerTestConnection pointing at a "
                + "database whose name contains 'test'.");
        }

        return cs;
    }

    [Fact]
    public async Task Competing_lifecycle_commands_change_exactly_one_state_and_audit_once()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var item = await host.SeedDiscontinuedItemAsync();

        var retire = host.Service.ChangeLifecycleAsync(
            [new IvMasterKeyToken { Code = item.ICode, RowVersion = item.RowVersion }],
            IvStockItemLifecycleStatusCodes.Inactive,
            "Concurrent retirement test.",
            IvStockLifecycleSourceActions.Bulk);

        var reactivate = host.Service.ChangeLifecycleAsync(
            [new IvMasterKeyToken { Code = item.ICode, RowVersion = item.RowVersion }],
            IvStockItemLifecycleStatusCodes.Active,
            "Concurrent reactivation test.",
            IvStockLifecycleSourceActions.Bulk);

        var results = await Task.WhenAll(retire, reactivate);

        Assert.Equal(1, results.Count(x => x.Succeeded));
        var winner = results.Single(x => x.Succeeded);
        var loser = Assert.Single(results.Where(x => !x.Succeeded));
        Assert.Equal(IvMasterErrorCode.Concurrency, loser.ErrorCode);

        await using var db = await host.Factory.CreateDbContextAsync();
        var stored = await db.IvStockMasters
            .Where(x => x.CompanyCode == Company && x.ICode == item.ICode)
            .Select(x => new { x.IsActive, x.IsDiscontinued })
            .SingleAsync();

        Assert.Equal(1, await db.IvStockMasterLifecycleEvents
            .CountAsync(x => x.CompanyCode == Company && x.ICode == item.ICode));

        // The persisted state must match the single committed winner.
        if (winner.Data?.Items.Single().NewStatus == IvStockItemLifecycleStatusCodes.Inactive)
        {
            Assert.False(stored.IsActive);
            Assert.False(stored.IsDiscontinued);
        }
        else
        {
            Assert.True(stored.IsActive);
            Assert.False(stored.IsDiscontinued);
        }
    }

    [Fact]
    public async Task Lifecycle_command_waits_for_a_concurrent_demand_lock_on_the_same_item()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var item = await host.SeedDiscontinuedItemAsync();

        // A demand-creating transaction (SO/PO/WO path) holds the shared per-item lock.
        await using var demandConnection = new SqlConnection(host.ConnectionString);
        await demandConnection.OpenAsync();
        await using var demandTx = (SqlTransaction)await demandConnection.BeginTransactionAsync();

        await using (var cmd = demandConnection.CreateCommand())
        {
            cmd.Transaction = demandTx;
            cmd.CommandText =
                "SELECT ICode FROM dbo.IvStockMaster WITH (UPDLOCK, HOLDLOCK) " +
                "WHERE CompanyCode = @c AND ICode = @i";
            cmd.Parameters.AddWithValue("@c", Company);
            cmd.Parameters.AddWithValue("@i", item.ICode);
            await cmd.ExecuteScalarAsync();
        }

        var transition = host.Service.ChangeLifecycleAsync(
            [new IvMasterKeyToken { Code = item.ICode, RowVersion = item.RowVersion }],
            IvStockItemLifecycleStatusCodes.Inactive,
            "Waits for the demand lock.",
            IvStockLifecycleSourceActions.Bulk);

        var completedEarly = await Task.WhenAny(transition, Task.Delay(750)) == transition;
        Assert.False(completedEarly, "The lifecycle command must block behind the demand transaction's item lock.");

        await demandTx.CommitAsync();
        var result = await transition;
        Assert.True(result.Succeeded, result.Message);
    }

    private sealed class Host : IAsyncDisposable
    {
        private Host(IDbContextFactory<AppDbContext> factory, IvStockMasterService service, string connectionString)
        {
            Factory = factory;
            Service = service;
            ConnectionString = connectionString;
        }

        public IDbContextFactory<AppDbContext> Factory { get; }
        public IvStockMasterService Service { get; }
        public string ConnectionString { get; }

        public static async Task<Host> CreateAsync(string connectionString)
        {
            // The scratch database is shared with other SQL Server suites running in parallel. Never
            // EnsureDelete it — isolate this class on a uniquely named sibling database instead.
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = $"{new SqlConnectionStringBuilder(connectionString).InitialCatalog}_LC_"
                    + Guid.NewGuid().ToString("N")[..8]
            };
            var isolatedConnectionString = builder.ConnectionString;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(isolatedConnectionString)
                .Options;
            IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
            }

            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var service = new IvStockMasterService(
                factory,
                InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
                access.Object,
                new FixedCurrentDateService(Today),
                new IvStockMasterRepository(factory),
                new IvStockCommonRepository(factory));

            return new Host(factory, service, isolatedConnectionString);
        }

        public async Task<(string ICode, byte[] RowVersion)> SeedDiscontinuedItemAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            var iCode = "LC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = Company,
                ICode = iCode,
                IDesc = "Lifecycle concurrency item",
                StdUom = "EA",
                IsActive = true,
                IsDiscontinued = true,
                DiscontinuedAtUtc = DateTime.UtcNow.AddDays(-1),
                RowVersion = [1]
            });
            await db.SaveChangesAsync();

            var rowVersion = await db.IvStockMasters
                .Where(x => x.CompanyCode == Company && x.ICode == iCode)
                .Select(x => x.RowVersion)
                .SingleAsync();

            return (iCode, rowVersion);
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
