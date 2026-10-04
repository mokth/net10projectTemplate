using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;
using Moq;

namespace ErpWeb.Tests.Inventory.Inquiry;
/// <summary>
/// <b>Est. Inventory Value</b> (Phase 3, item 15). The screen reuses the Stock Summary composition, so
/// these tests focus on what makes it a DIFFERENT screen: the value-first emphasis, the D16 unit rule,
/// and — most importantly — that its <c>ACCESS</c> and <c>VIEW_PRICE</c> are its OWN grant and not the
/// summary screen's (D11 Option B).
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockValue)]
public class IvStockValueServiceTests : IAsyncLifetime
{
    private const string Menu = MenuCodes.InventoryStockValue;

    private IvHistoryTestDb _db = null!;

    public async Task InitializeAsync() => _db = new IvHistoryTestDb();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static IvStockSummaryQuery Query(string groupBy = IvStockSummaryGroupBys.Item) =>
        new() { GroupBy = groupBy, Take = 100 };

    // ── The estimate ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Value_UsesTheShippedFormula_WithThePilesOwnPriceWinning()
    {
        // A100's item purchase price is 5 in the fixture; one pile overrides it at 9.
        await _db.SeedPileAsync("A100", 10m, loc: "BIN1", unitPrice: 9m);
        await _db.SeedPileAsync("A100", 2m, loc: "BIN2");

        var service = _db.CreateSummaryService();
        var row = Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows);

        // 10 x 9 + 2 x 5 = 100
        Assert.Equal(100m, row.EstValue);

