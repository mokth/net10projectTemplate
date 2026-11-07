using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Planning.Master;

/// <summary>
/// Product Definition SQL Server clone / save races. Skipped unless
/// <c>ConnectionStrings:SqlServerTestConnection</c> names a scratch database containing "test".
/// Set <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> to fail instead of skipping.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class PrProductDefSqlServerConcurrencyTests
{
    internal const string TestConnectionKey = "SqlServerTestConnection";

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    private static string? ResolveScratchConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString(TestConnectionKey);
        if (string.IsNullOrWhiteSpace(cs))
        {
            return null;
        }

        var database = ExtractDatabaseName(cs);
        if (database is null || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cs;
    }

    private static string? ExtractDatabaseName(string cs)
    {
        foreach (var part in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            foreach (var key in (string[])["Database", "Initial Catalog"])
            {
                if (trimmed.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(key.Length + 1)..].Trim();
                }
            }
        }

        return null;
    }

    private static string? TryResolveScratch()
    {
        var cs = ResolveScratchConnectionString();
        if (cs is null && RequireSqlServer)
        {
            Assert.Fail(
                "Product Definition SQL Server suite requires ConnectionStrings__SqlServerTestConnection "
                + "pointing at a database whose name contains 'test'.");
        }

        return cs;
    }

    [Fact]
    public async Task Two_users_cloning_the_same_definition_do_not_share_a_partial_version()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var first = host.CreateSut();
        var second = host.CreateSut();

        var cloneA = first.CreateNewVersionAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        var cloneB = second.CreateNewVersionAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        var results = await Task.WhenAll(cloneA, cloneB);

        Assert.Contains(results, x => x.Succeeded);
        var versions = results.Where(x => x.Succeeded).Select(x => x.Data!.Version).ToList();
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.All(results.Where(x => !x.Succeeded),
            x => Assert.Equal(IvMasterErrorCode.Concurrency, x.ErrorCode));

        await using var db = await host.Factory.CreateDbContextAsync();
        var headers = await db.PrBomHdrs
            .Where(x => x.CompanyCode == Host.Company && x.ProdCode == host.ProdCode)
            .ToListAsync();
        Assert.True(headers.Count >= 2);
        Assert.DoesNotContain(headers, x => x.Status == PrBomStatuses.Draft && !db.PrDefBOMs.Any(l => l.BomHdrId == x.Uid));
    }

    [Fact]
    public async Task Second_save_with_stale_header_token_fails()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var first = host.CreateSut();
        var second = host.CreateSut();
        var loadedA = await first.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        var loadedB = await second.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        Assert.True(loadedA.Succeeded && loadedB.Succeeded);

        loadedA.Data!.Remark = "A";
        var saveA = await first.SaveAsync(loadedA.Data, false, false);
        Assert.True(saveA.Succeeded, saveA.Message);

        loadedB.Data!.Remark = "B";
        var saveB = await second.SaveAsync(loadedB.Data, false, false);
        Assert.False(saveB.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, saveB.ErrorCode);
    }

    [Fact]
    public async Task Child_insert_after_load_is_not_silently_deleted()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var sut = host.CreateSut();
        var loaded = await sut.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        Assert.True(loaded.Succeeded);

        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var headerId = loaded.Data!.BomHdrId;
            db.PrDefBOMs.Add(new PrDefBOM
            {
                BomHdrId = headerId,
                CompanyCode = Host.Company,
                ProdCode = host.ProdCode,
                ICode = host.ExtraComponent,
                StdQty = 1m,
                StdUom = "PCS",
                SeqNo = 99,
                Warehouse = "WH01",
                IssueMethod = PrMaterialIssueMethods.Manual,
                SupplySource = PrMaterialSupplySources.Purchased,
                RowVersion = []
            });
            await db.SaveChangesAsync();
        }

        loaded.Data!.Remark = "replace-children";
        var save = await sut.SaveAsync(loaded.Data, false, false);
        await using var check = await host.Factory.CreateDbContextAsync();
        var extraStillThere = await check.PrDefBOMs.AnyAsync(x =>
            x.BomHdrId == loaded.Data.BomHdrId && x.ICode == host.ExtraComponent);

        if (save.Succeeded)
        {
            Assert.True(extraStillThere,
                "P1-E: a child inserted on a second connection must not be silently deleted by SaveAsync.");
        }
        else
        {
            Assert.Equal(IvMasterErrorCode.Concurrency, save.ErrorCode);
            Assert.True(extraStillThere);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public const string Company = "DEMO";
        public const string Branch = "HQ";

        private Host(IDbContextFactory<AppDbContext> factory, string prodCode, string extraComponent)
        {
            Factory = factory;
            ProdCode = prodCode;
            ExtraComponent = extraComponent;
        }

        public IDbContextFactory<AppDbContext> Factory { get; }
        public string ProdCode { get; }
        public string ExtraComponent { get; }

        public static async Task<Host> CreateAsync(string connectionString)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
            IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var prodCode = "PD" + suffix;
            var component = "RM" + suffix;
            var extra = "RX" + suffix;

            await using (var db = await factory.CreateDbContextAsync())
            {
                if (!await db.IvWarehouses.AnyAsync(x =>
                        x.CompanyCode == Company && x.BranchCode == Branch && x.WarehouseCode == "WH01"))
                {
                    db.IvWarehouses.Add(new IvWarehouse
                    {
                        CompanyCode = Company,
                        BranchCode = Branch,
                        WarehouseCode = "WH01",
                        WarehouseDesc = "Main",
                        IsActive = true
                    });
                }

                db.IvStockMasters.AddRange(
                    new IvStockMaster
                    {
                        CompanyCode = Company, ICode = prodCode, IDesc = "PD SQL FG",
                        StdUom = "PCS", MfgType = PrMfgTypes.Make, DefWarehouse = "WH01", IsActive = true
                    },
                    new IvStockMaster
                    {
                        CompanyCode = Company, ICode = component, IDesc = "PD SQL RM",
                        StdUom = "PCS", MfgType = PrMfgTypes.Buy, DefWarehouse = "WH01", IsActive = true
                    },
                    new IvStockMaster
                    {
                        CompanyCode = Company, ICode = extra, IDesc = "PD SQL extra",
                        StdUom = "PCS", MfgType = PrMfgTypes.Buy, DefWarehouse = "WH01", IsActive = true
                    });
                await db.SaveChangesAsync();
            }

            var host = new Host(factory, prodCode, extra);
            var sut = host.CreateSut();
            var save = await sut.SaveAsync(new PrProductDefEditVm
            {
                ProdCode = prodCode,
                DefinitionCode = PrProductDefinitionCodes.Standard,
                BaseQty = 1m,
                Lines =
                [
                    new PrProductDefLineVm { ICode = component, StdQty = 1m, Warehouse = "WH01" }
                ]
            }, true, false);
            Assert.True(save.Succeeded, save.Message);
            return host;
        }

        public PrProductDefService CreateSut()
        {
            var access = new Mock<IAccessRightService>();
            foreach (var permission in new[]
                     {
                         PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Edit, PermissionCodes.Delete
                     })
            {
                access.Setup(x => x.CanAsync(MenuCodes.PlanningProductDef, permission, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
            }

            return new PrProductDefService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(Company, Branch, "SITE"),
                access.Object,
                new IvUomConversionService(Factory));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
