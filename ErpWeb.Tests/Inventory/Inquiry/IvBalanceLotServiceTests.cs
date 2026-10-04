using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryBalanceLot)]
public class IvBalanceLotServiceTests : IAsyncLifetime
{
    /// <summary>The company-local "today" every test runs against.</summary>
    private static readonly DateTime Today = new(2026, 9, 24);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvBalanceLotServiceTests()
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

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();

        db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", WarehouseDesc = "Main store", IsActive = true });
        db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "WH2", WarehouseDesc = "Second store", IsActive = true });
        db.IvLocations.Add(new IvLocation { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", LocCode = "BIN1", LocDesc = "Bin one", IsActive = true });
        db.IvLocations.Add(new IvLocation { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", LocCode = "BIN2", LocDesc = "Bin two", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", StatusDesc = "Active", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "DAMAGED", StatusDesc = "Damaged", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "SCRAPS", StatusDesc = "Scraps", IsActive = true });

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "A100", IDesc = "Active stock item", StdUom = "EA",
            StockControl = true, IsActive = true, PurchasePrice = 5m
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "A101", IDesc = "Second item", StdUom = "EA",
            StockControl = true, IsActive = true, PurchasePrice = 7m
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "B200", IDesc = "Service item", StdUom = "EA",
            StockControl = false, IsActive = true, PurchasePrice = 1m
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "C300", IDesc = "Retired item", StdUom = "EA",
            StockControl = true, IsActive = false, PurchasePrice = 2m
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ── Tenant isolation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Company_isolation_excludes_another_company()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "OTHER", ICode = "A100", StdUom = "EA", StockControl = true, IsActive = true });
            db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "OTHER", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true });
            await db.SaveChangesAsync();
        }

        var demo = await SeedBalLocAsync("A100", 10m, company: "DEMO");
        var other = await SeedBalLocAsync("A100", 10m, company: "OTHER");

        var result = await CreateService().SearchAsync(Q());
        Assert.True(result.Succeeded, result.Message);

        var ids = result.Data!.Rows.Select(r => r.Id).ToList();
        Assert.Contains(demo, ids);
        Assert.DoesNotContain(other, ids);
    }

    [Fact]
    public async Task Branch_isolation_excludes_another_branch()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "DEMO", BranchCode = "BR2", WarehouseCode = "MAIN", IsActive = true });
            await db.SaveChangesAsync();
        }

        var hq = await SeedBalLocAsync("A100", 10m, branch: "HQ");
        var br2 = await SeedBalLocAsync("A100", 10m, branch: "BR2");

        var result = await CreateService().SearchAsync(Q());
        Assert.True(result.Succeeded, result.Message);

        var ids = result.Data!.Rows.Select(r => r.Id).ToList();
        Assert.Contains(hq, ids);
        Assert.DoesNotContain(br2, ids);
    }

    // ── Toggle polarity, pinned in BOTH directions ──────────────────────────────────────────────

    [Fact]
    public async Task IncludeZeroQty_polarity_pins_both_directions()
    {
        var zero = await SeedBalLocAsync("A100", 0m, loc: "BIN1");
        var positive = await SeedBalLocAsync("A101", 5m, loc: "BIN2");

        var include = await CreateService().SearchAsync(Q(includeZeroQty: true));
        Assert.Contains(include.Data!.Rows, r => r.Id == zero);
        Assert.Contains(include.Data!.Rows, r => r.Id == positive);

        var exclude = await CreateService().SearchAsync(Q(includeZeroQty: false));
        Assert.DoesNotContain(exclude.Data!.Rows, r => r.Id == zero);
        Assert.Contains(exclude.Data!.Rows, r => r.Id == positive);
    }

    [Fact]
    public async Task IncludeInactive_polarity_pins_both_directions()
    {
        var active = await SeedBalLocAsync("A100", 5m);
        var inactive = await SeedBalLocAsync("C300", 5m);

        var include = await CreateService().SearchAsync(Q(includeInactive: true));
        Assert.Contains(include.Data!.Rows, r => r.Id == active);
        Assert.Contains(include.Data!.Rows, r => r.Id == inactive);

        var exclude = await CreateService().SearchAsync(Q(includeInactive: false));
        Assert.Contains(exclude.Data!.Rows, r => r.Id == active);
        Assert.DoesNotContain(exclude.Data!.Rows, r => r.Id == inactive);
    }

    [Fact]
    public async Task IncludeNonStockControl_polarity_pins_both_directions()
    {
        var stock = await SeedBalLocAsync("A100", 5m);
        var nonStock = await SeedBalLocAsync("B200", 5m);

        var include = await CreateService().SearchAsync(Q(includeNonStockControl: true));
        Assert.Contains(include.Data!.Rows, r => r.Id == stock);
        Assert.Contains(include.Data!.Rows, r => r.Id == nonStock);

        var exclude = await CreateService().SearchAsync(Q(includeNonStockControl: false));
        Assert.Contains(exclude.Data!.Rows, r => r.Id == stock);
        Assert.DoesNotContain(exclude.Data!.Rows, r => r.Id == nonStock);
    }

    // ── Orphans (D15) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Orphan_pile_visible_with_IncludeInactive_and_hidden_without()
    {
        var orphan = await SeedOrphanBalLocAsync("GHOST", 5m);

        var visible = await CreateService().SearchAsync(Q(includeInactive: true));
        var orphanRow = Assert.Single(visible.Data!.Rows, r => r.Id == orphan);
        Assert.Null(orphanRow.IDesc);

        var hidden = await CreateService().SearchAsync(Q(includeInactive: false));
        Assert.DoesNotContain(hidden.Data!.Rows, r => r.Id == orphan);
    }

    // ── Expiry (R6) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expiry_yesterday_expired_today_and_future_and_null_are_not()
    {
        var yesterdayLot = await SeedLotAsync("A100", "LOT-EXP-Y", Today.AddDays(-1));
        var todayLot = await SeedLotAsync("A100", "LOT-EXP-T", Today);
        var tomorrowLot = await SeedLotAsync("A100", "LOT-EXP-TOM", Today.AddDays(1));
        var noExpiryLot = await SeedLotAsync("A100", "LOT-EXP-N", null);

        var y = await SeedBalLocAsync("A100", 1m, lotNo: "LOT-EXP-Y", lotId: yesterdayLot);
        var t = await SeedBalLocAsync("A100", 1m, lotNo: "LOT-EXP-T", lotId: todayLot);
        var tom = await SeedBalLocAsync("A100", 1m, lotNo: "LOT-EXP-TOM", lotId: tomorrowLot);
        var n = await SeedBalLocAsync("A100", 1m, lotNo: "LOT-EXP-N", lotId: noExpiryLot);

        var result = await CreateService().SearchAsync(Q(expiryBefore: Today));
        var ids = result.Data!.Rows.Select(r => r.Id).ToList();
        Assert.Contains(y, ids);
        Assert.DoesNotContain(t, ids);
        Assert.DoesNotContain(tom, ids);
        Assert.DoesNotContain(n, ids);
    }

    // ── Last movement (R13) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LastMovement_from_and_to_inclusive_to_plus_one_excluded()
    {
        var from = new DateTime(2026, 9, 1);
        var to = new DateTime(2026, 9, 10);

        var onFrom = await SeedBalLocAsync("A100", 1m, loc: "BIN1", transDate: from);
        var onTo = await SeedBalLocAsync("A100", 1m, loc: "BIN2", transDate: to);
        var after = await SeedBalLocAsync("A100", 1m, loc: "BIN3", transDate: to.AddDays(1));
        var before = await SeedBalLocAsync("A100", 1m, loc: "BIN4", transDate: from.AddDays(-1));

        var result = await CreateService().SearchAsync(Q(transDateFrom: from, transDateTo: to));
        var ids = result.Data!.Rows.Select(r => r.Id).ToList();
        Assert.Contains(onFrom, ids);
        Assert.Contains(onTo, ids);
        Assert.DoesNotContain(after, ids);
        Assert.DoesNotContain(before, ids);
    }

    // ── Sorting (R3) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_sort_field_falls_back_to_default_and_ignores_descending()
    {
        await SeedBalLocAsync("A101", 5m, loc: "BIN1");
        await SeedBalLocAsync("A100", 9m, loc: "BIN1");

        var result = await CreateService().SearchAsync(Q(sortField: "Nope", sortDescending: true));
        var rows = result.Data!.Rows;
        Assert.Equal(new[] { "A100", "A101" }, rows.Select(r => r.ICode).ToArray());
    }

    [Fact]
    public async Task Whitelisted_sort_and_descending()
    {
        await SeedBalLocAsync("A101", 5m, loc: "BIN1");
        await SeedBalLocAsync("A100", 9m, loc: "BIN1");

        var asc = await CreateService().SearchAsync(Q(sortField: nameof(IvBalanceLotRow.StdQty)));
        Assert.Equal(5m, asc.Data!.Rows[0].StdQty);

        var desc = await CreateService().SearchAsync(Q(sortField: nameof(IvBalanceLotRow.StdQty), sortDescending: true));
        Assert.Equal(9m, desc.Data!.Rows[0].StdQty);
    }

    // ── Status (R11, D19) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_status_returns_all_including_scraps_and_single_restricts()
    {
        var active = await SeedBalLocAsync("A100", 1m, status: "ACTIVE");
        var scraps = await SeedBalLocAsync("A101", 1m, status: "SCRAPS");

        var all = await CreateService().SearchAsync(Q());
        Assert.Contains(all.Data!.Rows, r => r.Id == active);
        Assert.Contains(all.Data!.Rows, r => r.Id == scraps);

        var onlyActive = await CreateService().SearchAsync(Q(statuses: ["ACTIVE"]));
        Assert.Contains(onlyActive.Data!.Rows, r => r.Id == active);
        Assert.DoesNotContain(onlyActive.Data!.Rows, r => r.Id == scraps);
    }

    // ── SearchText (D11) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchText_matches_code_description_warehouse_bin_and_lot()
    {
        var byCode = await SeedBalLocAsync("A100", 1m);
        await SeedBalLocAsync("A101", 1m);
        var byWh = await SeedBalLocAsync("A101", 1m, wh: "WH2");
        var byLoc = await SeedBalLocAsync("A101", 1m, loc: "BIN2");
        var byLot = await SeedBalLocAsync("A101", 1m, lotNo: "LOT-ALPHA");

        var code = await CreateService().SearchAsync(Q(searchText: "A100"));
        Assert.Equal(new[] { byCode }, code.Data!.Rows.Select(r => r.Id).ToArray());

        var wh = await CreateService().SearchAsync(Q(searchText: "WH2"));
        Assert.Equal(new[] { byWh }, wh.Data!.Rows.Select(r => r.Id).ToArray());

        var loc = await CreateService().SearchAsync(Q(searchText: "BIN2"));
        Assert.Equal(new[] { byLoc }, loc.Data!.Rows.Select(r => r.Id).ToArray());

        var lot = await CreateService().SearchAsync(Q(searchText: "ALPHA"));
        Assert.Equal(new[] { byLot }, lot.Data!.Rows.Select(r => r.Id).ToArray());
    }

    // ── LotNo source (D21) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LotNo_comes_from_IvBalLoc_not_IvLot()
    {
        var lot = await SeedLotAsync("A100", "IVLOT-DIFFERENT", null);
        var bal = await SeedBalLocAsync("A100", 1m, lotNo: "BAL-LOT", lotId: lot);

        var result = await CreateService().SearchAsync(Q());
        var row = Assert.Single(result.Data!.Rows, r => r.Id == bal);
        Assert.Equal("BAL-LOT", row.LotNo);
    }

    // ── Quantity range ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MinQty_and_MaxQty_are_inclusive()
    {
        var low = await SeedBalLocAsync("A100", 1m, loc: "BIN1");
        var mid = await SeedBalLocAsync("A100", 5m, loc: "BIN2");
        var high = await SeedBalLocAsync("A100", 9m, loc: "BIN3");

        var result = await CreateService().SearchAsync(Q(minQty: 5m, maxQty: 5m));
        Assert.Equal(new[] { mid }, result.Data!.Rows.Select(r => r.Id).ToArray());
        Assert.DoesNotContain(result.Data!.Rows, r => r.Id == low);
        Assert.DoesNotContain(result.Data!.Rows, r => r.Id == high);
    }

    // ── Summary equals the predicate (R4, N5, R10) ───────────────────────────────────────────────

    [Fact]
    public async Task Summary_matches_the_grid_predicate_at_page_size_1_and_50()
    {
        var lot = await SeedLotAsync("A100", "LOT-EXP", Today.AddDays(-1));
        await SeedBalLocAsync("A100", 2m, unitPrice: 2.5m);
        await SeedBalLocAsync("A100", 3m, unitPrice: 2.5m, lotId: lot, lotNo: "LOT-EXP");
        await SeedBalLocAsync("A101", 0m);
        await SeedBalLocAsync("A101", 4m, unitPrice: 1.25m, loc: "BIN2");
        await SeedBalLocAsync("A101", 0m, loc: "BIN3");

        var svc = CreateService();
        var query = Q();

        var summary = await svc.GetSummaryAsync(query);
        Assert.True(summary.Succeeded, summary.Message);

        var page1 = await svc.SearchAsync(Q(take: 1));
        var page50 = await svc.SearchAsync(Q(take: 50));

        Assert.Equal(page1.Data!.TotalCount, summary.Data!.TotalRows);
        Assert.Equal(page50.Data!.TotalCount, summary.Data!.TotalRows);
        Assert.Equal(9m, summary.Data!.TotalQty);          // 2 + 3 + 0 + 4
        Assert.Equal(2, summary.Data!.ZeroQtyRowCount);
        Assert.Equal(1, summary.Data!.ExpiredRowCount);

        // 2*2.5 + 3*2.5 + 0*7 + 4*1.25 = 5 + 7.5 + 0 + 5 = 17.5
        Assert.Equal(17.5m, summary.Data!.TotalValue);
    }

    // ── Security ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Access_denied_returns_AccessDenied_with_no_data()
    {
        await SeedBalLocAsync("A100", 1m);

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateService(access: access).SearchAsync(Q());
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task CanViewPrice_false_returns_null_value_on_grid_and_export()
    {
        await SeedBalLocAsync("A100", 2m, unitPrice: 3m);

        var svc = CreateService(canViewPrice: false);
        var grid = await svc.SearchAsync(Q());
        Assert.All(grid.Data!.Rows, r => Assert.Null(r.Value));

        var export = await svc.ExportRowsAsync(Q(take: 50_000));
        Assert.All(export.Data!.Rows, r => Assert.Null(r.Value));
    }

    [Fact]
    public async Task CanViewPrice_true_returns_the_documented_value()
    {
        await SeedBalLocAsync("A100", 2m, unitPrice: 3m);       // 2 * 3 = 6
        await SeedBalLocAsync("A101", 2m, unitPrice: null);     // 2 * 7 (PurchasePrice) = 14

        var result = await CreateService(canViewPrice: true).SearchAsync(Q());
        var rows = result.Data!.Rows.ToList();
        var byCode = rows.ToDictionary(r => r.ICode);
        Assert.Equal(6m, byCode["A100"].Value);
        Assert.Equal(14m, byCode["A101"].Value);
    }

    [Fact]
    public async Task ModifiedBy_is_always_null()
    {
        await SeedBalLocAsync("A100", 1m);

        var result = await CreateService().SearchAsync(Q());
        Assert.All(result.Data!.Rows, r => Assert.Null(r.ModifiedBy));
    }

    [Fact]
    public async Task Export_uses_the_same_composition_as_the_grid()
    {
        await SeedBalLocAsync("A100", 5m);
        await SeedBalLocAsync("A101", 3m);

        var svc = CreateService();
        var grid = await svc.SearchAsync(Q(take: 50));
        var export = await svc.ExportRowsAsync(Q(take: 50_000));

        Assert.Equal(grid.Data!.TotalCount, export.Data!.TotalCount);
        Assert.Equal(grid.Data!.Rows.Select(r => r.Id), export.Data!.Rows.Select(r => r.Id));
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────

    private static ICurrentDateService Clock(DateTime today) => new FixedCurrentDateService(today);

    private static IvBalanceLotQuery Q(
        bool includeZeroQty = true,
        bool includeInactive = true,
        bool includeNonStockControl = true,
        IReadOnlyList<string>? statuses = null,
        string? searchText = null,
        string? sortField = null,
        bool sortDescending = false,
        DateTime? expiryBefore = null,
        DateTime? transDateFrom = null,
        DateTime? transDateTo = null,
        decimal? minQty = null,
        decimal? maxQty = null,
        int? take = null) =>
        new()
        {
            IncludeZeroQty = includeZeroQty,
            IncludeInactive = includeInactive,
            IncludeNonStockControl = includeNonStockControl,
            IStatuses = statuses ?? [],
            SearchText = searchText,
            SortField = sortField,
            SortDescending = sortDescending,
            ExpiryBefore = expiryBefore,
            TransDateFrom = transDateFrom,
            TransDateTo = transDateTo,
            MinQty = minQty,
            MaxQty = maxQty,
            Take = take ?? 50
        };

    private IvBalanceLotService CreateService(
        ICurrentDateService? clock = null,
        Mock<IAccessRightService>? access = null,
        bool canViewPrice = true,
        string company = "DEMO",
        string branch = "HQ")
    {
        access ??= Access();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CanViewPrice).Returns(canViewPrice);

        return new IvBalanceLotService(
            new IvStockCommonRepository(_factory),
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            access.Object,
            clock ?? Clock(Today),
            currentUser.Object);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private async Task<int> SeedLotAsync(string iCode, string lotNo, DateTime? expiry)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lot = new IvLot
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            LotNo = lotNo,
            ExpiryDate = expiry,
            IsActive = true
        };
        db.IvLots.Add(lot);
        await db.SaveChangesAsync();
        return lot.Id;
    }

    /// <summary>
    /// Manufactures a pile whose item master is MISSING (D15 orphan). The EF model enforces the
    /// StockMaster FK, so enforcement is switched off for this one insert and restored immediately.
    /// </summary>
    private async Task<int> SeedOrphanBalLocAsync(string iCode, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = iCode,
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = "",
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA"
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        return bal.Id;
    }

    private async Task<int> SeedBalLocAsync(
        string iCode,
        decimal qty,
        string company = "DEMO",
        string branch = "HQ",
        string wh = "MAIN",
        string loc = "BIN1",
        string lotNo = "",
        string status = "ACTIVE",
        decimal? unitPrice = null,
        int? lotId = null,
        DateTime? transDate = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = company,
            BranchCode = branch,
            ICode = iCode,
            WhCode = wh,
            LocCode = loc,
            LotNo = lotNo,
            IStatus = status,
            StdQty = qty,
            StdUom = "EA",
            UnitPrice = unitPrice,
            LotId = lotId,
            TransDate = transDate
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

}
