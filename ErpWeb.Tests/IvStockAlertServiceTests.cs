using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// The stock-alert rules (Phase 2, item 10). Every case in the plan's mandatory fixture list is an
/// asserted test rather than a manual smoke: NULL and zero thresholds, exactly-on-the-threshold and
/// one-unit-either-side, an item with no pile at all, no movement ever, both ageing boundaries,
/// "expires today is not expired" and the expiry horizon's inclusive edge.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockAlerts)]
public class IvStockAlertServiceTests : IAsyncLifetime
{
    private const string Menu = MenuCodes.InventoryStockAlerts;

    /// <summary>The as-of date every test's clock is fixed to, so ageing is deterministic.</summary>
    private static readonly DateTime AsOf = new(2026, 9, 24);

    private IvHistoryTestDb _db = null!;

    public async Task InitializeAsync() => _db = new IvHistoryTestDb();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static IvStockAlertQuery Query(
        string rule,
        string? iCode = null,
        string? whCode = null,
        int slowDays = 90,
        int deadDays = 180,
        int expiryDays = 30,
        bool includeInactive = false,
        bool includeNonStockControl = false) =>
        new()
        {
            Rule = rule,
            ICode = iCode,
            WhCode = whCode,
            SlowDays = slowDays,
            DeadDays = deadDays,
            ExpiryDays = expiryDays,
            IncludeInactive = includeInactive,
            IncludeNonStockControl = includeNonStockControl,
            Take = 100
        };

