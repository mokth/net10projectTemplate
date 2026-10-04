using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Inventory.Inquiry;
/// <summary>
/// Shared SQLite fixture for the inquiry-suite tests (transaction inquiry, stock card). Owns the
/// connection, the master data and the row seeders so the two test classes cannot drift apart.
/// </summary>
internal sealed class IvHistoryTestDb : IAsyncDisposable
{
    public const string Company = "DEMO";
    public const string Branch = "HQ";

    public SqliteConnection Connection { get; }

    public IDbContextFactory<AppDbContext> Factory { get; }

    private int _nextBatchNo;

    public IvHistoryTestDb()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Connection)
            .Options;
        Factory = new TestDbContextFactory(options);

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();

        db.IvWarehouses.Add(new IvWarehouse { CompanyCode = Company, BranchCode = Branch, WarehouseCode = "MAIN", WarehouseDesc = "Main store", IsActive = true });
        db.IvWarehouses.Add(new IvWarehouse { CompanyCode = Company, BranchCode = Branch, WarehouseCode = "WH2", WarehouseDesc = "Second store", IsActive = true });
        db.IvLocations.Add(new IvLocation { CompanyCode = Company, BranchCode = Branch, WarehouseCode = "MAIN", LocCode = "BIN1", LocDesc = "Bin one", IsActive = true });
        db.IvLocations.Add(new IvLocation { CompanyCode = Company, BranchCode = Branch, WarehouseCode = "MAIN", LocCode = "BIN2", LocDesc = "Bin two", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = Company, IStatus = "ACTIVE", StatusDesc = "Active", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = Company, IStatus = "DAMAGED", StatusDesc = "Damaged", IsActive = true });

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = Company, ICode = "A100", IDesc = "First item", StdUom = "EA",
            StockControl = true, IsActive = true, PurchasePrice = 5m
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = Company, ICode = "A101", IDesc = "Second item", StdUom = "EA",
            StockControl = true, IsActive = true, PurchasePrice = 7m
        });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    /// <summary>
    /// Seeds one posted movement line. Defaults describe an "increase" leg (to-side only) of 10 EA.
    ///
    /// <para>
    /// <c>UQ_IvTrxHistory_Company_Branch_Batch_Line</c> is unique on
    /// <c>(CompanyCode, BranchCode, BatchNo, TrxLineNo)</c>, so each seeded row gets its own batch
    /// number unless the caller pins one — exactly as the posting engine does.
    /// </para>
    /// </summary>
    public async Task<int> SeedHistoryAsync(
        string iCode = "A100",
        string trxType = "MR",
        DateTime? trxDtTime = null,
        decimal? frStdQty = null,
        decimal? toStdQty = 10m,
        string? frWh = null,
        string? frLoc = null,
        string? frLot = null,
        string? toWh = "MAIN",
        string? toLoc = "BIN1",
        string? toLot = null,
        string? status = "ACTIVE",
        decimal? unitPrice = null,
        string? remarks = null,
        int? batchNo = null,
        string company = Company,
        string branch = Branch,
        string? refNo = null,
        int? toBalLocId = null,
        int? fromBalLocId = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var row = new IvTrxHistory
        {
            CompanyCode = company,
            BranchCode = branch,
            BatchNo = batchNo ?? ++_nextBatchNo,
            TrxLineNo = 1,
            TrxDtTime = trxDtTime ?? new DateTime(2026, 9, 1, 10, 0, 0),
            TrxType = trxType,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = refNo,
            ICode = iCode,
            IDesc = iCode == "A100" ? "First item" : "Second item",
            ToBalLocId = toBalLocId,
            FromBalLocId = fromBalLocId,
            FrWarehouse = frWh,
            FrLocation = frLoc,
            FrLotNo = frLot,
            FrStdQty = frStdQty,
            FrStdUom = frStdQty is null ? null : "EA",
            ToWarehouse = toWh,
            ToLocation = toLoc,
            ToLotNo = toLot,
            ToStdQty = toStdQty,
            ToStdUom = toStdQty is null ? null : "EA",
            IStatus = status,
            Remarks = remarks,
            UnitPrice = unitPrice
        };

        db.IvTrxHistories.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    public async Task<int> SeedPileAsync(
        string iCode = "A100",
        decimal qty = 10m,
        string wh = "MAIN",
        string loc = "BIN1",
        string lotNo = "",
        string status = "ACTIVE",
        string company = Company,
        string branch = Branch,
        int? lotId = null,
        decimal? unitPrice = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var pile = new IvBalLoc
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
            TransDate = new DateTime(2026, 8, 1),
            LotId = lotId,
            UnitPrice = unitPrice
        };

        db.IvBalLocs.Add(pile);
        await db.SaveChangesAsync();
        return pile.Id;
    }

    public IvTrxHistoryService CreateService(
        Mock<IAccessRightService>? access = null,
        string company = Company,
        string branch = Branch) =>
        new(
            new IvStockHistoryRepository(Factory),
            new IvStockInquiryRepository(Factory),
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            (access ?? Access()).Object);

    /// <summary>Every permission allowed.</summary>
    public static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    /// <summary>Every permission allowed EXCEPT <paramref name="denied"/>.</summary>
    public static Mock<IAccessRightService> Deny(string denied)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string permission, CancellationToken _) =>
                !string.Equals(permission, denied, StringComparison.OrdinalIgnoreCase));
        return access;
    }

    /// <summary>Records which (menu, permission) pairs were checked, so "checks the screen's own menu" is pinnable.</summary>
    public static (Mock<IAccessRightService> Mock, List<(string Menu, string Permission)> Checks) RecordingAccess()
    {
        var checks = new List<(string, string)>();
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string permission, CancellationToken _) =>
            {
                checks.Add((menu, permission));
                return true;
            });
        return (access, checks);
    }

    // ── Phase 2 seeders (stock alerts, lot inquiry, stock summary) ─────────────────────────────────

    /// <summary>Seeds an inventory lot and returns its surrogate id.</summary>
    public async Task<int> SeedLotAsync(
        string iCode = "A100",
        string lotNo = "L1",
        DateTime? receiptDate = null,
        DateTime? expiryDate = null,
        DateTime? mfgDate = null,
        string? sourceType = "GR",
        string? sourceDocNo = "GR-0001",
        string? supplierCode = "SUP1",
        string? qcStatus = "PASS",
        bool isActive = true,
        string company = Company)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var lot = new IvLot
        {
            CompanyCode = company,
            ICode = iCode,
            LotNo = lotNo,
            ReceiptDate = receiptDate,
            ExpiryDate = expiryDate,
            MfgDate = mfgDate,
            SourceType = sourceType,
            SourceDocNo = sourceDocNo,
            SupplierCode = supplierCode,
            QcStatus = qcStatus,
            IsActive = isActive
        };

        db.IvLots.Add(lot);
        await db.SaveChangesAsync();
        return lot.Id;
    }

    /// <summary>Sets the reorder thresholds a LOW/OVER alert is measured against. NULL or 0 never alerts.</summary>
    public async Task SetThresholdsAsync(string iCode, decimal? min, decimal? max)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == Company && x.ICode == iCode);
        item.MinStock = min;
        item.MaxStock = max;
        await db.SaveChangesAsync();
    }

    /// <summary>Sets the flags the alert rules gate on, so "inactive items are out by default" is provable.</summary>
    public async Task SetItemFlagsAsync(string iCode, bool? isActive = null, bool? stockControl = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var item = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == Company && x.ICode == iCode);
        if (isActive is bool active)
        {
            item.IsActive = active;
        }

        if (stockControl is bool control)
        {
            item.StockControl = control;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Assigns an item class, and ensures the <c>IvClass</c> row the summary's Class mode reads.</summary>
    public async Task SetItemClassAsync(string iCode, string? classCode, string? classDesc = null)
    {
        await using var db = await Factory.CreateDbContextAsync();
        if (!string.IsNullOrWhiteSpace(classCode) && classDesc is not null
            && !await db.IvClasses.AnyAsync(x => x.CompanyCode == Company && x.IClassCode == classCode))
        {
            db.IvClasses.Add(new IvClass
            {
                CompanyCode = Company,
                IClassCode = classCode,
                IDesc = classDesc,
                IsActive = true
            });
        }

        var item = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == Company && x.ICode == iCode);
        item.IClassCode = classCode;
        await db.SaveChangesAsync();
    }

    public IvStockAlertService CreateAlertService(
        ICurrentDateService? clock = null,
        Mock<IAccessRightService>? access = null,
        string company = Company,
        string branch = Branch) =>
        new(
            new IvStockInquiryRepository(Factory),
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            (access ?? Access()).Object,
            clock ?? new FixedCurrentDateService(new DateTime(2026, 9, 24)));

    public IvStockSummaryService CreateSummaryService(
        Mock<IAccessRightService>? access = null,
        string company = Company,
        string branch = Branch) =>
        new(
            new IvStockInquiryRepository(Factory),
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            (access ?? Access()).Object);

    public IvLotInquiryService CreateLotService(
        ICurrentDateService? clock = null,
        Mock<IAccessRightService>? access = null,
        string company = Company,
        string branch = Branch)
    {
        var rights = access ?? Access();
        var history = new IvTrxHistoryService(
            new IvStockHistoryRepository(Factory),
            new IvStockInquiryRepository(Factory),
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            rights.Object);

        return new IvLotInquiryService(
            new IvStockInquiryRepository(Factory),
            history,
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            rights.Object,
            clock ?? new FixedCurrentDateService(new DateTime(2026, 9, 24)));
    }
}

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryTrxInquiry)]
public class IvTrxHistoryServiceTests : IAsyncLifetime
{
    private const string Menu = MenuCodes.InventoryTrxInquiry;

