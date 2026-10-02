using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Inventory reconciliation (Phase 3, item 16). The load-bearing test here is D18: both sides must be
/// aggregated on the same 7-part stock slice before they are compared, so a legacy database holding two
/// balance rows for one slice does not manufacture a discrepancy that does not exist.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryReconciliation)]
public class IvReconciliationServiceTests : IAsyncLifetime
{
    private IvHistoryTestDb _db = null!;

    public async Task InitializeAsync() => _db = new IvHistoryTestDb();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private IIvInventoryReconciliationService Service(Mock<IAccessRightService>? access = null) =>
        new IvInventoryReconciliationService(
            _db.Factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            (access ?? IvHistoryTestDb.Access()).Object);

    // ── D18: the slice is the unit of comparison ────────────────────────────────────────────────────

    [Fact]
    public async Task NoFindings_WhenTheBalanceAndTheLedgerAgree()
    {
        var pileId = await _db.SeedPileAsync("A100", 10m);
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: pileId, toStdQty: 10m, frStdQty: null);

        var result = await Service().ReconcileAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Findings);
        Assert.Equal("OK", result.Status);
    }

    [Fact]
    public async Task TheComparisonUnitIsTheSlice_AndTheFindingNamesIt()
    {
        // The unique index on (company, branch, item, warehouse, bin, lot, status) means two balance rows
        // for ONE slice cannot exist in this schema — which is exactly why the comparison is defined on
        // the slice and not on the row: a legacy database without that index is the only way the two
        // could differ, and this finding would then be the thing that tells you.
        var pileId = await _db.SeedPileAsync("A100", 8m);
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: pileId, toStdQty: 12m, frStdQty: null);

        var result = await Service().ReconcileAsync();

        var mismatch = Assert.Single(result.Findings, f => f.Code == "MISMATCH");
        Assert.Equal(pileId, mismatch.BalLocId);
        Assert.Equal(8m, mismatch.BalLocQty);
        Assert.Equal(12m, mismatch.HistoryNetQty);

        // The 7-part slice, in IvStockSliceKey's column order, so the reader knows exactly which stock.
        Assert.Equal("DEMO/HQ/A100/MAIN/BIN1//ACTIVE", mismatch.Slice);
    }

    [Fact]
    public async Task AHistoryLegIsAttributedToItsOwnSlice_NotToTheItemsWholeLedger()
    {
        // One item, two piles, and a single receipt into ONE of them. Attributing the receipt to the item
        // would make the other, empty pile look 4 short.
        var received = await _db.SeedPileAsync("A100", 4m, wh: "MAIN");
        var untouched = await _db.SeedPileAsync("A100", 0m, wh: "WH2");
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: received, toStdQty: 4m, frStdQty: null);

        var result = await Service().ReconcileAsync();

        Assert.Empty(result.Findings);
        Assert.NotEqual(received, untouched);
    }

    [Fact]
    public async Task OrphanHistory_IsReported()
    {
        var pileId = await _db.SeedPileAsync("A100", 5m);
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: pileId, toStdQty: 5m, frStdQty: null);

        // A branch-HQ history row pointing at a pile that belongs to ANOTHER branch. The pile exists (so
        // every FK and unique index is satisfied) but it is not a HQ pile, so this row can never
        // reconcile here — which is precisely what ORPHAN_HISTORY means.
        var otherBranchPile = await SeedOtherBranchPileAsync();
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: otherBranchPile, toStdQty: 3m, frStdQty: null);

        var result = await Service().ReconcileAsync();

        Assert.Contains(result.Findings, f => f.Code == "ORPHAN_HISTORY");
    }

    /// <summary>
    /// A pile in another branch. It needs its own warehouse row first: <c>IvBalLoc</c> has a real FK to
    /// (CompanyCode, BranchCode, WhCode), so a branch cannot be faked on the pile alone.
    /// </summary>
    private async Task<int> SeedOtherBranchPileAsync()
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "BR2"))
        {
            db.IvWarehouses.Add(new ErpWeb.Model.Entities.Inventory.IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                WarehouseCode = "MAIN",
                WarehouseDesc = "Second branch store",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var pile = new ErpWeb.Model.Entities.Inventory.IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "BR2",
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = 3m,
            StdUom = "EA"
        };

        db.IvBalLocs.Add(pile);
        await db.SaveChangesAsync();
        return pile.Id;
    }

    [Fact]
    public async Task AFilteredQuery_DoesNotManufactureAnOrphan()
    {
        // A transfer out of WH2 into MAIN, reported while asking about MAIN only. The WH2 pile is a real
        // pile, so its leg is not orphaned history — the check is about existence, not about the filter.
        var source = await _db.SeedPileAsync("A100", 0m, wh: "WH2");
        var dest = await _db.SeedPileAsync("A100", 4m, wh: "MAIN");

        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: source, toStdQty: 4m, frStdQty: null);
        await _db.SeedHistoryAsync(
            iCode: "A100",
            trxType: "TR",
            fromBalLocId: source,
            toBalLocId: dest,
            frStdQty: 4m,
            toStdQty: 4m,
            frWh: "WH2",
            frLoc: "BIN1",
            toWh: "MAIN",
            toLoc: "BIN1");

        var result = await Service().ReconcileAsync(whCode: "MAIN");

        Assert.DoesNotContain(result.Findings, f => f.Code == "ORPHAN_HISTORY");
        Assert.DoesNotContain(result.Findings, f => f.Code == "MISMATCH");
    }

    [Fact]
    public async Task GenuineMismatch_IsReportedWithBothQuantities()
    {
        var pileId = await _db.SeedPileAsync("A100", 9m);
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: pileId, toStdQty: 10m, frStdQty: null);

        var result = await Service().ReconcileAsync();

        var mismatch = Assert.Single(result.Findings, f => f.Code == "MISMATCH");
        Assert.Equal(9m, mismatch.BalLocQty);
        Assert.Equal(10m, mismatch.HistoryNetQty);
    }

    [Fact]
    public async Task HistorySliceMismatch_is_reported_even_when_quantities_agree()
    {
        var pileId = await _db.SeedPileAsync("A100", 10m);
        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            var pile = await db.IvBalLocs.SingleAsync(x => x.Id == pileId);
            pile.TransDate = new DateTime(2026, 8, 1);
            await db.SaveChangesAsync();
        }

        var historyId = await _db.SeedHistoryAsync(
            iCode: "A100",
            trxType: "MR",
            toBalLocId: pileId,
            toStdQty: 10m,
            toLoc: "BIN2");

        var result = await Service().ReconcileAsync();

        var finding = Assert.Single(result.Findings, f => f.Code == "HISTORY_SLICE_MISMATCH");
        Assert.Equal(historyId, finding.HistoryId);
        Assert.Equal(pileId, finding.BalLocId);
        Assert.Contains("BIN2", finding.Message);
        Assert.Contains("BIN1", finding.Message);
    }

    [Fact]
    public async Task HistorySliceMismatch_filter_matches_recorded_or_actual_identity()
    {
        var pileId = await _db.SeedPileAsync("A101", 5m, wh: "WH2");
        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            var pile = await db.IvBalLocs.SingleAsync(x => x.Id == pileId);
            pile.TransDate = new DateTime(2026, 8, 1);
            await db.SaveChangesAsync();
        }

        await _db.SeedHistoryAsync(
            iCode: "A100",
            trxType: "MR",
            toBalLocId: pileId,
            toStdQty: 5m,
            toWh: "MAIN",
            toLoc: "BIN1");

        var actualFilter = await Service().ReconcileAsync(iCode: "A101", whCode: "WH2");
        Assert.Contains(actualFilter.Findings, f => f.Code == "HISTORY_SLICE_MISMATCH");

        var recordedFilter = await Service().ReconcileAsync(iCode: "A100", whCode: "MAIN");
        Assert.Contains(recordedFilter.Findings, f => f.Code == "HISTORY_SLICE_MISMATCH");
    }

    [Fact]
    public async Task ATransfer_NetsAgainstBothOfItsLegs()
    {
        // The source opened with 8, sent 4, and therefore holds 4; the destination received 4.
        var source = await _db.SeedPileAsync("A100", 4m, wh: "MAIN");
        var dest = await _db.SeedPileAsync("A100", 4m, wh: "WH2");

        await _db.SeedHistoryAsync(iCode: "A100", trxType: "MR", toBalLocId: source, toStdQty: 8m, frStdQty: null);

        await _db.SeedHistoryAsync(
            iCode: "A100",
            trxType: "TR",
            fromBalLocId: source,
            toBalLocId: dest,
            frStdQty: 4m,
            toStdQty: 4m,
            frWh: "MAIN",
            frLoc: "BIN1",
            toWh: "WH2",
            toLoc: "BIN1");

        var result = await Service().ReconcileAsync();

        // Each leg is attributed to ITS OWN slice, so both piles reconcile with no finding at all.
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task BalanceWithNoHistoryAtAll_IsReportedAsUnexpected()
    {
        await _db.SeedPileAsync("A100", 5m);

        var result = await Service().ReconcileAsync();

        Assert.Contains(result.Findings, f => f.Code == "UNEXPECTED_BALANCE");
        Assert.Contains(result.Findings, f => f.Code == "MISMATCH");
    }

    [Fact]
    public async Task NonStockHistory_WithNoPileLink_IsIgnored()
    {
        // A non-stock item's movement never touches on-hand, so it must not raise a finding.
        await _db.SeedHistoryAsync(iCode: "A100", trxType: "NG", toStdQty: 7m, frStdQty: null);

        var result = await Service().ReconcileAsync();

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task FindingsAreOrderedBySlice_SoTwoRunsDiffCleanly()
    {
        await _db.SeedPileAsync("A101", 1m);
        await _db.SeedPileAsync("A100", 2m);

        var result = await Service().ReconcileAsync();

        var slices = result.Findings
            .Where(f => f.Slice is not null)
            .Select(f => f.Slice!)
            .ToList();

        Assert.Equal(slices.OrderBy(x => x, StringComparer.Ordinal), slices);
    }

    // ── Filters and the gates ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ItemFilter_RestrictsTheFindings()
    {
        await _db.SeedPileAsync("A100", 5m);
        await _db.SeedPileAsync("A101", 7m);

        var result = await Service().ReconcileAsync(iCode: "A101");

        Assert.All(
            result.Findings.Where(f => f.BalLocQty is not null),
            f => Assert.Contains("A101", f.Slice));
        Assert.DoesNotContain(result.Findings, f => f.Slice is not null && f.Slice.Contains("A100"));
    }

    [Fact]
    public async Task WarehouseFilter_RestrictsTheFindings()
    {
        await _db.SeedPileAsync("A100", 5m, wh: "MAIN");
        await _db.SeedPileAsync("A100", 7m, wh: "WH2");

        var result = await Service().ReconcileAsync(whCode: "WH2");

        Assert.DoesNotContain(result.Findings, f => f.Slice is not null && f.Slice.Contains("MAIN"));
        Assert.Contains(result.Findings, f => f.Slice is not null && f.Slice.Contains("WH2"));
    }

    [Fact]
    public async Task DeniedAccess_YieldsNoFindingsAtAll()
    {
        await _db.SeedPileAsync("A100", 5m);

        var result = await Service(IvHistoryTestDb.Deny(PermissionCodes.Access)).ReconcileAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(result.Findings);
        Assert.Contains("Not authorized", result.ErrorMessage);
    }

    [Fact]
    public async Task AccessIsCheckedOnTheReconciliationMenu()
    {
        var checks = new List<(string Menu, string Permission)>();
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string permission, CancellationToken _) =>
            {
                checks.Add((menu, permission));
                return true;
            });

        await Service(access).ReconcileAsync();

        Assert.Contains((MenuCodes.InventoryReconciliation, PermissionCodes.Access), checks);
    }

    [Fact]
    public async Task OtherCompanySeesNothing()
    {
        await _db.SeedPileAsync("A100", 5m);

        var service = new IvInventoryReconciliationService(
            _db.Factory,
            InventoryTenantTestHelper.CreateTenantContext(company: "OTHER"),
            IvHistoryTestDb.Access().Object);

        var result = await service.ReconcileAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task InvalidScope_IsReportedWithoutQuerying()
    {
        var service = new IvInventoryReconciliationService(
            _db.Factory,
            InventoryTenantTestHelper.CreateTenantContext(company: "TOOLONGCOMPANY"),
            IvHistoryTestDb.Access().Object);

        var result = await service.ReconcileAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Invalid company or branch context", result.ErrorMessage);
    }
}