        var summary = await service.GetSummaryAsync(Menu, Query());
        Assert.Equal(100m, summary.Data!.TotalValue);
    }

    [Fact]
    public async Task Value_IsRoundedToTheQuantityScale()
    {
        await _db.SeedPileAsync("A100", 1.23456789m, unitPrice: 0.3333333m);

        var row = Assert.Single((await _db.CreateSummaryService().SearchAsync(Menu, Query())).Data!.Rows);

        Assert.Equal(IvQty.Round(1.23456789m * 0.3333333m), row.EstValue);
    }

    [Fact]
    public async Task Value_IsGroupedByItem_Warehouse_OrClass()
    {
        await _db.SetItemClassAsync("A100", "RAW", "Raw materials");
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN", unitPrice: 2m);
        await _db.SeedPileAsync("A100", 5m, wh: "WH2", unitPrice: 2m);

        var service = _db.CreateSummaryService();

        var byItem = await service.SearchAsync(Menu, Query());
        var itemRow = Assert.Single(byItem.Data!.Rows);
        Assert.Equal(30m, itemRow.EstValue);
        Assert.Equal("EA", itemRow.UomDisplay);

        var byWarehouse = await service.SearchAsync(Menu, Query(IvStockSummaryGroupBys.Warehouse));
        Assert.Equal(2, byWarehouse.Data!.TotalCount);
        Assert.Equal(20m, byWarehouse.Data.Rows.Single(x => x.WhCode == "MAIN").EstValue);

        var byClass = await service.SearchAsync(Menu, Query(IvStockSummaryGroupBys.Class));
        Assert.Equal("Raw materials", Assert.Single(byClass.Data!.Rows).IClassDesc);
    }

    [Fact]
    public async Task WarehouseAndClassGroups_ClaimNoSingleUom_SoTheQuantityCaptionIsMixedUnit()
    {
        await _db.SetItemClassAsync("A100", "RAW", "Raw materials");
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A101", 4m, wh: "MAIN");

        var row = Assert.Single(
            (await _db.CreateSummaryService().SearchAsync(Menu, Query(IvStockSummaryGroupBys.Warehouse))).Data!.Rows);

        Assert.Null(row.StdUom);
        Assert.Null(row.UomDisplay);
        Assert.Equal(2, row.ItemCount);
    }

    // ── The grants are the value screen's own (D11 Option B) ────────────────────────────────────────

    [Fact]
    public async Task Value_IsMaskedWhenViewPriceIsDeniedOnTheValueMenu()
    {
        await _db.SeedPileAsync("A100", 10m);

        var service = _db.CreateSummaryService(IvHistoryTestDb.Deny(PermissionCodes.ViewPrice));

        Assert.Null(Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows).EstValue);
        Assert.Null((await service.GetSummaryAsync(Menu, Query())).Data!.TotalValue);
        Assert.Null(Assert.Single((await service.ExportRowsAsync(Menu, Query())).Data!.Rows).EstValue);
    }

    [Fact]
    public async Task ViewPriceOnTheSummaryMenu_DoesNotLeakIntoTheValueScreen()
    {
        // The whole point of D11 Option B: the two screens hold SEPARATE price grants.
        await _db.SeedPileAsync("A100", 10m);

        var onlySummary = GrantViewPriceFor([MenuCodes.InventoryStockSummary]);
        var service = _db.CreateSummaryService(onlySummary);

        Assert.Null(Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows).EstValue);
        Assert.NotNull(Assert.Single((await service.SearchAsync(MenuCodes.InventoryStockSummary, Query())).Data!.Rows).EstValue);
    }

    [Fact]
    public async Task ViewPriceOnTheValueMenu_IsHonoured()
    {
        await _db.SeedPileAsync("A100", 10m);

        var service = _db.CreateSummaryService(GrantViewPriceFor([Menu]));

        Assert.Equal(50m, Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows).EstValue);
    }

    [Fact]
    public async Task Search_ChecksTheValueMenuItself()
    {
        var (access, checks) = IvHistoryTestDb.RecordingAccess();

        await _db.CreateSummaryService(access).SearchAsync(Menu, Query());

        Assert.Contains((Menu, PermissionCodes.Access), checks);
        Assert.Contains((Menu, PermissionCodes.ViewPrice), checks);
        Assert.DoesNotContain(checks, c => c.Menu == MenuCodes.InventoryStockSummary);
    }

    [Fact]
    public async Task Search_DeniedAccess_IsRejected()
    {
        var result = await _db.CreateSummaryService(IvHistoryTestDb.Deny(PermissionCodes.Access))
            .SearchAsync(Menu, Query());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Search_OnAMenuThisServiceDoesNotServe_IsRejected()
    {
        var result = await _db.CreateSummaryService().SearchAsync(MenuCodes.InventoryStockAlerts, Query());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task SummaryGridAndExport_AgreeAtTwoPageSizes()
    {
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A101", 5m, wh: "WH2");

        var service = _db.CreateSummaryService();
        var summary = await service.GetSummaryAsync(Menu, Query());

        foreach (var pageSize in new[] { 1, 50 })
        {
            var query = Query();
            query.Take = pageSize;

            Assert.Equal(summary.Data!.GroupCount, (await service.SearchAsync(Menu, query)).Data!.TotalCount);
            Assert.Equal(summary.Data.GroupCount, (await service.ExportRowsAsync(Menu, query)).Data!.TotalCount);
        }
    }

    [Fact]
    public async Task Search_OnAnotherCompany_SeesNothing()
    {
        await _db.SeedPileAsync("A100", 10m);

        var result = await _db.CreateSummaryService(company: "OTHER").SearchAsync(Menu, Query());

        Assert.True(result.Succeeded);
        Assert.Empty(result.Data!.Rows);
    }

    /// <summary>
    /// Allows everything, EXCEPT that <c>VIEW_PRICE</c> is granted only on the listed menus — the shape
    /// that proves one screen's price grant cannot be spent on another.
    /// </summary>
    private static Mock<IAccessRightService> GrantViewPriceFor(IReadOnlyList<string> menus)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string permission, CancellationToken _) =>
                !string.Equals(permission, PermissionCodes.ViewPrice, StringComparison.OrdinalIgnoreCase)
                || menus.Contains(menu, StringComparer.OrdinalIgnoreCase));
        return access;
    }
}