    private readonly IvHistoryTestDb _db = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static IvTrxHistoryQuery Q(
        string? iCode = null,
        string? whCode = null,
        string? locCode = null,
        string? lotNo = null,
        IReadOnlyList<string>? trxTypes = null,
        int? batchNo = null,
        string? refNo = null,
        string? documentNo = null,
        IReadOnlyList<string>? statuses = null,
        DateTime? trxDateFrom = null,
        DateTime? trxDateTo = null,
        string? searchText = null,
        string? sortField = null,
        bool sortDescending = false,
        int? take = null) =>
        new()
        {
            ICode = iCode,
            WhCode = whCode,
            LocCode = locCode,
            LotNo = lotNo,
            TrxTypes = trxTypes ?? [],
            BatchNo = batchNo,
            RefNo = refNo,
            DocumentNo = documentNo,
            IStatuses = statuses ?? [],
            TrxDateFrom = trxDateFrom,
            TrxDateTo = trxDateTo,
            SearchText = searchText,
            SortField = sortField,
            SortDescending = sortDescending,
            Take = take ?? 50
        };

    // ── Security ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Company_isolation_excludes_another_company()
    {
        var mine = await _db.SeedHistoryAsync(refNo: "MINE");
        var theirs = await _db.SeedHistoryAsync(company: "OTHER", refNo: "THEIRS");

        var result = await _db.CreateService().SearchAsync(Menu, Q());

        Assert.True(result.Succeeded, result.Message);
        var ids = result.Data!.Rows.Select(r => r.Id).ToList();
        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
    }

