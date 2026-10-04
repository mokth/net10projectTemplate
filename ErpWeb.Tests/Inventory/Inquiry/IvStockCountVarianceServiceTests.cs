using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Inventory.Inquiry;
/// <summary>
/// The stock-count variance report (Phase 3, item 14). Every case in the plan's mandatory fixture list
/// is asserted here: zero system quantity against a positive count and the reverse, both zero, an
/// uncounted line excluded from the denominator, a stale line, and an empty denominator rendering "—"
/// rather than 0 or 100.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockCountVar)]
public class IvStockCountVarianceServiceTests : IAsyncLifetime
{
    private const string Menu = MenuCodes.InventoryStockCountVar;

    private static readonly DateTime Today = new(2026, 9, 24);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    private int _nextHeaderId;
    private short _nextLineNumber;

    public IvStockCountVarianceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ── The arithmetic and the direction ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10, 4, 6, IvStockCountVarianceDirections.Decrease)]    // system had more → write-down
    [InlineData(4, 10, -6, IvStockCountVarianceDirections.Increase)]   // found more than the system knew
    [InlineData(10, 10, 0, IvStockCountVarianceDirections.None)]
    public async Task Variance_IsSystemMinusPhysical_AndTheDirectionFollowsItsSign(
        double systemQty,
        double physicalQty,
        double expectedVariance,
        string expectedDirection)
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", (decimal)systemQty, (decimal)physicalQty);

        var row = Assert.Single((await Service().SearchVarianceAsync(Menu, Query())).VariancePage!.Rows);

        Assert.Equal((decimal)expectedVariance, row.Variance);
        Assert.Equal(expectedDirection, row.Direction);
        Assert.True(row.IsCounted);
    }

    [Fact]
    public async Task ZeroSystemQuantityWithAPositiveCount_IsAnIncrease_NotADivisionByZero()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", systemQty: 0m, physicalQty: 5m);

        var summary = (await Service().GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;
        var row = Assert.Single((await Service().SearchVarianceAsync(Menu, Query())).VariancePage!.Rows);

        Assert.Equal(-5m, row.Variance);
        Assert.Equal(IvStockCountVarianceDirections.Increase, row.Direction);
        Assert.Equal(1, summary.CountedLines);
        Assert.Equal(0, summary.ExactMatchLines);
        Assert.Equal(0m, summary.LineAccuracyPercent);
    }

    [Fact]
    public async Task PositiveSystemQuantityWithAZeroCount_IsADecrease()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", systemQty: 10m, physicalQty: 0m);

        var row = Assert.Single((await Service().SearchVarianceAsync(Menu, Query())).VariancePage!.Rows);

        Assert.Equal(10m, row.Variance);
        Assert.Equal(IvStockCountVarianceDirections.Decrease, row.Direction);
    }

    [Fact]
    public async Task BothZero_IsAnExactMatch()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", systemQty: 0m, physicalQty: 0m);

        var summary = (await Service().GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        Assert.Equal(1, summary.CountedLines);
        Assert.Equal(1, summary.ExactMatchLines);
        Assert.Equal(100m, summary.LineAccuracyPercent);
    }

    // ── Accuracy (D17) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UncountedLine_IsExcludedFromTheDenominator_NotCountedAsAgreement()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", 10m, 10m);      // exact
        await SeedLineAsync(sheet, "A101", 10m, 7m);       // wrong
        await SeedLineAsync(sheet, "A102", 10m, null);     // never counted

        var result = await Service().SearchVarianceAsync(Menu, Query());
        var summary = (await Service().GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        Assert.Equal(3, result.VariancePage!.TotalCount);
        Assert.Equal(2, summary.CountedLines);
        Assert.Equal(1, summary.ExactMatchLines);
        Assert.Equal(50m, summary.LineAccuracyPercent);

        var uncounted = result.VariancePage.Rows.Single(x => x.ICode == "A102");
        Assert.Null(uncounted.Variance);
        Assert.False(uncounted.IsCounted);
        Assert.Equal(IvStockCountVarianceDirections.NotCounted, uncounted.Direction);
    }

    [Fact]
    public async Task NothingCounted_ReportsNullAccuracy_NotZeroAndNotOneHundred()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", 10m, null);
        await SeedLineAsync(sheet, "A101", 20m, null);

        var summary = (await Service().GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        Assert.Equal(2, summary.LineCount);
        Assert.Equal(0, summary.CountedLines);
        Assert.Null(summary.LineAccuracyPercent);
    }

    // ── Which sheets are in scope ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(IvStockCountStatuses.Draft)]
    [InlineData(IvStockCountStatuses.Counted)]
    [InlineData(IvStockCountStatuses.RolledBack)]
    [InlineData(IvStockCountStatuses.Cancelled)]
    public async Task OnlyPostedSheets_AreReported(string status)
    {
        // A draft/counted sheet has no evidence, and a rolled-back sheet's adjustment is no longer in the
        // ledger — either would print a variance beside stock that does not exist.
        var sheet = await SeedSheetAsync("CC000001", status: status);
        await SeedLineAsync(sheet, "A100", 10m, 4m);

        var result = await Service().SearchVarianceAsync(Menu, Query());

        Assert.Empty(result.VariancePage!.Rows);
        Assert.Equal(0, result.VariancePage.TotalCount);
    }

    [Fact]
    public async Task REcognises_the_posted_sheet_and_its_batch_and_stale_disclosure()
    {
        var sheet = await SeedSheetAsync("CC000001", staleLines: 2, batchNo: 77);
        await SeedLineAsync(sheet, "A100", 10m, 4m);

        var row = Assert.Single((await Service().SearchVarianceAsync(Menu, Query())).VariancePage!.Rows);

        Assert.Equal("CC000001", row.CountNo);
        Assert.Equal(Today, row.CountDate);
        Assert.Equal(77, row.PostedBatchNo);
        Assert.Equal(2, row.PostedStaleLines);
    }

    // ── The summary's SQL definitions must not drift from the rows ──────────────────────────────────

    [Fact]
    public async Task Summary_AgreesWithTheRows_ForEveryDerivedMeasure()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", 10m, 10m);
        await SeedLineAsync(sheet, "A101", 10m, 7m);
        await SeedLineAsync(sheet, "A102", 5m, 9m);
        await SeedLineAsync(sheet, "A103", 3m, null);

        var service = Service();
        var rows = (await service.SearchVarianceAsync(Menu, Query())).VariancePage!.Rows;
        var summary = (await service.GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        var counted = rows.Where(x => x.IsCounted).ToList();

        Assert.Equal(rows.Count, summary.LineCount);
        Assert.Equal(counted.Count, summary.CountedLines);
        Assert.Equal(counted.Count(x => x.Variance == 0m), summary.ExactMatchLines);
        Assert.Equal(IvQty.Round(counted.Sum(x => x.Variance!.Value)), summary.NetVarianceQty);
        Assert.Equal(IvQty.Round(counted.Sum(x => Math.Abs(x.Variance!.Value))), summary.AbsVarianceQty);
    }

    [Fact]
    public async Task StaleLines_AreSummedPerSheet_NotPerLine()
    {
        var one = await SeedSheetAsync("CC000001", staleLines: 1);
        await SeedLineAsync(one, "A100", 10m, 9m);
        await SeedLineAsync(one, "A101", 10m, 9m);
        await SeedLineAsync(one, "A102", 10m, 9m);

        var two = await SeedSheetAsync("CC000002", staleLines: 2);
        await SeedLineAsync(two, "A100", 10m, 9m);

        var summary = (await Service().GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        // 1 + 2, NOT 1 x 3 lines + 2 x 1 line.
        Assert.Equal(3, summary.PostedStaleLines);
    }

    // ── Value ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VarianceValue_IsTheVarianceTimesTheSheetsOwnSnapshotPrice()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", systemQty: 10m, physicalQty: 4m, snapshotPrice: 2.5m);

        var row = Assert.Single((await Service().SearchVarianceAsync(Menu, Query())).VariancePage!.Rows);
        var summary = (await Service().GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        Assert.Equal(15m, row.VarianceValue);
        Assert.Equal(15m, summary.VarianceValue);
    }

    [Fact]
    public async Task VarianceValue_IsNullWhenNoCountedLineCarriesAPrice()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", 10m, 4m, snapshotPrice: null);

        var summary = (await Service().GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        // 0 would read as "no value impact"; the prices are unknown, which is a different statement.
        Assert.Null(summary.VarianceValue);
    }

    // ── Filters ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DateRange_IsInclusiveOfBothBounds()
    {
        var onFrom = await SeedSheetAsync("CC000001", countDate: Today);
        await SeedLineAsync(onFrom, "A100", 10m, 9m);

        var onTo = await SeedSheetAsync("CC000002", countDate: Today.AddDays(10));
        await SeedLineAsync(onTo, "A100", 10m, 9m);

        var outside = await SeedSheetAsync("CC000003", countDate: Today.AddDays(11));
        await SeedLineAsync(outside, "A100", 10m, 9m);

        var query = Query();
        query.DateFrom = Today;
        query.DateTo = Today.AddDays(10);

        var result = await Service().SearchVarianceAsync(Menu, query);

        Assert.Equal(2, result.VariancePage!.TotalCount);
    }

    [Fact]
    public async Task WarehouseClassStatusAndItemFilters_Apply()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", 10m, 9m, wh: "MAIN", status: "ACTIVE", cls: "RAW");
        await SeedLineAsync(sheet, "A101", 10m, 9m, wh: "WH2", status: "DAMAGED", cls: "FIN", lotNo: "L1");

        var service = Service();

        var byWarehouse = Query();
        byWarehouse.WhCode = "WH2";
        Assert.Equal("A101", Assert.Single((await service.SearchVarianceAsync(Menu, byWarehouse)).VariancePage!.Rows).ICode);

        var byClass = Query();
        byClass.IClassCode = "RAW";
        Assert.Equal("A100", Assert.Single((await service.SearchVarianceAsync(Menu, byClass)).VariancePage!.Rows).ICode);

        var byStatus = Query();
        byStatus.IStatuses = ["DAMAGED"];
        Assert.Equal("A101", Assert.Single((await service.SearchVarianceAsync(Menu, byStatus)).VariancePage!.Rows).ICode);

        var byItem = Query();
        byItem.ICode = "A100";
        Assert.Equal("A100", Assert.Single((await service.SearchVarianceAsync(Menu, byItem)).VariancePage!.Rows).ICode);

        // An empty status list means ALL statuses, never "none".
        Assert.Equal(2, (await service.SearchVarianceAsync(Menu, Query())).VariancePage!.TotalCount);
    }

    [Fact]
    public async Task UnknownSortField_FallsBackToTheDefaultOrder()
    {
        var sheet = await SeedSheetAsync("CC000001", countDate: Today.AddDays(-1));
        await SeedLineAsync(sheet, "A100", 10m, 9m);
        await SeedLineAsync(sheet, "A101", 10m, 9m);

        var query = Query();
        query.SortField = "'; DROP TABLE IvStockCountLine; --";

        var result = await Service().SearchVarianceAsync(Menu, query);

        Assert.True(result.Succeeded);
        // LIFO on CountNo then the sheet's own line order.
        Assert.Equal(["A100", "A101"], result.VariancePage!.Rows.Select(x => x.ICode));
    }

    [Fact]
    public async Task SummaryGridAndExport_AgreeAtTwoPageSizes()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", 10m, 9m);
        await SeedLineAsync(sheet, "A101", 10m, null);

        var service = Service();
        var summary = (await service.GetVarianceSummaryAsync(Menu, Query())).VarianceSummary!;

        foreach (var pageSize in new[] { 1, 50 })
        {
            var query = Query();
            query.Take = pageSize;

            var grid = await service.SearchVarianceAsync(Menu, query);
            var export = await service.ExportVarianceRowsAsync(Menu, query);

            Assert.Equal(summary.LineCount, grid.VariancePage!.TotalCount);
            Assert.Equal(summary.LineCount, export.VariancePage!.TotalCount);
        }
    }

    // ── Security ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_DeniedAccess_IsRejected()
    {
        var result = await Service(Deny(PermissionCodes.Access)).SearchVarianceAsync(Menu, Query());

        Assert.False(result.Succeeded);
        Assert.Contains("Not authorized", result.ErrorMessage);
    }

    [Fact]
    public async Task Search_OnTheCountMenuItself_IsRejected()
    {
        // The variance report is its own screen: holding INV_STOCK_COUNT must NOT read evidence through
        // this door any more than holding this menu may post a sheet.
        var result = await Service()
            .SearchVarianceAsync(MenuCodes.InventoryStockCount, Query());

        Assert.False(result.Succeeded);
        Assert.Contains("Unknown inquiry menu", result.ErrorMessage);
    }

    [Fact]
    public async Task Search_ChecksAccessOnTheVarianceMenu_AndNeverOnTheValueMenu()
    {
        var checks = new List<(string Menu, string Permission)>();
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string permission, CancellationToken _) =>
            {
                checks.Add((menu, permission));
                return true;
            });

        await Service(access).SearchVarianceAsync(Menu, Query());

        Assert.Contains((Menu, PermissionCodes.Access), checks);
        Assert.DoesNotContain(checks, c => c.Permission == PermissionCodes.ViewPrice);
    }

    [Fact]
    public async Task Search_OnAnotherCompany_SeesNothing()
    {
        var sheet = await SeedSheetAsync("CC000001");
        await SeedLineAsync(sheet, "A100", 10m, 4m);

        var result = await Service(company: "OTHER").SearchVarianceAsync(Menu, Query());

        Assert.True(result.Succeeded);
        Assert.Empty(result.VariancePage!.Rows);
    }

    [Fact]
    public void DirectionTokens_UseTheCountSheetsOwnSense()
    {
        // Mirrors IvStockCountService's preview: variance = system − physical, positive = write-down.
        Assert.Equal("DECREASE", IvStockCountVarianceDirections.Decrease);
        Assert.Equal("INCREASE", IvStockCountVarianceDirections.Increase);
        Assert.Equal("NONE", IvStockCountVarianceDirections.None);
        Assert.Equal("NOT COUNTED", IvStockCountVarianceDirections.NotCounted);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────────

    private static IvStockCountVarianceQuery Query() => new() { Take = 100 };

    private IvStockCountService Service(
        Mock<IAccessRightService>? access = null,
        string company = "DEMO",
        string branch = "HQ")
    {
        var rights = access ?? AllowAll();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var postingRepo = new IvStockPostingRepository();

        return new IvStockCountService(
            _factory,
            tenant,
            rights.Object,
            new RunningNumberService(),
            new FixedCurrentDateService(Today),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            new IvInventoryPostingService(
                _factory,
                tenant,
                rights.Object,
                postingRepo,
                new IvStockCommonRepository(_factory),
                new PoOrderRepository(),
                NullLogger<IvInventoryPostingService>.Instance),
            NullLogger<IvStockCountService>.Instance);
    }

    private static Mock<IAccessRightService> AllowAll()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static Mock<IAccessRightService> Deny(string denied)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string permission, CancellationToken _) =>
                !string.Equals(permission, denied, StringComparison.OrdinalIgnoreCase));
        return access;
    }

    private async Task<int> SeedSheetAsync(
        string countNo,
        string status = IvStockCountStatuses.Posted,
        int? staleLines = 0,
        int? batchNo = null,
        DateTime? countDate = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CountNo = countNo,
            CountDate = countDate ?? Today,
            Status = status,
            PostedBatchNo = batchNo,
            PostedStaleLines = staleLines
        };

        db.IvStockCountHdrs.Add(header);
        await db.SaveChangesAsync();
        _nextHeaderId = header.Id;
        _nextLineNumber = 0;
        return header.Id;
    }

    private async Task SeedLineAsync(
        int sheetId,
        string iCode,
        decimal systemQty,
        decimal? physicalQty,
        string wh = "MAIN",
        string loc = "BIN1",
        string lotNo = "",
        string status = "ACTIVE",
        string? cls = null,
        decimal? snapshotPrice = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvStockCountLines.Add(new IvStockCountLine
        {
            StockCountId = sheetId,
            LineNumber = ++_nextLineNumber,
            BalLocId = sheetId * 100 + _nextLineNumber,
            ICode = iCode,
            IDesc = $"{iCode} description",
            WHCode = wh,
            LocCode = loc,
            LotNo = lotNo,
            IStatus = status,
            IClassCode = cls,
            StdUom = "EA",
            SystemQty = systemQty,
            PhysicalQty = physicalQty,
            SnapshotUnitPrice = snapshotPrice,
            CountedBy = physicalQty is null ? null : "TESTER",
            CountedOn = physicalQty is null ? null : Today
        });

        await db.SaveChangesAsync();
    }
}
