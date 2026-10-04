using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Tests.Inventory.Inquiry;
/// <summary>
/// The stock summary (Phase 2, item 12). The mandatory fixtures: a mixed UOM inside one group and the
/// caption/UOM rule it drives (D16), zero-quantity piles, the same item in two warehouses, the same item
/// in two lots, and Class grouping resolving <c>IClassCode</c> through <c>IvClass</c>.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockSummary)]
public class IvStockSummaryServiceTests : IAsyncLifetime
{
    private const string Menu = MenuCodes.InventoryStockSummary;

    private IvHistoryTestDb _db = null!;

    public async Task InitializeAsync() => _db = new IvHistoryTestDb();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static IvStockSummaryQuery Query(
        string groupBy = IvStockSummaryGroupBys.Item,
        string? iCode = null,
        string? whCode = null,
        string? iClassCode = null) =>
        new()
        {
            GroupBy = groupBy,
            ICode = iCode,
            WhCode = whCode,
            IClassCode = iClassCode,
            Take = 100
        };

    // ── Grouping modes ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Item_ListsOneRowPerItem_WithItsUomPileCountAndQuantity()
    {
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A100", 5m, wh: "WH2");
        await _db.SeedPileAsync("A101", 7m, wh: "MAIN");

        var result = await _db.CreateSummaryService().SearchAsync(Menu, Query());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Data!.TotalCount);