    [Fact]
    public async Task Branch_isolation_excludes_another_branch()
    {
        var mine = await _db.SeedHistoryAsync(refNo: "MINE");
        var theirs = await _db.SeedHistoryAsync(branch: "BR2", refNo: "THEIRS");

        var result = await _db.CreateService().SearchAsync(Menu, Q());

        var ids = result.Data!.Rows.Select(r => r.Id).ToList();
        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
    }

    [Fact]
    public async Task Access_denied_returns_AccessDenied_with_no_rows()
    {
        await _db.SeedHistoryAsync();

        var result = await _db.CreateService(IvHistoryTestDb.Deny(PermissionCodes.Access)).SearchAsync(Menu, Q());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task An_unknown_menu_is_refused_so_a_page_cannot_borrow_another_screens_rights()
    {
        await _db.SeedHistoryAsync();

        var result = await _db.CreateService().SearchAsync(MenuCodes.InventoryBalanceLot, Q());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task Access_and_price_are_checked_against_the_callers_own_menu()
    {
        var (access, checks) = IvHistoryTestDb.RecordingAccess();
        await _db.SeedHistoryAsync();

        await _db.CreateService(access).SearchAsync(MenuCodes.InventoryStockCard, Q());

        Assert.Contains((MenuCodes.InventoryStockCard, PermissionCodes.Access), checks);
        Assert.Contains((MenuCodes.InventoryStockCard, PermissionCodes.ViewPrice), checks);
        Assert.DoesNotContain(checks, c => c.Menu == MenuCodes.InventoryTrxInquiry);
    }

    [Fact]
    public async Task ViewPrice_denied_nulls_the_value_on_grid_summary_and_export()
    {
        await _db.SeedHistoryAsync(toStdQty: 2m, unitPrice: 3m);

        var service = _db.CreateService(IvHistoryTestDb.Deny(PermissionCodes.ViewPrice));
        var grid = await service.SearchAsync(Menu, Q());
        var summary = await service.GetSummaryAsync(Menu, Q());
        var export = await service.ExportRowsAsync(Menu, Q(take: 50_000));

        Assert.All(grid.Data!.Rows, r => Assert.Null(r.EstValue));
        Assert.Null(summary.Data!.TotalValue);
        Assert.All(export.Data!.Rows, r => Assert.Null(r.EstValue));
    }

    [Fact]
    public async Task ViewPrice_granted_uses_the_locked_formula_history_price_then_item_purchase_price()
    {
        await _db.SeedHistoryAsync(iCode: "A100", toStdQty: 2m, unitPrice: 3m);   // 2 * 3 = 6
        await _db.SeedHistoryAsync(iCode: "A101", toStdQty: 2m, unitPrice: null); // 2 * 7 = 14

        var result = await _db.CreateService().SearchAsync(Menu, Q());

        var byCode = result.Data!.Rows.ToDictionary(r => r.ICode);
        Assert.Equal(6m, byCode["A100"].EstValue);
        Assert.Equal(14m, byCode["A101"].EstValue);
    }

    // ── Scope-aware quantities (D12) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_slice_pinned_net_is_to_minus_from()
    {
        await _db.SeedHistoryAsync(toStdQty: null, frStdQty: 4m, toWh: null, toLoc: null);

        var row = (await _db.CreateService().SearchAsync(Menu, Q())).Data!.Rows.Single();

        Assert.Equal(0m, row.InQty);
        Assert.Equal(4m, row.OutQty);
        Assert.Equal(-4m, row.NetQty);
    }

    [Fact]
    public async Task A_transfer_is_in_for_the_receiving_warehouse_and_out_for_the_issuing_one()
    {
        // MAIN -> WH2, 5 EA: neither warehouse's "in" should be polluted by the other leg.
        await _db.SeedHistoryAsync(
            trxType: "TR", frStdQty: 5m, frWh: "MAIN", frLoc: "BIN1", toStdQty: 5m, toWh: "WH2", toLoc: "BIN1");

        var service = _db.CreateService();

        var unfiltered = (await service.SearchAsync(Menu, Q())).Data!.Rows.Single();
        Assert.Equal(5m, unfiltered.InQty);
        Assert.Equal(5m, unfiltered.OutQty);
        Assert.Equal(0m, unfiltered.NetQty);

        var fromMain = (await service.SearchAsync(Menu, Q(whCode: "MAIN"))).Data!.Rows.Single();
        Assert.Equal(0m, fromMain.InQty);
        Assert.Equal(5m, fromMain.OutQty);
        Assert.Equal(-5m, fromMain.NetQty);

        var intoWh2 = (await service.SearchAsync(Menu, Q(whCode: "WH2"))).Data!.Rows.Single();
        Assert.Equal(5m, intoWh2.InQty);
        Assert.Equal(0m, intoWh2.OutQty);
        Assert.Equal(5m, intoWh2.NetQty);
    }

    [Fact]
    public async Task A_bin_filter_is_also_per_leg()
    {
        await _db.SeedHistoryAsync(frStdQty: 3m, frWh: "MAIN", frLoc: "BIN1", toStdQty: 3m, toWh: "MAIN", toLoc: "BIN2");

        var row = (await _db.CreateService().SearchAsync(Menu, Q(locCode: "BIN2"))).Data!.Rows.Single();

        Assert.Equal(3m, row.InQty);
        Assert.Equal(0m, row.OutQty);
    }

    /// <summary>
    /// The summary is aggregated in SQL over two leg-filtered queries while the row figures are
    /// computed in memory; this pins that the two definitions agree, which is what stops them drifting.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("MAIN")]
    [InlineData("WH2")]
    [InlineData("NOPE")]
    public async Task Row_quantities_sum_to_the_summary_for_the_same_query(string? whCode)
    {
        await _db.SeedHistoryAsync(trxType: "TR", frStdQty: 5m, frWh: "MAIN", toStdQty: 5m, toWh: "WH2");
        await _db.SeedHistoryAsync(toStdQty: 7m, toWh: "MAIN");
        await _db.SeedHistoryAsync(toStdQty: null, frStdQty: 2m, frWh: "WH2", toWh: null, toLoc: null);

        var service = _db.CreateService();
        var rows = (await service.SearchAsync(Menu, Q(whCode: whCode, take: 100))).Data!.Rows;
        var summary = (await service.GetSummaryAsync(Menu, Q(whCode: whCode))).Data!;

        Assert.Equal(rows.Sum(r => r.InQty), summary.InQty);
        Assert.Equal(rows.Sum(r => r.OutQty), summary.OutQty);
        Assert.Equal(summary.InQty - summary.OutQty, summary.NetQty);
    }

    // ── Summary == predicate == export (D19) ────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_grid_and_export_agree_at_two_page_sizes()
    {
        for (var i = 0; i < 5; i++)
        {
            await _db.SeedHistoryAsync(iCode: "A100", toStdQty: 1m + i, trxDtTime: new DateTime(2026, 9, 1 + i));
        }

        await _db.SeedHistoryAsync(iCode: "A101", toStdQty: 99m, trxDtTime: new DateTime(2026, 9, 20));

        var service = _db.CreateService();
        var small = await service.SearchAsync(Menu, Q(take: 2));
        var large = await service.SearchAsync(Menu, Q(take: 100));
        var summary = await service.GetSummaryAsync(Menu, Q());
        var export = await service.ExportRowsAsync(Menu, Q(take: 50_000));

        Assert.Equal(6, small.Data!.TotalCount);
        Assert.Equal(6, large.Data!.TotalCount);
        Assert.Equal(6, summary.Data!.TotalRows);
        Assert.Equal(6, export.Data!.TotalCount);
        Assert.Equal(6, export.Data!.Rows.Count);
        Assert.Equal(2, small.Data!.Rows.Count);
    }

    [Fact]
    public async Task Export_uses_the_same_predicate_as_the_grid()
    {
        await _db.SeedHistoryAsync(iCode: "A100", toStdQty: 1m);
        await _db.SeedHistoryAsync(iCode: "A101", toStdQty: 2m);

        var service = _db.CreateService();
        var grid = await service.SearchAsync(Menu, Q(iCode: "A100"));
        var export = await service.ExportRowsAsync(Menu, Q(iCode: "A100", take: 50_000));

        Assert.Equal(grid.Data!.TotalCount, export.Data!.TotalCount);
        Assert.Equal(grid.Data!.Rows.Select(r => r.Id), export.Data!.Rows.Select(r => r.Id));
    }

    // ── Filters ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrxType_selection_of_ADJ_alone_is_the_adjustment_analysis()
    {
        var adj = await _db.SeedHistoryAsync(trxType: "ADJ", toStdQty: 1m, remarks: "FOUND: found on shelf");
        await _db.SeedHistoryAsync(trxType: "MR", toStdQty: 2m);

        var rows = (await _db.CreateService().SearchAsync(Menu, Q(trxTypes: ["ADJ"]))).Data!.Rows;

        Assert.Single(rows);
        Assert.Equal(adj, rows[0].Id);
    }

    [Fact]
    public async Task An_empty_type_selection_means_all_types()
    {
        await _db.SeedHistoryAsync(trxType: "ADJ", toStdQty: 1m);
        await _db.SeedHistoryAsync(trxType: "MR", toStdQty: 2m);
        await _db.SeedHistoryAsync(trxType: "MI", toStdQty: null, frStdQty: 3m, frWh: "MAIN", toWh: null, toLoc: null);

        var rows = (await _db.CreateService().SearchAsync(Menu, Q())).Data!.Rows;

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task An_empty_status_selection_means_all_statuses()
    {
        await _db.SeedHistoryAsync(status: "ACTIVE");
        await _db.SeedHistoryAsync(status: "DAMAGED");
        await _db.SeedHistoryAsync(status: null);

        var rows = (await _db.CreateService().SearchAsync(Menu, Q())).Data!.Rows;

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task A_pinned_status_narrows_the_result()
    {
        await _db.SeedHistoryAsync(status: "ACTIVE");
        await _db.SeedHistoryAsync(status: "DAMAGED");

        var rows = (await _db.CreateService().SearchAsync(Menu, Q(statuses: ["DAMAGED"]))).Data!.Rows;

        Assert.Single(rows);
        Assert.Equal("DAMAGED", rows[0].IStatus);
    }

    /// <summary>Half-open range: the row dated exactly at <c>from</c> is IN, one at <c>to + 1 day</c> is OUT.</summary>
    [Fact]
    public async Task The_date_range_is_half_open_with_no_Date_on_the_column()
    {
        var atFrom = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 1, 0, 0, 0));
        var inside = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 15, 23, 59, 59));
        var atToPlusOne = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 21, 0, 0, 0));
        var afterTo = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 25, 0, 0, 0));

        var rows = (await _db.CreateService().SearchAsync(
            Menu,
            Q(trxDateFrom: new DateTime(2026, 9, 1), trxDateTo: new DateTime(2026, 9, 20)))).Data!.Rows;

        var ids = rows.Select(r => r.Id).ToList();
        Assert.Contains(atFrom, ids);
        Assert.Contains(inside, ids);
        Assert.DoesNotContain(atToPlusOne, ids);
        Assert.DoesNotContain(afterTo, ids);
    }

    [Fact]
    public async Task A_document_number_filter_matches_any_of_the_document_columns()
    {
        var byInv = await _db.SeedHistoryAsync();
        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            var row = await db.IvTrxHistories.SingleAsync(x => x.Id == byInv);
            row.InvNo = "INV-9001";
            await db.SaveChangesAsync();
        }

        var rows = (await _db.CreateService().SearchAsync(Menu, Q(documentNo: "9001"))).Data!.Rows;

        Assert.Single(rows);
        Assert.Equal(byInv, rows[0].Id);
    }

    [Fact]
    public async Task Search_text_matches_the_item_code_description_reference_and_remarks()
    {
        var byRemarks = await _db.SeedHistoryAsync(iCode: "A100", remarks: "damaged in transit");
        await _db.SeedHistoryAsync(iCode: "A101", remarks: "none");

        var rows = (await _db.CreateService().SearchAsync(Menu, Q(searchText: "transit"))).Data!.Rows;

        Assert.Single(rows);
        Assert.Equal(byRemarks, rows[0].Id);
    }

    // ── Sorting ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_default_order_is_newest_first()
    {
        var older = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 1));
        var newer = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 20));

        var rows = (await _db.CreateService().SearchAsync(Menu, Q())).Data!.Rows;

        Assert.Equal(newer, rows[0].Id);
        Assert.Equal(older, rows[1].Id);
    }

    /// <summary>An unknown field must fall back to the default order and can never reach the expression tree.</summary>
    [Fact]
    public async Task An_unknown_sort_field_falls_back_instead_of_failing()
    {
        var older = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 1));
        var newer = await _db.SeedHistoryAsync(trxDtTime: new DateTime(2026, 9, 20));

        var result = await _db.CreateService().SearchAsync(Menu, Q(sortField: "DROP TABLE IvTrxHistory"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(newer, result.Data!.Rows[0].Id);
        Assert.Equal(older, result.Data!.Rows[1].Id);
    }

    [Fact]
    public async Task A_whitelisted_sort_field_is_honoured()
    {
        await _db.SeedHistoryAsync(iCode: "A101");
        await _db.SeedHistoryAsync(iCode: "A100");

        var rows = (await _db.CreateService().SearchAsync(
            Menu,
            Q(sortField: nameof(IvTrxHistoryRow.ICode)))).Data!.Rows;

        Assert.Equal("A100", rows[0].ICode);
    }

    // ── Derived columns ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_adjustment_reason_is_parsed_from_remarks_only_for_ADJ_rows()
    {
        // "OTHER" IS a real adjustment reason code, so use text that is not one.
        await _db.SeedHistoryAsync(trxType: "ADJ", remarks: "FOUND: found on shelf");
        await _db.SeedHistoryAsync(trxType: "ADJ", remarks: "found on shelf");
        await _db.SeedHistoryAsync(trxType: "MR", remarks: "FOUND: not an adjustment");

        var rows = (await _db.CreateService().SearchAsync(Menu, Q())).Data!.Rows;

        var byType = rows.ToDictionary(r => r.TrxType + "|" + r.Remarks);
        Assert.Equal("FOUND", byType["ADJ|FOUND: found on shelf"].Reason);
        Assert.Null(byType["ADJ|found on shelf"].Reason);
        Assert.Null(byType["MR|FOUND: not an adjustment"].Reason);
    }

    [Fact]
    public async Task ModifiedBy_is_always_null_because_the_table_has_no_such_column()
    {
        await _db.SeedHistoryAsync();

        var rows = (await _db.CreateService().SearchAsync(Menu, Q())).Data!.Rows;

        Assert.All(rows, r => Assert.Null(r.ModifiedBy));
    }

    [Fact]
    public void The_model_side_adjustment_token_matches_the_core_constant()
    {
        // Both are const, so only a test can catch drift.
        Assert.Equal(IvTrxTypes.StockAdjustment, IvTrxHistoryTypes.StockAdjustment);
    }

    [Fact]
    public void The_slice_key_orders_its_parts_the_same_way_as_the_pile_key()
    {
        var scope = new IvTrxHistoryScope("A100", "MAIN", "BIN1", "L1", "ACTIVE");

        var key = scope.ToSliceKey("DEMO", "HQ");

        Assert.NotNull(key);
        Assert.Equal("DEMO/HQ/A100/MAIN/BIN1/L1/ACTIVE", key!.Value.ToString());
        Assert.Null(IvTrxHistoryScope.Empty.ToSliceKey("DEMO", "HQ"));
    }

    [Fact]
    public void A_multi_status_selection_cannot_be_a_slice_so_the_status_part_is_a_wildcard()
    {
        var one = IvTrxHistoryScope.FromQuery(new IvTrxHistoryQuery { IStatuses = ["ACTIVE"] });
        var two = IvTrxHistoryScope.FromQuery(new IvTrxHistoryQuery { IStatuses = ["ACTIVE", "DAMAGED"] });

        Assert.Equal("ACTIVE", one.IStatus);
        Assert.Null(two.IStatus);
    }

    [Fact]
    public void Blank_filter_text_is_normalised_away_rather_than_matching_nothing()
    {
        var scope = IvTrxHistoryScope.FromQuery(new IvTrxHistoryQuery
        {
            ICode = "  ",
            WhCode = " MAIN ",
            LocCode = null,
            LotNo = "",
            IStatuses = []
        });

        Assert.Null(scope.ICode);
        Assert.Equal("MAIN", scope.WhCode);
        Assert.Null(scope.LotNo);
    }

    [Fact]
    public void An_entirely_blank_scope_matches_every_leg()
    {
        Assert.True(IvTrxHistoryScope.Empty.MatchesFromLeg("A100", "MAIN", "BIN1", "L1", "ACTIVE"));
        Assert.True(IvTrxHistoryScope.Empty.MatchesToLeg("A100", "WH2", null, null, null));
    }
}

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockCard)]
public class IvStockCardServiceTests : IAsyncLifetime
{
    private const string Menu = MenuCodes.InventoryStockCard;

