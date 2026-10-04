using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// The lot / batch inquiry (Phase 2, item 11) — the lot passport with its on-hand piles and its posted
/// movements, including the ageing figures and the inclusive expiry window.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryLotInquiry)]
public class IvLotInquiryServiceTests : IAsyncLifetime
{
    private const string Menu = MenuCodes.InventoryLotInquiry;

    private static readonly DateTime AsOf = new(2026, 9, 24);

    private IvHistoryTestDb _db = null!;

    public async Task InitializeAsync() => _db = new IvHistoryTestDb();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static IvLotInquiryQuery Query(string? iCode = null, string? lotNo = null) =>
        new()
        {
            ICode = iCode,
            LotNo = lotNo,
            Take = 100
        };

    // ── The passport ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ReturnsThePassport_WithItsOnHandAndPileCount()
    {
        var lot = await _db.SeedLotAsync(
            "A100", "L1",
            receiptDate: AsOf.AddDays(-40),
            expiryDate: AsOf.AddDays(50),
            sourceType: "GR", sourceDocNo: "GR-0007", supplierCode: "SUP9", qcStatus: "PASS");
        await _db.SeedPileAsync("A100", 6m, wh: "MAIN", lotNo: "L1", lotId: lot);
        await _db.SeedPileAsync("A100", 4m, wh: "WH2", lotNo: "L1", lotId: lot);

        var row = Assert.Single((await _db.CreateLotService().SearchAsync(Menu, Query())).Data!.Rows);

        Assert.Equal("A100", row.ICode);
        Assert.Equal("L1", row.LotNo);
        Assert.Equal("GR-0007", row.SourceDocNo);
        Assert.Equal("SUP9", row.SupplierCode);
        Assert.Equal("PASS", row.QcStatus);
        Assert.Equal(10m, row.OnHandQty);
        Assert.Equal(2, row.PileCount);
        Assert.Equal("EA", row.StdUom);
    }

    [Fact]
    public async Task Search_AgeAndDaysToExpiry_UseTheCompanyLocalClock()
    {
        var lot = await _db.SeedLotAsync(
            "A100", "L1",
            receiptDate: new DateTime(2026, 8, 25),
            expiryDate: new DateTime(2026, 10, 4));
        await _db.SeedPileAsync("A100", 1m, lotNo: "L1", lotId: lot);

        var row = Assert.Single((await _db.CreateLotService().SearchAsync(Menu, Query())).Data!.Rows);

        Assert.Equal(30, row.AgeDays);
        Assert.Equal(10, row.DaysToExpiry);
    }

    [Fact]
    public async Task Search_AgeAndDaysToExpiry_FollowTheInjectedClock()
    {
        var lot = await _db.SeedLotAsync(
            "A100", "L1",
            receiptDate: new DateTime(2026, 8, 25),
            expiryDate: new DateTime(2026, 10, 4));
        await _db.SeedPileAsync("A100", 1m, lotNo: "L1", lotId: lot);

        var clock = new FixedCurrentDateService(new DateTime(2026, 9, 14));
        var row = Assert.Single(
            (await _db.CreateLotService(clock).SearchAsync(Menu, Query())).Data!.Rows);

        Assert.Equal(20, row.AgeDays);
        Assert.Equal(20, row.DaysToExpiry);
    }

    [Fact]
    public async Task Search_ALotWithNoStockLeft_IsStillListed_WithZeroQuantity()
    {
        // Its movements are the audit trail, so the passport must not disappear when the stock does.
        await _db.SeedLotAsync("A100", "EMPTY", receiptDate: AsOf.AddDays(-10), expiryDate: null);

        var row = Assert.Single((await _db.CreateLotService().SearchAsync(Menu, Query())).Data!.Rows);

        Assert.Equal(0m, row.OnHandQty);
        Assert.Equal(0, row.PileCount);
    }

    [Fact]
    public async Task Search_AnItemWithNoLotRows_NeverAppears()
    {
        // Non-lot items are explicitly out: A101 exists in the item master but has no lot.
        await _db.SeedLotAsync("A100", "L1", receiptDate: AsOf.AddDays(-1), expiryDate: null);

        var rows = (await _db.CreateLotService().SearchAsync(Menu, Query())).Data!.Rows;

        Assert.DoesNotContain(rows, x => x.ICode == "A101");
    }

    // ── Filters ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_LotNo_IsASubstringMatch()
    {
        await _db.SeedLotAsync("A100", "BATCH-A1", receiptDate: AsOf.AddDays(-1), expiryDate: null);
        await _db.SeedLotAsync("A100", "OTHER", receiptDate: AsOf.AddDays(-1), expiryDate: null);

        var rows = (await _db.CreateLotService().SearchAsync(Menu, Query(lotNo: "ATCH-A"))).Data!.Rows;

        Assert.Equal("BATCH-A1", Assert.Single(rows).LotNo);
    }

