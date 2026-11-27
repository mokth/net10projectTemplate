using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Moq;
using System.Data.Common;

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

        for (var attempt = 0; attempt < 5; attempt++)
        {
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_changed_or_deleted_after_load_rejects_stale_save(bool delete)
    {
        var cs = TryResolveScratch();
        if (cs is null) return;
        await using var host = await Host.CreateAsync(cs);
        var sut = host.CreateSut();
        var loaded = await sut.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        Assert.True(loaded.Succeeded, loaded.Message);
        var lineId = Assert.Single(loaded.Data!.Lines).Uid;
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var line = await db.PrDefBOMs.SingleAsync(x => x.Uid == lineId);
            if (delete) db.PrDefBOMs.Remove(line);
            else line.StdQty = 7m;
            await db.SaveChangesAsync();
        }

        loaded.Data.Remark = "stale edit";
        var save = await sut.SaveAsync(loaded.Data, false, false);
        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, save.ErrorCode);
        await using var check = await host.Factory.CreateDbContextAsync();
        var persisted = await check.PrDefBOMs.SingleOrDefaultAsync(x => x.Uid == lineId);
        if (delete) Assert.Null(persisted);
        else Assert.Equal(7m, persisted!.StdQty);
        Assert.NotEqual("stale edit", (await check.PrBomHdrs.SingleAsync(x => x.Uid == loaded.Data.BomHdrId)).Remark);
    }

    [Fact]
    public async Task Intentional_child_removal_succeeds_when_structure_is_unchanged()
    {
        var cs = TryResolveScratch();
        if (cs is null) return;
        await using var host = await Host.CreateAsync(cs);
        var sut = host.CreateSut();
        var loaded = await sut.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        Assert.True(loaded.Succeeded, loaded.Message);
        loaded.Data!.Lines.Add(new PrProductDefLineVm
        {
            ICode = host.ExtraComponent, StdQty = 2m, Warehouse = "WH01", SeqNo = 2
        });
        var expanded = await sut.SaveAsync(loaded.Data, false, false);
        Assert.True(expanded.Succeeded, expanded.Message);
        Assert.Equal(2, expanded.Data!.Lines.Count);
        expanded.Data.Lines.RemoveAll(x => x.ICode != host.ExtraComponent);
        var saved = await sut.SaveAsync(expanded.Data, false, false);
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(host.ExtraComponent, Assert.Single(saved.Data!.Lines).ICode);
    }

    [Fact]
    public async Task Existing_definition_without_original_structure_fingerprint_requires_reload()
    {
        var cs = TryResolveScratch();
        if (cs is null) return;
        await using var host = await Host.CreateAsync(cs);
        var sut = host.CreateSut();
        var loaded = await sut.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        Assert.True(loaded.Succeeded, loaded.Message);
        loaded.Data!.OriginalStructureFingerprint = null;
        var save = await sut.SaveAsync(loaded.Data, false, false);
        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, save.ErrorCode);
        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.Single(await db.PrDefBOMs.Where(x => x.BomHdrId == loaded.Data.BomHdrId).ToListAsync());
    }

    [Fact]
    public async Task Branch_default_inserted_after_load_is_not_removed_by_stale_save()
    {
        var cs = TryResolveScratch();
        if (cs is null) return;
        await using var host = await Host.CreateAsync(cs);
        var sut = host.CreateSut();
        var loaded = await sut.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        Assert.True(loaded.Succeeded, loaded.Message);
        var lineId = Assert.Single(loaded.Data!.Lines).Uid;
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var line = await db.PrDefBOMs.SingleAsync(x => x.Uid == lineId);
            db.PrBomMaterialBranchDefaults.Add(new PrBomMaterialBranchDefault
            {
                MaterialId = lineId, BranchCode = line.BranchCode ?? Host.Branch,
                WarehouseCode = line.Warehouse ?? "WH01", RowVersion = []
            });
            await db.SaveChangesAsync();
        }
        var save = await sut.SaveAsync(loaded.Data, false, false);
        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, save.ErrorCode);
        await using var check = await host.Factory.CreateDbContextAsync();
        Assert.Single(await check.PrBomMaterialBranchDefaults.Where(x => x.MaterialId == lineId).ToListAsync());
    }

    [Fact]
    public async Task Child_insert_during_save_is_serialized_and_preserved()
    {
        var cs = TryResolveScratch();
        if (cs is null) return;
        var barrier = new BomDeleteBarrier();
        await using var host = await Host.CreateAsync(cs, barrier);
        var sut = host.CreateSut();
        var loaded = await sut.GetAsync(host.ProdCode, PrProductDefinitionCodes.Standard);
        Assert.True(loaded.Succeeded, loaded.Message);
        loaded.Data!.Remark = "save with concurrent child";
        barrier.Arm();
        var saveTask = sut.SaveAsync(loaded.Data, false, false);
        try
        {
            await barrier.DeleteReached.WaitAsync(TimeSpan.FromSeconds(30));
            var insertTask = Task.Run(async () =>
            {
                await using var db = await host.Factory.CreateDbContextAsync();
                db.PrDefBOMs.Add(new PrDefBOM
                {
                    BomHdrId = loaded.Data.BomHdrId, CompanyCode = Host.Company,
                    ProdCode = host.ProdCode, ICode = host.ExtraComponent, StdQty = 1m,
                    StdUom = "PCS", SeqNo = 99, Warehouse = "WH01",
                    IssueMethod = PrMaterialIssueMethods.Manual,
                    SupplySource = PrMaterialSupplySources.Purchased, RowVersion = []
                });
                await db.SaveChangesAsync();
            });
            await barrier.InsertReached.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(300);
            Assert.False(insertTask.IsCompleted, "The concurrent insert should wait for the definition save.");
            barrier.Release();
            var saved = await saveTask.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(saved.Succeeded, saved.Message);
            await insertTask.WaitAsync(TimeSpan.FromSeconds(30));
            await using var check = await host.Factory.CreateDbContextAsync();
            Assert.True(await check.PrDefBOMs.AnyAsync(x => x.BomHdrId == loaded.Data.BomHdrId
                && x.ICode == host.ExtraComponent));
        }
        finally { barrier.Release(); }
    }

    private sealed class BomDeleteBarrier : DbCommandInterceptor
    {
        private int _armed;
        private readonly TaskCompletionSource _deleteReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _insertReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeleteReached => _deleteReached.Task;
        public Task InsertReached => _insertReached.Task;
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 2
                && command.CommandText.Contains("INSERT INTO [PrDefBOM]", StringComparison.OrdinalIgnoreCase)
                && _deleteReached.Task.IsCompleted)
                _insertReached.TrySetResult();
            if (command.CommandText.Contains("DELETE FROM [PrDefBOM]", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _armed, 2, 1) == 1)
            {
                _deleteReached.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            }
            return result;
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

        public static async Task<Host> CreateAsync(string connectionString, DbCommandInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            var options = builder.Options;
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