        var a100 = result.Data.Rows.Single(x => x.ICode == "A100");
        Assert.Equal(15m, a100.TotalQty);
        Assert.Equal(2, a100.PileCount);
        Assert.Equal("EA", a100.StdUom);
        Assert.Equal("EA", a100.UomDisplay);
    }

    [Fact]
    public async Task ItemWarehouse_SplitsTheSameItemAcrossWarehouses()
    {
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A100", 5m, wh: "WH2");

        var result = await _db.CreateSummaryService()
            .SearchAsync(Menu, Query(IvStockSummaryGroupBys.ItemWarehouse));

        Assert.Equal(2, result.Data!.TotalCount);
        Assert.Equal(10m, result.Data.Rows.Single(x => x.WhCode == "MAIN").TotalQty);
        Assert.Equal(5m, result.Data.Rows.Single(x => x.WhCode == "WH2").TotalQty);
    }

    [Fact]
    public async Task Warehouse_And_Class_NeverClaimASingleUom()
    {
        // D16: a Warehouse/Class group may mix PCS with KG, so no UOM is asserted for it at all — the
        // page hides the quantity column and captions the opt-in total as mixed-unit.
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A101", 4m, wh: "MAIN");
        await _db.SetItemClassAsync("A100", "RAW", "Raw materials");
        await _db.SetItemClassAsync("A101", "RAW", "Raw materials");

        var service = _db.CreateSummaryService();

        var byWarehouse = await service.SearchAsync(Menu, Query(IvStockSummaryGroupBys.Warehouse));
        var warehouseRow = Assert.Single(byWarehouse.Data!.Rows);
        Assert.Null(warehouseRow.StdUom);
        Assert.Null(warehouseRow.UomDisplay);
        Assert.Equal(2, warehouseRow.ItemCount);
        Assert.Equal(14m, warehouseRow.TotalQty);

        var byClass = await service.SearchAsync(Menu, Query(IvStockSummaryGroupBys.Class));
        var classRow = Assert.Single(byClass.Data!.Rows);
        Assert.Equal("RAW", classRow.IClassCode);
        Assert.Equal("Raw materials", classRow.IClassDesc);
        Assert.Null(classRow.UomDisplay);
    }

    [Fact]
    public async Task Class_Grouping_ResolvesIClassCodeThroughIvClass()
    {
        await _db.SetItemClassAsync("A100", "RAW", "Raw materials");
        await _db.SetItemClassAsync("A101", "FIN", "Finished goods");
        await _db.SeedPileAsync("A100", 1m);
        await _db.SeedPileAsync("A101", 2m);

        var result = await _db.CreateSummaryService()
            .SearchAsync(Menu, Query(IvStockSummaryGroupBys.Class));

        Assert.Equal(2, result.Data!.TotalCount);
        Assert.Equal("Finished goods", result.Data.Rows.Single(x => x.IClassCode == "FIN").IClassDesc);
    }

    [Fact]
    public async Task Item_Grouping_CarriesTheItemsClassAsAGroupKey()
    {
        await _db.SetItemClassAsync("A100", "RAW", "Raw materials");
        await _db.SeedPileAsync("A100", 1m);

        var row = Assert.Single((await _db.CreateSummaryService().SearchAsync(Menu, Query())).Data!.Rows);

        Assert.Equal("RAW", row.IClassCode);
    }

    [Fact]
    public async Task ClassFilter_RestrictsToThatClass()
    {
        await _db.SetItemClassAsync("A100", "RAW", "Raw materials");
        await _db.SetItemClassAsync("A101", "FIN", "Finished goods");
        await _db.SeedPileAsync("A100", 1m);
        await _db.SeedPileAsync("A101", 2m);

        var result = await _db.CreateSummaryService().SearchAsync(Menu, Query(iClassCode: "RAW"));

        Assert.Equal("A100", Assert.Single(result.Data!.Rows).ICode);
    }

    [Fact]
    public async Task SameItemInTwoLots_IsOnePileEach_AndTwoPilesInTheItemGroup()
    {
        var lot1 = await _db.SeedLotAsync("A100", "L1");
        var lot2 = await _db.SeedLotAsync("A100", "L2");
        await _db.SeedPileAsync("A100", 3m, lotNo: "L1", lotId: lot1);
        await _db.SeedPileAsync("A100", 4m, lotNo: "L2", lotId: lot2);

        var row = Assert.Single((await _db.CreateSummaryService().SearchAsync(Menu, Query())).Data!.Rows);

        Assert.Equal(7m, row.TotalQty);
        Assert.Equal(2, row.PileCount);
    }

    // ── Zero-quantity piles ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ZeroQtyPiles_AreCountedSeparately_AndCanBeExcluded()
    {
        await _db.SeedPileAsync("A100", 10m, loc: "BIN1");
        await _db.SeedPileAsync("A100", 0m, loc: "BIN2");

        var service = _db.CreateSummaryService();

        var row = Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows);
        Assert.Equal(2, row.PileCount);
        Assert.Equal(1, row.ZeroQtyPileCount);
        Assert.Equal(10m, row.TotalQty);

        var excluded = Query();
        excluded.IncludeZeroQty = false;
        var without = Assert.Single((await service.SearchAsync(Menu, excluded)).Data!.Rows);
        Assert.Equal(1, without.PileCount);
        Assert.Equal(0, without.ZeroQtyPileCount);
    }

    // ── Money (D11 Option B / D5) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EstValue_UsesTheShippedFormula_WithThePilePriceWinningOverTheItemPrice()
    {
        // A100's item purchase price is 5 in the fixture; one pile overrides it at 9.
        await _db.SeedPileAsync("A100", 10m, loc: "BIN1", unitPrice: 9m);
        await _db.SeedPileAsync("A100", 2m, loc: "BIN2");

        var row = Assert.Single((await _db.CreateSummaryService().SearchAsync(Menu, Query())).Data!.Rows);

        // 10 x 9 + 2 x 5 = 100
        Assert.Equal(100m, row.EstValue);
    }

    [Fact]
    public async Task MoneyColumns_AreOmittedWithoutViewPrice()
    {
        await _db.SeedPileAsync("A100", 10m);

        var service = _db.CreateSummaryService(IvHistoryTestDb.Deny(PermissionCodes.ViewPrice));

        var row = Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows);
        Assert.Null(row.EstValue);

        var summary = await service.GetSummaryAsync(Menu, Query());
        Assert.Null(summary.Data!.TotalValue);

        var export = await service.ExportRowsAsync(Menu, Query());
        Assert.Null(Assert.Single(export.Data!.Rows).EstValue);
    }

    [Fact]
    public async Task MoneyColumns_AreRoundedToTheQuantityScale()
    {
        await _db.SeedPileAsync("A100", 1.23456789m, unitPrice: 0.3333333m);

        var row = Assert.Single((await _db.CreateSummaryService().SearchAsync(Menu, Query())).Data!.Rows);

        // 1.23456789 x 0.3333333 = 0.4115226076... → 4 dp, away from zero.
        Assert.Equal(IvQty.Round(1.23456789m * 0.3333333m), row.EstValue);
    }

    // ── The aggregate ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_MeasuresAreModeIndependent_ButTheGroupCountIsNot()
    {
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A101", 5m, wh: "WH2");

        var service = _db.CreateSummaryService();

        var byItem = await service.GetSummaryAsync(Menu, Query());
        var byWarehouse = await service.GetSummaryAsync(Menu, Query(IvStockSummaryGroupBys.Warehouse));

        Assert.Equal(2, byItem.Data!.GroupCount);
        Assert.Equal(2, byWarehouse.Data!.GroupCount);

        // Grouping only partitions the piles, so the quantities are identical for every mode.
        Assert.Equal(15m, byItem.Data.TotalQty);
        Assert.Equal(15m, byWarehouse.Data.TotalQty);
        Assert.Equal(2, byItem.Data.PileCount);
        Assert.Equal(2, byItem.Data.ItemCount);
    }

    [Fact]
    public async Task GroupCount_MatchesTheGridAndTheExport_AtTwoPageSizes()
    {
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A101", 5m, wh: "WH2");

        var service = _db.CreateSummaryService();
        var summary = await service.GetSummaryAsync(Menu, Query());

        foreach (var pageSize in new[] { 1, 50 })
        {
            var query = Query();
            query.Take = pageSize;

            var grid = await service.SearchAsync(Menu, query);
            var export = await service.ExportRowsAsync(Menu, query);

            Assert.Equal(summary.Data!.GroupCount, grid.Data!.TotalCount);
            Assert.Equal(summary.Data.GroupCount, export.Data!.TotalCount);
        }
    }

    [Fact]
    public async Task Summary_CountsZeroQtyPiles()
    {
        await _db.SeedPileAsync("A100", 0m, loc: "BIN1");
        await _db.SeedPileAsync("A100", 0m, loc: "BIN2");

        var summary = await _db.CreateSummaryService().GetSummaryAsync(Menu, Query());

        Assert.Equal(2, summary.Data!.ZeroQtyPileCount);
        Assert.Equal(0m, summary.Data.TotalQty);
    }

    // ── Filters, sort whitelist and security ────────────────────────────────────────────────────────

    [Fact]
    public async Task StatusList_IsAnInclusionList_AndEmptyMeansAll()
    {
        var lot = await _db.SeedLotAsync("A100", "L1");
        await _db.SeedPileAsync("A100", 3m, loc: "BIN1", status: "ACTIVE", lotId: lot);
        await _db.SeedPileAsync("A100", 4m, loc: "BIN2", status: "DAMAGED", lotId: lot);

        var service = _db.CreateSummaryService();

        var all = await service.GetSummaryAsync(Menu, Query());
        Assert.Equal(2, all.Data!.PileCount);

        var filtered = Query();
        filtered.IStatuses = ["DAMAGED"];
        var damaged = await service.GetSummaryAsync(Menu, filtered);
        Assert.Equal(1, damaged.Data!.PileCount);
        Assert.Equal(4m, damaged.Data.TotalQty);
    }

    [Fact]
    public async Task UnknownSortField_FallsBackToTheDefaultOrder()
    {
        await _db.SeedPileAsync("A100", 1m);
        await _db.SeedPileAsync("A101", 1m);

        var query = Query();
        query.SortField = "'; DROP TABLE IvBalLoc; --";

        var result = await _db.CreateSummaryService().SearchAsync(Menu, query);

        Assert.True(result.Succeeded);
        Assert.Equal(["A100", "A101"], result.Data!.Rows.Select(x => x.ICode));
    }

    [Fact]
    public async Task DeclaredSortField_IsHonoured()
    {
        await _db.SeedPileAsync("A100", 1m);
        await _db.SeedPileAsync("A101", 9m);

        var query = Query();
        query.SortField = nameof(IvStockSummaryRow.TotalQty);
        query.SortDescending = true;

        var result = await _db.CreateSummaryService().SearchAsync(Menu, query);

        Assert.Equal("A101", result.Data!.Rows[0].ICode);
    }

    [Fact]
    public async Task Search_OnAnotherCompany_SeesNothing()
    {
        await _db.SeedPileAsync("A100", 10m);

        var result = await _db.CreateSummaryService(company: "OTHER").SearchAsync(Menu, Query());

        Assert.True(result.Succeeded);
        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task Search_DeniedAccess_IsRejected()
    {
        var result = await _db.CreateSummaryService(access: IvHistoryTestDb.Deny(PermissionCodes.Access))
            .SearchAsync(Menu, Query());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Search_OnAMenuThisServiceDoesNotServe_IsRejected()
    {
        var result = await _db.CreateSummaryService()
            .SearchAsync(MenuCodes.InventoryStockAlerts, Query());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task UnknownGroupingToken_FallsBackToItem()
    {
        await _db.SeedPileAsync("A100", 10m, wh: "MAIN");
        await _db.SeedPileAsync("A100", 5m, wh: "WH2");

        var result = await _db.CreateSummaryService()
            .SearchAsync(Menu, Query("NOT_A_MODE"));

        // Item grouping: one row, not the two an Item × Warehouse fallback would produce.
        Assert.Equal("A100", Assert.Single(result.Data!.Rows).ICode);
    }

    [Fact]
    public void GroupingTokens_AreTheFourTheOwnerFixed()
    {
        Assert.Equal(new[] { "ITEM", "WAREHOUSE", "ITEM_WAREHOUSE", "CLASS" }, IvStockSummaryGroupBys.All);
        Assert.True(IvStockSummaryGroupBys.HasSingleUomPerGroup("ITEM"));
        Assert.True(IvStockSummaryGroupBys.HasSingleUomPerGroup("ITEM_WAREHOUSE"));
        Assert.False(IvStockSummaryGroupBys.HasSingleUomPerGroup("WAREHOUSE"));
        Assert.False(IvStockSummaryGroupBys.HasSingleUomPerGroup("CLASS"));
        Assert.Equal("Item × Warehouse", IvStockSummaryGroupBys.Describe("item_warehouse"));
    }
}