    // ── LOW ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Low_ItemWithNoPileAtAll_StillAlerts()
    {
        // The item-master-driven requirement: a balance-driven query could never see this item.
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low));

        Assert.True(result.Succeeded);
        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("A100", row.ICode);
        Assert.Equal(0m, row.OnHand);
        Assert.Equal(-10m, row.Variance);
        Assert.Null(row.LastMovement);
    }

    [Theory]
    [InlineData(9.9999, true)]   // one unit (4 dp) below the threshold
    [InlineData(10, false)]      // exactly ON the threshold is NOT below it
    [InlineData(10.0001, false)]
    public async Task Low_ComparesStrictlyBelowTheThreshold(double onHand, bool expected)
    {
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);
        await _db.SeedPileAsync("A100", (decimal)onHand);

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low));

        Assert.Equal(expected, result.Data!.Rows.Count == 1);
    }

    [Fact]
    public async Task Low_NullOrZeroMinimum_NeverAlerts()
    {
        await _db.SeedPileAsync("A100", 0m);

        await _db.SetThresholdsAsync("A100", min: null, max: null);
        var withNull = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low));
        Assert.Empty(withNull.Data!.Rows);

        await _db.SetThresholdsAsync("A100", min: 0m, max: null);
        var withZero = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low));
        Assert.Empty(withZero.Data!.Rows);
    }

    [Fact]
    public async Task Low_ZeroOnHandWithAConfiguredMinimum_Alerts()
    {
        await _db.SetThresholdsAsync("A100", min: 5m, max: null);
        await _db.SeedPileAsync("A100", 0m);

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low));

        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(0m, row.OnHand);
    }

    [Fact]
    public async Task Low_WarehouseFilter_MeasuresOnlyThatWarehouse()
    {
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);
        await _db.SeedPileAsync("A100", 3m, wh: "MAIN");
        await _db.SeedPileAsync("A100", 20m, wh: "WH2");

        var all = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low));
        Assert.Empty(all.Data!.Rows); // 23 across the branch is above 10

        var pinned = await _db.CreateAlertService()
            .SearchAsync(Menu, Query(IvStockAlertRules.Low, whCode: "MAIN"));

        var row = Assert.Single(pinned.Data!.Rows);
        Assert.Equal(3m, row.OnHand);
        Assert.Equal("MAIN", row.ThresholdBasis);
    }

    [Fact]
    public async Task Low_ThresholdBasis_NamesAllWarehousesWhenNoneIsPinned()
    {
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low));

        Assert.Equal(
            IvStockInquiryRepository.AllWarehousesBasis,
            Assert.Single(result.Data!.Rows).ThresholdBasis);
    }

    // ── OVER ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(5, false)]       // exactly ON the maximum is NOT above it
    [InlineData(4.9999, false)]
    [InlineData(5.0001, true)]
    public async Task Over_ComparesStrictlyAboveTheThreshold(double onHand, bool expected)
    {
        await _db.SetThresholdsAsync("A100", min: null, max: 5m);
        await _db.SeedPileAsync("A100", (decimal)onHand);

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Over));

        Assert.Equal(expected, result.Data!.Rows.Count == 1);
    }

    [Fact]
    public async Task Over_NullOrZeroMaximum_NeverAlerts()
    {
        await _db.SeedPileAsync("A100", 1000m);

        await _db.SetThresholdsAsync("A100", min: null, max: null);
        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Over))).Data!.Rows);

        await _db.SetThresholdsAsync("A100", min: null, max: 0m);
        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Over))).Data!.Rows);
    }

    [Fact]
    public async Task Over_ReportsAPositiveVarianceOverTheMaximum()
    {
        await _db.SetThresholdsAsync("A100", min: null, max: 5m);
        await _db.SeedPileAsync("A100", 12m);

        var row = Assert.Single(
            (await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Over))).Data!.Rows);

        Assert.Equal(7m, row.Variance);
    }

    // ── SLOW / DEAD / NEVER MOVED ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Slow_MovementOlderThanTheHorizon_Alerts_AndReportsDaysSince()
    {
        await _db.SeedPileAsync("A100", 5m);
        await _db.SeedHistoryAsync(trxDtTime: AsOf.AddDays(-120));

        var row = Assert.Single(
            (await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Slow))).Data!.Rows);

        Assert.Equal(5m, row.OnHand);
        Assert.Equal(AsOf.AddDays(-120), row.LastMovement);
        Assert.Equal(120, row.DaysSinceMovement);
    }

    [Fact]
    public async Task Slow_MovementInsideTheHorizon_DoesNotAlert()
    {
        await _db.SeedPileAsync("A100", 5m);
        await _db.SeedHistoryAsync(trxDtTime: AsOf.AddDays(-89));

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Slow));

        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task Slow_RespectsTheSlowDaysFilterParameter()
    {
        await _db.SeedPileAsync("A100", 5m);
        await _db.SeedHistoryAsync(trxDtTime: AsOf.AddDays(-100));

        // Same data, different parameter: the threshold is a filter, not a constant.
        var wide = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Slow, slowDays: 120));
        Assert.Empty(wide.Data!.Rows);

        var narrow = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Slow, slowDays: 30));
        Assert.Single(narrow.Data!.Rows);
    }

    [Fact]
    public async Task Dead_UsesDeadDays_SoAnItemIsSlowBeforeItIsDead()
    {
        await _db.SeedPileAsync("A100", 5m);
        await _db.SeedHistoryAsync(trxDtTime: AsOf.AddDays(-146));

        Assert.Single((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Slow))).Data!.Rows);
        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Dead))).Data!.Rows);

        var dead = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Dead, deadDays: 120));
        Assert.Single(dead.Data!.Rows);
    }

    [Theory]
    [InlineData(IvStockAlertRules.Slow)]
    [InlineData(IvStockAlertRules.Dead)]
    [InlineData(IvStockAlertRules.NeverMoved)]
    public async Task MovementRules_RequireStockOnHand(string rule)
    {
        // D21: an item with no stock anywhere is not an operational stock alert, even with an ancient
        // movement history (a fully consumed item has moved, but holds nothing).
        await _db.SeedHistoryAsync(trxDtTime: AsOf.AddDays(-900));

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(rule));

        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task NeverMoved_HasStockAndNoHistory_Alerts()
    {
        await _db.SeedPileAsync("A100", 7m);

        var row = Assert.Single(
            (await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.NeverMoved))).Data!.Rows);

        Assert.Null(row.LastMovement);
        Assert.Null(row.DaysSinceMovement);
        Assert.Equal(7m, row.OnHand);
    }

    [Fact]
    public async Task NeverMoved_StockThatHasMovedAtAll_IsNotReported()
    {
        await _db.SeedPileAsync("A100", 7m);
        await _db.SeedHistoryAsync(trxDtTime: AsOf.AddDays(-900));

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.NeverMoved));

        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task DeadDays_NotGreaterThanSlowDays_IsRejectedNamingBothNumbers()
    {
        var result = await _db.CreateAlertService().SearchAsync(
            Menu,
            Query(IvStockAlertRules.Dead, slowDays: 90, deadDays: 90));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("90", result.Message);
        Assert.Contains("Dead days", result.Message);
        Assert.Contains("slow days", result.Message);
    }

    // ── EXPIRING / EXPIRED ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expiring_LotInsideTheHorizon_IsIn_AndABeyondItIsOut()
    {
        var inLot = await _db.SeedLotAsync("A100", "IN", receiptDate: AsOf.AddDays(-10), expiryDate: AsOf.AddDays(30));
        var outLot = await _db.SeedLotAsync("A100", "OUT", receiptDate: AsOf.AddDays(-10), expiryDate: AsOf.AddDays(31));
        await _db.SeedPileAsync("A100", 4m, lotNo: "IN", lotId: inLot);
        await _db.SeedPileAsync("A100", 4m, lotNo: "OUT", lotId: outLot);

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expiring));

        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal("IN", row.LotNo);
        Assert.Equal(30, row.DaysToExpiry);
        Assert.Equal("MAIN", row.WhCode);
    }

    [Fact]
    public async Task Expiring_LotExpiringExactlyToday_IsNotExpired_ButIsNotExpiringEither()
    {
        var lot = await _db.SeedLotAsync("A100", "TODAY", receiptDate: AsOf.AddDays(-5), expiryDate: AsOf);
        await _db.SeedPileAsync("A100", 2m, lotNo: "TODAY", lotId: lot);

        // Expiring needs expiry >= AsOf, so today is eligible...
        Assert.Single((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expiring))).Data!.Rows);

        // ...and expired needs expiry < AsOf, so a lot expiring today is NOT expired.
        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expired))).Data!.Rows);
    }

    [Fact]
    public async Task Expired_LotExpiringYesterday_IsIn()
    {
        var lot = await _db.SeedLotAsync("A100", "YESTERDAY", receiptDate: AsOf.AddDays(-30), expiryDate: AsOf.AddDays(-1));
        await _db.SeedPileAsync("A100", 3m, lotNo: "YESTERDAY", lotId: lot);

        var row = Assert.Single(
            (await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expired))).Data!.Rows);

        Assert.Equal(-1, row.DaysToExpiry);
    }

    [Fact]
    public async Task Expiring_LotWithNoPileInScope_IsNotAnAlert()
    {
        await _db.SeedLotAsync("A100", "GHOST", receiptDate: AsOf.AddDays(-5), expiryDate: AsOf.AddDays(3));

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expiring));

        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task Expiring_LotWithNoExpiryDate_IsNeverReported()
    {
        var lot = await _db.SeedLotAsync("A100", "NOEXP", receiptDate: AsOf.AddDays(-5), expiryDate: null);
        await _db.SeedPileAsync("A100", 2m, lotNo: "NOEXP", lotId: lot);

        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expiring))).Data!.Rows);
        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expired))).Data!.Rows);
    }

    [Fact]
    public async Task ExpiryRows_AreOnePerLotPerWarehouse_WithAUniqueRowKey()
    {
        var lot = await _db.SeedLotAsync("A100", "SPREAD", receiptDate: AsOf.AddDays(-5), expiryDate: AsOf.AddDays(5));
        await _db.SeedPileAsync("A100", 2m, wh: "MAIN", lotNo: "SPREAD", lotId: lot);
        await _db.SeedPileAsync("A100", 3m, wh: "WH2", lotNo: "SPREAD", lotId: lot);

        var result = await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Expiring));

        Assert.Equal(2, result.Data!.Rows.Count);
        Assert.Equal(5m, result.Data.Rows.Sum(x => x.OnHand));
        Assert.Equal(2, result.Data.Rows.Select(x => x.RowKey).Distinct().Count());
    }

    // ── The as-of date, the guards and the aggregate ────────────────────────────────────────────────

    [Fact]
    public async Task AsOfDate_ComesFromTheCompanyLocalClock_NotTheServerClock()
    {
        await _db.SeedPileAsync("A100", 5m);
        await _db.SeedHistoryAsync(trxDtTime: new DateTime(2030, 6, 1));

        // A clock two years ahead makes a "future" movement 90 days old; the real clock would not.
        var clock = new FixedCurrentDateService(new DateTime(2030, 9, 9));
        var result = await _db.CreateAlertService(clock)
            .SearchAsync(Menu, Query(IvStockAlertRules.Slow));

        var row = Assert.Single(result.Data!.Rows);
        Assert.Equal(100, row.DaysSinceMovement);
        Assert.Equal(new DateTime(2030, 9, 9), result.Data.AsOfDate);
    }

    [Fact]
    public async Task InactiveOrNonStockControlledItems_AreOutByDefault_AndInOnOptIn()
    {
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);
        await _db.SetItemFlagsAsync("A100", isActive: false);

        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low))).Data!.Rows);
        Assert.Single((await _db.CreateAlertService()
            .SearchAsync(Menu, Query(IvStockAlertRules.Low, includeInactive: true))).Data!.Rows);

        await _db.SetItemFlagsAsync("A100", isActive: true, stockControl: false);

        Assert.Empty((await _db.CreateAlertService().SearchAsync(Menu, Query(IvStockAlertRules.Low))).Data!.Rows);
        Assert.Single((await _db.CreateAlertService()
            .SearchAsync(Menu, Query(IvStockAlertRules.Low, includeNonStockControl: true))).Data!.Rows);
    }

    [Fact]
    public async Task Summary_CountsRowsAndDistinctItems_AndSumsOnHand()
    {
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);
        await _db.SetThresholdsAsync("A101", min: 10m, max: null);
        await _db.SeedPileAsync("A100", 2m);
        await _db.SeedPileAsync("A101", 3m);

        var summary = await _db.CreateAlertService().GetSummaryAsync(Menu, Query(IvStockAlertRules.Low));

        Assert.True(summary.Succeeded);
        Assert.Equal(2, summary.Data!.TotalRows);
        Assert.Equal(2, summary.Data.ItemCount);
        Assert.Equal(5m, summary.Data.TotalOnHand);
    }

    [Fact]
    public async Task Summary_AndGrid_AndExport_AgreeAtTwoPageSizes()
    {
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);
        await _db.SetThresholdsAsync("A101", min: 10m, max: null);
        await _db.SeedPileAsync("A100", 2m);

        var service = _db.CreateAlertService();
        var summary = await service.GetSummaryAsync(Menu, Query(IvStockAlertRules.Low));

        foreach (var pageSize in new[] { 1, 50 })
        {
            var query = Query(IvStockAlertRules.Low);
            query.Take = pageSize;

            var grid = await service.SearchAsync(Menu, query);
            var export = await service.ExportRowsAsync(Menu, query);

            // The aggregate provably does not depend on paging, and the export carries the same count.
            Assert.Equal(summary.Data!.TotalRows, grid.Data!.TotalCount);
            Assert.Equal(summary.Data.TotalRows, export.Data!.TotalCount);
        }
    }

    // ── Security and tenant scope ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_OnAnotherCompany_SeesNothing()
    {
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);

        var result = await _db.CreateAlertService(company: "OTHER")
            .SearchAsync(Menu, Query(IvStockAlertRules.Low));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task Search_OnAnotherBranch_MeasuresOnlyThatBranchsStock()
    {
        // The item master is COMPANY-scoped (MinStock is an item-level column, D3), so the item is still
        // a candidate in the other branch — but the quantity must come from THAT branch's piles, not HQ's.
        await _db.SetThresholdsAsync("A100", min: 10m, max: null);
        await _db.SeedPileAsync("A100", 1m, wh: "MAIN");

        var otherBranch = await _db.CreateAlertService(branch: "BR2")
            .SearchAsync(Menu, Query(IvStockAlertRules.Low));

        var row = Assert.Single(otherBranch.Data!.Rows);
        Assert.Equal(0m, row.OnHand);

        var here = Assert.Single((await _db.CreateAlertService()
            .SearchAsync(Menu, Query(IvStockAlertRules.Low))).Data!.Rows);
        Assert.Equal(1m, here.OnHand);
    }

    [Fact]
    public async Task Search_DeniedAccess_IsRejected()
    {
        var result = await _db.CreateAlertService(access: IvHistoryTestDb.Deny(PermissionCodes.Access))
            .SearchAsync(Menu, Query(IvStockAlertRules.Low));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Search_OnAMenuThisServiceDoesNotServe_IsRejected()
    {
        var result = await _db.CreateAlertService()
            .SearchAsync(MenuCodes.InventoryStockSummary, Query(IvStockAlertRules.Low));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task Search_ChecksAccessOnTheAlertsMenuItself()
    {
        var (access, checks) = IvHistoryTestDb.RecordingAccess();

        await _db.CreateAlertService(access: access).SearchAsync(Menu, Query(IvStockAlertRules.Low));

        Assert.Contains((Menu, PermissionCodes.Access), checks);
        Assert.DoesNotContain(checks, c => c.Permission == PermissionCodes.ViewPrice);
    }

    [Fact]
    public void RuleTokens_AreTheSevenThePlanNamed()
    {
        Assert.Equal(
            new[] { "LOW", "OVER", "SLOW", "DEAD", "NEVER_MOVED", "EXPIRING", "EXPIRED" },
            IvStockAlertRules.All);
        Assert.False(IvStockAlertRules.IsLotRule("LOW"));
        Assert.True(IvStockAlertRules.IsLotRule("EXPIRED"));
        Assert.True(IvStockAlertRules.IsMovementRule("NEVER_MOVED"));
        Assert.Equal(IvStockAlertRules.Default, IvStockAlertRules.Normalize("nonsense"));
    }
}