    private static readonly DateTime PeriodFrom = new(2026, 9, 10);
    private static readonly DateTime PeriodTo = new(2026, 9, 20);

    private readonly IvHistoryTestDb _db = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static IvTrxHistoryQuery Q(
        string? iCode = "A100",
        string? whCode = null,
        string? locCode = null,
        string? lotNo = null,
        IReadOnlyList<string>? statuses = null,
        DateTime? from = null,
        DateTime? to = null) =>
        new()
        {
            ICode = iCode,
            WhCode = whCode,
            LocCode = locCode,
            LotNo = lotNo,
            IStatuses = statuses ?? [],
            TrxDateFrom = from ?? PeriodFrom,
            TrxDateTo = to ?? PeriodTo
        };

    [Fact]
    public async Task An_item_is_required_because_a_card_is_one_items_ledger()
    {
        var result = await _db.CreateService().GetStockCardAsync(Menu, Q(iCode: null));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task A_missing_start_date_is_refused()
    {
        var query = Q(from: null);
        query.TrxDateFrom = null;

        var result = await _db.CreateService().GetStockCardAsync(Menu, query);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("start date", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Opening_is_every_in_scope_movement_strictly_before_the_period()
    {
        await _db.SeedHistoryAsync(toStdQty: 10m, trxDtTime: new DateTime(2026, 9, 1));          // opening
        await _db.SeedHistoryAsync(toStdQty: 5m, trxDtTime: new DateTime(2026, 9, 9, 23, 59, 59)); // opening
        await _db.SeedHistoryAsync(toStdQty: 3m, trxDtTime: PeriodFrom);                          // period (boundary is IN)
        await _db.SeedHistoryAsync(toStdQty: 2m, trxDtTime: new DateTime(2026, 9, 15));           // period

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q())).Data!;

        Assert.Equal(15m, card.OpeningQty);
        Assert.Equal(5m, card.InQty);
        Assert.Equal(0m, card.OutQty);
        Assert.Equal(20m, card.LedgerClosingQty);
        Assert.Equal(2, card.TotalCount);
    }

    [Fact]
    public async Task A_row_dated_at_to_plus_one_day_is_out_of_the_period()
    {
        await _db.SeedHistoryAsync(toStdQty: 4m, trxDtTime: new DateTime(2026, 9, 21));

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q())).Data!;