    [Fact]
    public async Task Search_ExpiryWindow_IsInclusiveOfBothBounds()
    {
        await _db.SeedLotAsync("A100", "ON-FROM", receiptDate: AsOf.AddDays(-1), expiryDate: AsOf);
        await _db.SeedLotAsync("A100", "ON-TO", receiptDate: AsOf.AddDays(-1), expiryDate: AsOf.AddDays(10));
        await _db.SeedLotAsync("A100", "OUTSIDE", receiptDate: AsOf.AddDays(-1), expiryDate: AsOf.AddDays(11));

        var query = Query();
        query.ExpiryFrom = AsOf;
        query.ExpiryTo = AsOf.AddDays(10);

        var rows = (await _db.CreateLotService().SearchAsync(Menu, query)).Data!.Rows;

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, x => x.LotNo == "OUTSIDE");
    }

    [Fact]
    public async Task Search_QcStatus_IsAnExactFilter()
    {
        await _db.SeedLotAsync("A100", "PASSED", receiptDate: AsOf.AddDays(-1), expiryDate: null, qcStatus: "PASS");
        await _db.SeedLotAsync("A100", "QUARANTINED", receiptDate: AsOf.AddDays(-1), expiryDate: null, qcStatus: "HOLD");

        var query = Query();
        query.QcStatus = "HOLD";

        var rows = (await _db.CreateLotService().SearchAsync(Menu, query)).Data!.Rows;

        Assert.Equal("QUARANTINED", Assert.Single(rows).LotNo);
    }

    [Fact]
    public async Task Search_InactiveLots_AreInByDefault_AndExcludable()
    {
        await _db.SeedLotAsync("A100", "DEAD", receiptDate: AsOf.AddDays(-1), expiryDate: null, isActive: false);

        var service = _db.CreateLotService();
        Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows);

        var query = Query();
        query.IncludeInactive = false;
        Assert.Empty((await service.SearchAsync(Menu, query)).Data!.Rows);
    }

    [Fact]
    public async Task Search_SearchText_MatchesSourceDocumentAndSupplier()
    {
        await _db.SeedLotAsync("A100", "L1", receiptDate: AsOf.AddDays(-1), expiryDate: null,
            sourceDocNo: "GR-4242", supplierCode: "SUP-77");

        var service = _db.CreateLotService();

        Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows);
        Assert.Single((await service.SearchAsync(Menu, new IvLotInquiryQuery { SearchText = "4242", Take = 100 })).Data!.Rows);
        Assert.Single((await service.SearchAsync(Menu, new IvLotInquiryQuery { SearchText = "SUP-77", Take = 100 })).Data!.Rows);
        Assert.Empty((await service.SearchAsync(Menu, new IvLotInquiryQuery { SearchText = "nothing", Take = 100 })).Data!.Rows);
    }

    // ── Detail: piles and movements ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDetail_ReturnsPilesAndScopeAwareMovements()
    {
        var lot = await _db.SeedLotAsync("A100", "L1", receiptDate: AsOf.AddDays(-5), expiryDate: AsOf.AddDays(20));
        await _db.SeedPileAsync("A100", 12m, wh: "MAIN", loc: "BIN1", lotNo: "L1", lotId: lot);

        // Receipt into the lot, then an issue out of it: the ledger must net to +12 - 2 = 10.
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toLot: "L1", toWh: "MAIN", toLoc: "BIN1", toStdQty: 12m, frStdQty: null);
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MI", frLot: "L1", frWh: "MAIN", frLoc: "BIN1", frStdQty: 2m, toStdQty: null, toWh: null, toLoc: null);

        var service = _db.CreateLotService();
        var selected = Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows);

        var detail = await service.GetDetailAsync(Menu, selected);

        Assert.True(detail.Succeeded);
        Assert.Equal(12m, detail.Data!.PileQty);
        Assert.Single(detail.Data.Piles);
        Assert.Equal(2, detail.Data.Movements.Count);
        Assert.Equal(10m, detail.Data.MovementNetQty);

        var receipt = detail.Data.Movements.Single(x => x.TrxType == "MR");
        Assert.Equal(12m, receipt.InQty);
        Assert.Equal(0m, receipt.OutQty);

        var issue = detail.Data.Movements.Single(x => x.TrxType == "MI");
        Assert.Equal(0m, issue.InQty);
        Assert.Equal(2m, issue.OutQty);
    }

    [Fact]
    public async Task GetDetail_MovementList_DoesNotIncludeOtherLots()
    {
        var lot = await _db.SeedLotAsync("A100", "L1", receiptDate: AsOf.AddDays(-5), expiryDate: null);
        await _db.SeedPileAsync("A100", 5m, lotNo: "L1", lotId: lot);
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toLot: "L1", toStdQty: 5m);
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toLot: "L2", toStdQty: 9m);

        var service = _db.CreateLotService();
        var selected = Assert.Single((await service.SearchAsync(Menu, Query())).Data!.Rows);

        var detail = await service.GetDetailAsync(Menu, selected);

        Assert.Equal("L1", Assert.Single(detail.Data!.Movements).ToLotNo);
    }

    [Fact]
    public async Task GetDetail_WithoutALot_IsRejected()
    {
        var result = await _db.CreateLotService().GetDetailAsync(Menu, null);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    // ── Aggregate ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Summary_CountsLotsExpiredLotsAndLotsWithNoExpiry()
    {
        await _db.SeedLotAsync("A100", "EXPIRED", receiptDate: AsOf.AddDays(-90), expiryDate: AsOf.AddDays(-1));
        await _db.SeedLotAsync("A100", "FRESH", receiptDate: AsOf.AddDays(-1), expiryDate: AsOf.AddDays(30));
        await _db.SeedLotAsync("A100", "NOEXP", receiptDate: AsOf.AddDays(-1), expiryDate: null);

        var summary = await _db.CreateLotService().GetSummaryAsync(Menu, Query());

        Assert.True(summary.Succeeded);
        Assert.Equal(3, summary.Data!.TotalRows);
        Assert.Equal(1, summary.Data.ItemCount);
        Assert.Equal(1, summary.Data.ExpiredCount);
        Assert.Equal(1, summary.Data.NoExpiryCount);
    }

    [Fact]
    public async Task Summary_TotalOnHand_EqualsTheSumOfTheLotQuantities()
    {
        var lot1 = await _db.SeedLotAsync("A100", "L1", receiptDate: AsOf.AddDays(-1), expiryDate: null);
        var lot2 = await _db.SeedLotAsync("A100", "L2", receiptDate: AsOf.AddDays(-1), expiryDate: null);
        await _db.SeedPileAsync("A100", 3m, wh: "MAIN", lotNo: "L1", lotId: lot1);
        await _db.SeedPileAsync("A100", 4m, wh: "WH2", lotNo: "L2", lotId: lot2);

        var summary = await _db.CreateLotService().GetSummaryAsync(Menu, Query());

        Assert.Equal(7m, summary.Data!.TotalOnHandQty);
    }

    [Fact]
    public async Task Summary_TotalMatchesTheGridTotalCount()
    {
        await _db.SeedLotAsync("A100", "L1", receiptDate: AsOf.AddDays(-1), expiryDate: null);
        await _db.SeedLotAsync("A100", "L2", receiptDate: AsOf.AddDays(-1), expiryDate: null);

        var service = _db.CreateLotService();
        var summary = await service.GetSummaryAsync(Menu, Query());

        foreach (var pageSize in new[] { 1, 50 })
        {
            var query = Query();
            query.Take = pageSize;
            Assert.Equal(summary.Data!.TotalRows, (await service.SearchAsync(Menu, query)).Data!.TotalCount);
        }
    }

    // ── Security and tenant scope ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_OnAnotherCompany_SeesNothing()
    {
        await _db.SeedLotAsync("A100", "L1", receiptDate: AsOf.AddDays(-1), expiryDate: null);

        var result = await _db.CreateLotService(company: "OTHER").SearchAsync(Menu, Query());

        Assert.True(result.Succeeded);
        Assert.Empty(result.Data!.Rows);
    }

    [Fact]
    public async Task Search_DeniedAccess_IsRejected()
    {
        var result = await _db.CreateLotService(access: IvHistoryTestDb.Deny(PermissionCodes.Access))
            .SearchAsync(Menu, Query());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Search_OnAMenuThisServiceDoesNotServe_IsRejected()
    {
        var result = await _db.CreateLotService().SearchAsync(MenuCodes.InventoryStockCard, Query());

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task Search_ChecksAccessOnTheLotMenuItself()
    {
        var (access, checks) = IvHistoryTestDb.RecordingAccess();

        await _db.CreateLotService(access: access).SearchAsync(Menu, Query());

        Assert.Contains((Menu, PermissionCodes.Access), checks);
    }

    [Fact]
    public async Task GetDetail_ReRunsTheTenantAndAccessCheck_OnAClientSuppliedRow()
    {
        // A row the caller claims to have does not bypass the gates.
        var fake = new IvLotInquiryRow
        {
            LotId = 1,
            ICode = "A100",
            LotNo = "L1"
        };

        var denied = await _db.CreateLotService(access: IvHistoryTestDb.Deny(PermissionCodes.Access))
            .GetDetailAsync(Menu, fake);
        Assert.False(denied.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, denied.ErrorCode);

        var otherCompany = await _db.CreateLotService(company: "OTHER").GetDetailAsync(Menu, fake);
        Assert.True(otherCompany.Succeeded);
        Assert.Empty(otherCompany.Data!.Piles);
        Assert.Empty(otherCompany.Data.Movements);
    }
}
