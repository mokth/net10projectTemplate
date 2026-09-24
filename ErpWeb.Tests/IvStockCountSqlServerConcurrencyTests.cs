using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server REQUIRED concurrency/integrity tests for the stock-count document.
///
/// SAFETY RAIL: these run against <c>ConnectionStrings:SqlServerTestConnection</c> and only when the
/// database name contains "test" — the inventory suites that use <c>DefaultConnection</c> write to the
/// LIVE ERPWeb database, which is not acceptable for throwaway count documents.
///
/// A silent early return is reported as PASSED, so only a run with
/// <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> and <c>Skipped: 0</c> proves these actually executed.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockCount)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public class IvStockCountSqlServerConcurrencyTests
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";
    private static readonly DateTime Today = new(2026, 9, 24);

    // ── Connection resolution (mirrors PoCdnSqlServerConcurrencyTests) ────────────────────────────

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

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"), "1", StringComparison.Ordinal);

    /// <summary>Returns null when the scratch database is unavailable and the run may be skipped.</summary>
    private static string? TryResolveScratch()
    {
        var cs = ResolveScratchConnectionString();
        if (cs is null && RequireSqlServer)
        {
            Assert.Fail(
                "TC-SC requires a scratch SQL Server: set ConnectionStrings__SqlServerTestConnection to a "
                + "database whose name contains 'test'.");
        }

        return cs;
    }

    // ── Cases ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_concurrent_posts_of_one_count_leave_exactly_one_batch()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var balId = await host.SeedBalanceAsync(100m);
        var sheet = await host.NewCountedSheetAsync(balId, physicalQty: 70m);

        var results = await Task.WhenAll(
            host.Service.PostAsync(sheet.Id),
            host.Service.PostAsync(sheet.Id));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        var failure = Assert.Single(results.Where(r => !r.Succeeded));
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));

        await using var db = host.Factory.CreateDbContext();
        var winner = results.Single(r => r.Succeeded);
        Assert.Equal(1, await db.IvTrxHistories.CountAsync(x => x.ICode == host.ICode));
        Assert.Equal(
            IvBatchStatuses.Posted,
            await db.IvTrxBatches.Where(x => x.BatchNo == winner.PostedBatchNo).Select(x => x.BatchStatus).SingleAsync());
        Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(IvStockCountStatuses.Posted, (await db.IvStockCountHdrs.SingleAsync(x => x.Id == sheet.Id)).Status);
    }

    [Fact]
    public async Task Two_savers_with_the_same_rowversion_leave_one_winner()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var balId = await host.SeedBalanceAsync(100m);
        var sheetId = await host.NewSheetAsync(balId);
        var token = (await host.Service.GetAsync(await host.CountNoAsync(sheetId))).Document!.RowVersion;

        var results = await Task.WhenAll(
            host.Service.UpdateAsync(sheetId, Request(token, "first")),
            host.Service.UpdateAsync(sheetId, Request(token, "second")));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        var loser = Assert.Single(results.Where(r => !r.Succeeded));
        Assert.Contains("changed by another user", loser.ErrorMessage);
    }

    [Fact]
    public async Task A_count_edit_after_the_post_is_refused()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var balId = await host.SeedBalanceAsync(100m);
        var sheet = await host.NewCountedSheetAsync(balId, physicalQty: 70m);
        var token = (await host.Service.GetAsync(sheet.CountNo)).Document!.RowVersion;

        var post = await host.Service.PostAsync(sheet.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);

        var lateEdit = await host.Service.SaveCountsAsync(
            sheet.Id,
            [new IvStockCountLineCountRequest { BalLocId = balId, PhysicalQty = 60m }],
            token);

        Assert.False(lateEdit.Succeeded);
    }

    [Fact]
    public async Task A_count_post_and_a_stock_adjustment_post_on_one_pile_serialise_without_deadlock()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var balId = await host.SeedBalanceAsync(100m);
        var sheet = await host.NewCountedSheetAsync(balId, physicalQty: 70m);
        var adj = await host.NewAdjustmentAsync(balId, adjustQty: -10m);

        var countPost = host.Service.PostAsync(sheet.Id);
        var adjPost = host.Adjustment.PostAsync([adj]);

        await Task.WhenAll(countPost, adjPost);

        var countResult = await countPost;
        var adjResult = await adjPost;
        Assert.True(countResult.Succeeded, countResult.ErrorMessage);
        Assert.True(adjResult.Succeeded, adjResult.ErrorMessage);

        await using var db = host.Factory.CreateDbContext();
        Assert.Equal(60m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(2, await db.IvTrxHistories.CountAsync(x => x.ICode == host.ICode));
    }

    [Fact]
    public async Task The_unique_bal_loc_index_rejects_a_duplicate_line_in_one_sheet()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        await using var host = await Host.CreateAsync(cs);
        var balId = await host.SeedBalanceAsync(100m);
        var sheetId = await host.NewSheetAsync(balId);

        await using var db = host.Factory.CreateDbContext();
        db.IvStockCountLines.Add(new IvStockCountLine
        {
            StockCountId = sheetId,
            LineNumber = 2,
            BalLocId = balId,
            ICode = host.ICode,
            IStatus = "ACTIVE",
            SystemQty = 100m
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(SqlErrorClassifier.IsUniqueViolation(ex), $"Expected a unique violation, got: {ex}");
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────

    private static IvStockCountSaveRequest Request(string? rowVersion, string remark) => new()
    {
        CountDate = Today,
        WHCode = "MAIN",
        IncludeZeroQty = true,
        Remark = remark,
        RowVersion = rowVersion
    };

    private sealed class Host : IAsyncDisposable
    {
        private Host(IDbContextFactory<AppDbContext> factory, string iCode, IvStockCountService service, IvStockAdjustmentService adjustment)
        {
            Factory = factory;
            ICode = iCode;
            Service = service;
            Adjustment = adjustment;
        }

        public IDbContextFactory<AppDbContext> Factory { get; }
        public string ICode { get; }
        public IvStockCountService Service { get; }
        public IvStockAdjustmentService Adjustment { get; }

        public static async Task<Host> CreateAsync(string connectionString)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
            IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

            await using (var db = await factory.CreateDbContextAsync())
            {
                // Builds the CURRENT model on a fresh database, which also cross-checks the EF model
                // against the create-iv-stock-count.sql migration.
                await db.Database.EnsureCreatedAsync();
            }

            var iCode = "SC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            await using (var db = await factory.CreateDbContextAsync())
            {
                if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == Company && x.BranchCode == Branch && x.WarehouseCode == "MAIN"))
                {
                    db.IvWarehouses.Add(new IvWarehouse
                    {
                        CompanyCode = Company,
                        BranchCode = Branch,
                        WarehouseCode = "MAIN",
                        IsActive = true
                    });
                }

                if (!await db.IvStatuses.AnyAsync(x => x.CompanyCode == Company && x.IStatus == "ACTIVE"))
                {
                    db.IvStatuses.Add(new IvStatus { CompanyCode = Company, IStatus = "ACTIVE", IsActive = true });
                }

                if (!await db.IvClasses.AnyAsync(x => x.CompanyCode == Company && x.IClassCode == "RAW"))
                {
                    db.IvClasses.Add(new IvClass { CompanyCode = Company, IClassCode = "RAW", IsActive = true });
                }

                if (!await db.MsUoms.AnyAsync(x => x.CompanyCode == Company && x.UomCode == "EA"))
                {
                    db.MsUoms.Add(new MsUom { CompanyCode = Company, UomCode = "EA", IsActive = true });
                }

                db.IvStockMasters.Add(new IvStockMaster
                {
                    CompanyCode = Company,
                    ICode = iCode,
                    IDesc = "Stock count concurrency item",
                    IClassCode = "RAW",
                    StdUom = "EA",
                    StockControl = true,
                    LotControl = false,
                    IsActive = true,
                    PurchasePrice = 5m
                });

                await db.SaveChangesAsync();
            }

            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var tenant = InventoryTenantTestHelper.CreateTenantContext();
            var clock = new FixedCurrentDateService(Today);
            var postingRepo = new IvStockPostingRepository();
            var common = new IvStockCommonRepository(factory);
            var posting = new IvInventoryPostingService(
                factory, tenant, access.Object, postingRepo, common,
                new PoOrderRepository(), NullLogger<IvInventoryPostingService>.Instance);

            var service = new IvStockCountService(
                factory, tenant, access.Object, new RunningNumberService(), clock,
                common, new IvStockTransactionRepository(), postingRepo, posting,
                NullLogger<IvStockCountService>.Instance);

            var adjustment = new IvStockAdjustmentService(
                factory, tenant, access.Object, new RunningNumberService(),
                new IvStockMasterRepository(factory), common, new IvStockTransactionRepository(),
                postingRepo, posting, NullLogger<IvStockAdjustmentService>.Instance);

            return new Host(factory, iCode, service, adjustment);
        }

        public async Task<int> SeedBalanceAsync(decimal qty)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var bal = new IvBalLoc
            {
                CompanyCode = Company,
                BranchCode = Branch,
                ICode = ICode,
                WhCode = "MAIN",
                LocCode = "BIN1",
                LotNo = string.Empty,
                IStatus = "ACTIVE",
                StdQty = qty,
                StdUom = "EA"
            };
            db.IvBalLocs.Add(bal);
            await db.SaveChangesAsync();
            return bal.Id;
        }

        public string CountNo(int id) => $"SC{id}";

        /// <summary>Resolves the generated count number from the database (never guessed).</summary>
        public async Task<string> CountNoAsync(int id)
        {
            await using var db = await Factory.CreateDbContextAsync();
            return await db.IvStockCountHdrs.Where(x => x.Id == id).Select(x => x.CountNo).SingleAsync();
        }

        public async Task<int> NewSheetAsync(int balLocId)
        {
            var save = await Service.SaveAsync(new IvStockCountSaveRequest
            {
                CountDate = Today,
                WHCode = "MAIN",
                ICodes = [ICode],
                IncludeZeroQty = true
            });
            Assert.True(save.Succeeded, save.ErrorMessage);

            var generated = await Service.GenerateAsync(save.Id, discardCounts: false);
            Assert.True(generated.Succeeded, generated.ErrorMessage);
            _ = balLocId;
            return save.Id;
        }

        public async Task<(int Id, string CountNo)> NewCountedSheetAsync(int balLocId, decimal physicalQty)
        {
            var id = await NewSheetAsync(balLocId);
            var countNo = await CountNoAsync(id);
            var doc = (await Service.GetAsync(countNo)).Document!;
            var line = Assert.Single(doc.Lines);

            var saved = await Service.SaveCountsAsync(
                id,
                [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = physicalQty }],
                doc.RowVersion);
            Assert.True(saved.Succeeded, saved.ErrorMessage);

            return (id, countNo);
        }

        public async Task<int> NewAdjustmentAsync(int balLocId, decimal adjustQty)
        {
            var saved = await Adjustment.SaveNewAsync(new IvStockAdjustmentSaveRequest
            {
                TrxDate = Today,
                Lines =
                [
                    new IvStockAdjustmentLineRequest
                    {
                        BalLocId = balLocId,
                        ICode = ICode,
                        Warehouse = "MAIN",
                        Location = "BIN1",
                        LotNo = string.Empty,
                        AdjustQty = adjustQty,
                        Uom = "EA",
                        IClassCode = "RAW",
                        IStatus = "ACTIVE",
                        UnitPrice = 5m,
                        Reason = IvAdjustmentReasons.Count
                    }
                ]
            });

            Assert.True(saved.Succeeded, saved.ErrorMessage);
            return saved.BatchNo;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