        Assert.Equal(0m, card.OpeningQty);
        Assert.Equal(0, card.TotalCount);
        Assert.Equal(0m, card.LedgerClosingQty);
    }

    [Fact]
    public async Task The_running_balance_is_opening_plus_the_cumulative_net_in_movement_order()
    {
        await _db.SeedHistoryAsync(toStdQty: 100m, trxDtTime: new DateTime(2026, 9, 1)); // opening 100

        await _db.SeedHistoryAsync(toStdQty: 10m, trxDtTime: new DateTime(2026, 9, 11)); // +10 -> 110
        await _db.SeedHistoryAsync(toStdQty: null, frStdQty: 4m, frWh: "MAIN", toWh: null, toLoc: null,
            trxDtTime: new DateTime(2026, 9, 12));                                        // -4  -> 106
        await _db.SeedHistoryAsync(trxType: "ADJ", toStdQty: 6m, trxDtTime: new DateTime(2026, 9, 13)); // +6 -> 112

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q())).Data!;

        Assert.Equal(100m, card.OpeningQty);
        Assert.Equal(new[] { 110m, 106m, 112m }, card.Rows.Select(r => r.RunningQty!.Value));
        Assert.Equal(112m, card.LedgerClosingQty);
        Assert.Equal(6m, card.AdjustNetQty);
        Assert.Equal(3, card.TotalCount);
    }

    [Fact]
    public async Task Rows_are_chronological_even_when_a_sort_field_is_supplied()
    {
        await _db.SeedHistoryAsync(toStdQty: 1m, trxDtTime: new DateTime(2026, 9, 19));
        await _db.SeedHistoryAsync(toStdQty: 2m, trxDtTime: new DateTime(2026, 9, 11));
        await _db.SeedHistoryAsync(toStdQty: 3m, trxDtTime: new DateTime(2026, 9, 15));

        var query = Q();
        query.SortField = nameof(IvTrxHistoryRow.TrxDtTime);
        query.SortDescending = true;

        var card = (await _db.CreateService().GetStockCardAsync(Menu, query)).Data!;

        // Chronological: 9/11 (+2) = 2, 9/15 (+3) = 5, 9/19 (+1) = 6.
        Assert.Equal(2m, card.Rows[0].RunningQty!.Value);
        Assert.Equal(5m, card.Rows[1].RunningQty!.Value);
        Assert.Equal(6m, card.Rows[2].RunningQty!.Value);
        Assert.Equal(new DateTime(2026, 9, 11), card.Rows[0].TrxDtTime);
    }

    [Fact]
    public async Task Transfer_in_and_transfer_out_on_the_same_pile_move_the_running_balance_both_ways()
    {
        await _db.SeedPileAsync(qty: 0m);
        await _db.SeedHistoryAsync(trxType: "TR", frStdQty: 5m, frWh: "WH2", frLoc: "BIN1",
            toStdQty: 5m, toWh: "MAIN", toLoc: "BIN1", trxDtTime: new DateTime(2026, 9, 11));
        await _db.SeedHistoryAsync(trxType: "TR", frStdQty: 2m, frWh: "MAIN", frLoc: "BIN1",
            toStdQty: 2m, toWh: "WH2", toLoc: "BIN1", trxDtTime: new DateTime(2026, 9, 12));

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q(whCode: "MAIN", locCode: "BIN1"))).Data!;

        Assert.Equal(new[] { 5m, 3m }, card.Rows.Select(r => r.RunningQty!.Value));
        Assert.Equal(3m, card.LedgerClosingQty);
    }

    [Fact]
    public async Task A_multi_pile_scope_sums_across_every_matching_pile()
    {
        await _db.SeedHistoryAsync(toStdQty: 3m, toLoc: "BIN1", trxDtTime: new DateTime(2026, 9, 1));
        await _db.SeedHistoryAsync(toStdQty: 4m, toLoc: "BIN2", trxDtTime: new DateTime(2026, 9, 1));

        // Item-only scope: no warehouse/bin pinned, so both piles are in scope.
        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q())).Data!;

        Assert.Equal(7m, card.OpeningQty);
    }

    [Fact]
    public async Task The_live_quantity_is_reported_and_the_difference_flagged()
    {
        await _db.SeedPileAsync(qty: 9m, wh: "MAIN", loc: "BIN1");
        await _db.SeedHistoryAsync(toStdQty: 7m, toWh: "MAIN", toLoc: "BIN1", trxDtTime: new DateTime(2026, 9, 1));

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q(whCode: "MAIN", locCode: "BIN1"))).Data!;

        Assert.Equal(9m, card.LiveQty);
        Assert.Equal(7m, card.LedgerClosingQty);
        Assert.Equal(2m, card.DifferenceQty);
    }

    [Fact]
    public async Task There_is_no_difference_when_the_ledger_and_the_pile_agree()
    {
        await _db.SeedPileAsync(qty: 7m, wh: "MAIN", loc: "BIN1");
        await _db.SeedHistoryAsync(toStdQty: 7m, toWh: "MAIN", toLoc: "BIN1", trxDtTime: new DateTime(2026, 9, 1));

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q(whCode: "MAIN", locCode: "BIN1"))).Data!;

        Assert.Equal(0m, card.DifferenceQty);
    }

    [Fact]
    public async Task The_live_quantity_is_reported_as_zero_when_the_item_has_no_pile_at_all()
    {
        await _db.SeedHistoryAsync(toStdQty: 1m);

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q())).Data!;

        Assert.Equal(0m, card.LiveQty);
    }

    [Fact]
    public async Task A_card_for_another_items_movement_never_appears()
    {
        await _db.SeedHistoryAsync(iCode: "A100", toStdQty: 5m, trxDtTime: new DateTime(2026, 9, 1));   // opening
        await _db.SeedHistoryAsync(iCode: "A101", toStdQty: 99m, trxDtTime: new DateTime(2026, 9, 1)); // opening, other item
        await _db.SeedHistoryAsync(iCode: "A100", toStdQty: 2m, trxDtTime: new DateTime(2026, 9, 12));  // period
        await _db.SeedHistoryAsync(iCode: "A101", toStdQty: 7m, trxDtTime: new DateTime(2026, 9, 12));  // period, other item

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q(iCode: "A100"))).Data!;

        Assert.Equal(5m, card.OpeningQty);
        Assert.Single(card.Rows);
        Assert.Equal("A100", card.Rows[0].ICode);
        Assert.Equal(7m, card.LedgerClosingQty);
    }

    [Fact]
    public async Task Access_denied_refuses_the_card()
    {
        var result = await _db.CreateService(IvHistoryTestDb.Deny(PermissionCodes.Access)).GetStockCardAsync(Menu, Q());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task ViewPrice_denied_nulls_the_card_value_but_keeps_the_quantities()
    {
        await _db.SeedHistoryAsync(toStdQty: 2m, unitPrice: 3m, trxDtTime: new DateTime(2026, 9, 1));

        var card = (await _db.CreateService(IvHistoryTestDb.Deny(PermissionCodes.ViewPrice))
            .GetStockCardAsync(Menu, Q())).Data!;

        Assert.Null(card.TotalValue);
        Assert.All(card.Rows, r => Assert.Null(r.EstValue));
        Assert.Equal(2m, card.OpeningQty);
    }

    [Fact]
    public async Task TotalCount_is_what_the_grid_can_serve_and_MatchingCount_is_the_truth()
    {
        await _db.SeedHistoryAsync(toStdQty: 1m, trxDtTime: new DateTime(2026, 9, 11));
        await _db.SeedHistoryAsync(toStdQty: 2m, trxDtTime: new DateTime(2026, 9, 12));

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q())).Data!;

        Assert.Equal(2, card.TotalCount);
        Assert.Equal(2, card.MatchingCount);
        Assert.Equal(2, card.Rows.Count);
        Assert.False(card.Truncated);
    }

    [Fact]
    public async Task Company_and_branch_are_always_resolved_server_side()
    {
        await _db.SeedHistoryAsync(toStdQty: 5m, trxDtTime: new DateTime(2026, 9, 1));
        await _db.SeedHistoryAsync(toStdQty: 5m, trxDtTime: new DateTime(2026, 9, 1), branch: "BR2");

        var card = (await _db.CreateService().GetStockCardAsync(Menu, Q())).Data!;

        Assert.Equal(5m, card.OpeningQty);
    }
}
