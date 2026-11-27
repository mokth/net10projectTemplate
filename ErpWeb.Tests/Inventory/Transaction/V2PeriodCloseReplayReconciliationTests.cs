using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Tests.Procurement.Transaction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Inventory.Transaction;

/// <summary>
/// End-to-end V2 period close, reopen, backdated replay, negative historical stock guard,
/// snapshot revisioning, and reclose/carry-forward tests across Inventory and Production.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPeriodClose)]
public sealed class V2PeriodCloseReplayReconciliationTests : IAsyncLifetime
{
    private static readonly DateTime AugFrom = new(2026, 8, 1);
    private static readonly DateTime AugTo = new(2026, 8, 31);
    private static readonly DateTime SepFrom = new(2026, 9, 1);
    private static readonly DateTime SepTo = new(2026, 9, 30);
    private static readonly DateTime TodayOct = new(2026, 10, 15);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public V2PeriodCloseReplayReconciliationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = InventoryLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedCompanyAsync(db);
        db.IvWarehouses.Add(new IvWarehouse { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true });
        db.IvLocations.Add(new IvLocation { CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", LocCode = "BIN1", IsActive = true });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO", ICode = "WAC-PC", IDesc = "WAC Period Close Item",
                IClassCode = "RAW", StdUom = "EA", StockControl = true, LotControl = false,
                IsActive = true, PurchasePrice = 10m
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO", ICode = "FIFO-PC", IDesc = "FIFO Period Close Item",
                IClassCode = "RAW", StdUom = "EA", StockControl = true, LotControl = false,
                IsActive = true, PurchasePrice = 10m
            });
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Wac_close_blocks_replay_until_reopen_rejects_negative_historical_then_reclose_and_next_close_reconcile()
    {
        await using (var seedDb = await _factory.CreateDbContextAsync())
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(seedDb);

        var mr = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, TodayOct, enableBackdatedReplay: true);
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, TodayOct, enableBackdatedReplay: true);
        var closeSvc = CreatePeriodCloseService(TodayOct);

        // Aug 5: Receipt 10 @ 10 = 100; Aug 15: Issue 6 @ 10 = 60 -> Ending = 4 qty, 40 value
        var r1 = await PostMrAsync(mr, "WAC-PC", new DateTime(2026, 8, 5, 9, 0, 0), 10m, 10m);
        var i1 = await PostMiAsync(mi, "WAC-PC", new DateTime(2026, 8, 15, 9, 0, 0), 6m);

        var closeAug1 = await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugFrom, PeriodTo = AugTo });
        Assert.True(closeAug1.Succeeded, closeAug1.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var qtySnap1 = await db.StockPeriodSnapshotHdrs.Include(h => h.Lines)
                .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 1);
            var valSnap1 = await db.StockValuationPeriodSnapshotHdrs.Include(h => h.Lines)
                .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 1);
            Assert.Equal(4m, Assert.Single(qtySnap1.Lines, l => l.LedgerArea == "INVENTORY" && l.ItemCode == "WAC-PC").BaseQty);
            var vLine1 = Assert.Single(valSnap1.Lines, l => l.ItemCode == "WAC-PC");
            Assert.Equal(4m, vLine1.ClosingQty);
            Assert.Equal(40m, vLine1.ClosingValue);
        }

        // Closed August blocks both backdated MR creation and MI rollback atomically
        var closedMrDraft = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = new DateTime(2026, 8, 3, 9, 0, 0),
            Lines = [new() { ICode = "WAC-PC", ToWarehouse = "MAIN", ToLocation = "BIN1", Quantity = 10m, Uom = "EA", UnitPrice = 20m, PriceConfirmed = true, IClassCode = "RAW", IStatus = "ACTIVE", Reason = "REPLAY" }]
        });
        Assert.False(closedMrDraft.Succeeded);
        Assert.Contains("closed inventory period", closedMrDraft.ErrorMessage);

        var closedRb = await mi.RollbackAsync([i1]);
        Assert.False(closedRb.Succeeded);
        Assert.Contains("closed inventory period", closedRb.ErrorMessage);

        // Reopen August
        var list = await closeSvc.ListAsync();
        Assert.True(list.Succeeded);
        var augHdr = Assert.Single(list.ListPage!.Headers);
        var reopen = await closeSvc.ReopenAsync(new IvPeriodCloseRequest
        {
            Id = augHdr.Id,
            RowVersion = augHdr.RowVersion,
            ReopenReason = "Admit late August receipt"
        });
        Assert.True(reopen.Succeeded, reopen.ErrorMessage);

        // Negative historical stock attempt while reopened: live balance is +4, but Aug 3 had 0 -> MI -3 must fail atomically
        int wacBalId;
        await using (var balDb = await _factory.CreateDbContextAsync())
            wacBalId = await balDb.IvBalLocs.Where(b => b.ICode == "WAC-PC").Select(b => b.Id).SingleAsync();
        var negMiDraft = await mi.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = new DateTime(2026, 8, 3, 8, 0, 0),
            Lines = [new() { FromBalLocId = wacBalId, ICode = "WAC-PC", FrWarehouse = "MAIN", FrLocation = "BIN1", Quantity = 3m, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "HIST_NEG" }]
        });
        Assert.True(negMiDraft.Succeeded, negMiDraft.ErrorMessage);
        var negMiPost = await mi.PostAsync([negMiDraft.BatchNo]);
        Assert.False(negMiPost.Succeeded);
        await using (var checkDb = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(4m, await checkDb.IvBalLocs.Where(b => b.ICode == "WAC-PC").SumAsync(b => b.StdQty));
            var st = await checkDb.StockCostStates.SingleAsync(s => s.ItemCode == "WAC-PC" && s.CostMethod == StockCostMethods.MovingAverage);
            Assert.Equal(4m, st.OnHandBaseQty);
            Assert.Equal(40m, st.InventoryValue);
            // Delete unposted draft so reclose can proceed
            var bNeg = await checkDb.IvTrxBatches.SingleAsync(b => b.BatchNo == negMiDraft.BatchNo);
            checkDb.IvTrxBatchDetails.RemoveRange(checkDb.IvTrxBatchDetails.Where(d => d.BatchId == bNeg.Id));
            checkDb.IvTrxBatches.Remove(bNeg);
            await checkDb.SaveChangesAsync();
        }

        // Valid backdated MR on Aug 3 (+10 @ 20 = 200): before Aug 5 (+10 @ 10 = 100) and Aug 15 (-6)
        // New WAC before Aug 15 = 300 / 20 = 15.00 -> Aug 15 MI repriced to -90 -> Ending = 14 qty, 210 value
        await PostMrAsync(mr, "WAC-PC", new DateTime(2026, 8, 3, 9, 0, 0), 10m, 20m);

        // Reclose August -> must append Revision = 2 physical & valuation snapshots with updated totals
        var recloseAug = await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugFrom, PeriodTo = AugTo });
        Assert.True(recloseAug.Succeeded, recloseAug.ErrorMessage);

        // Close contiguous September (no new movements; verifies carry-forward from reclosed August)
        var closeSep = await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = SepFrom, PeriodTo = SepTo });
        Assert.True(closeSep.Succeeded, closeSep.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var st = await db.StockCostStates.SingleAsync(s => s.ItemCode == "WAC-PC" && s.CostMethod == StockCostMethods.MovingAverage);
            Assert.Equal(14m, st.OnHandBaseQty);
            Assert.Equal(210m, st.InventoryValue);

            var qtySnap2 = await db.StockPeriodSnapshotHdrs.Include(h => h.Lines)
                .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 2);
            Assert.Equal(14m, Assert.Single(qtySnap2.Lines, l => l.LedgerArea == "INVENTORY" && l.ItemCode == "WAC-PC").BaseQty);

            var valSnap2 = await db.StockValuationPeriodSnapshotHdrs.Include(h => h.Lines)
                .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 2);
            var augLine2 = Assert.Single(valSnap2.Lines, l => l.ItemCode == "WAC-PC");
            Assert.Equal(14m, augLine2.ClosingQty);
            Assert.Equal(210m, augLine2.ClosingValue);

            var sepCloseLine = await db.IvPeriodCloseBals
                .Include(b => b.Header)
                .SingleAsync(b => b.Header.PeriodFrom == SepFrom && b.ICode == "WAC-PC");
            Assert.Equal(14m, sepCloseLine.OpeningQty);
            Assert.Equal(0m, sepCloseLine.OpeningAdjustQty);
            Assert.Equal(14m, sepCloseLine.ClosingQty);
            Assert.True(sepCloseLine.CarryForwardOk);

            var sepValSnap = await db.StockValuationPeriodSnapshotHdrs.Include(h => h.Lines)
                .SingleAsync(h => h.PeriodKey == "2026-09" && h.Revision == 1);
            var sepLine = Assert.Single(sepValSnap.Lines, l => l.ItemCode == "WAC-PC");
            Assert.Equal(14m, sepLine.OpeningQty);
            Assert.Equal(210m, sepLine.OpeningValue);
            Assert.Equal(14m, sepLine.ClosingQty);
            Assert.Equal(210m, sepLine.ClosingValue);
        }
    }

    [Fact]
    public async Task Wac_closed_august_allows_open_september_backdated_replay_without_mutating_august()
    {
        await using (var seedDb = await _factory.CreateDbContextAsync())
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(seedDb);

        var mr = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, TodayOct, enableBackdatedReplay: true);
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, TodayOct, enableBackdatedReplay: true);
        var closeSvc = CreatePeriodCloseService(TodayOct);

        // Aug 10: +10 @ 10; Aug 20: -6 -> Aug closing = 4 qty, 40 value
        await PostMrAsync(mr, "WAC-PC", new DateTime(2026, 8, 10, 9, 0, 0), 10m, 10m);
        await PostMiAsync(mi, "WAC-PC", new DateTime(2026, 8, 20, 9, 0, 0), 6m);
        Assert.True((await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugFrom, PeriodTo = AugTo })).Succeeded);

        // Open September: Sep 5 +6 @ 20 (total 10 qty, 160 val), Sep 12 -5 (cost 80 -> 5 qty, 80 val),
        // then backdated Sep 3 +10 @ 25 -> total before Sep 12 = 40+250+120 = 410 / 20 = 20.50 -> Sep 12 -102.50 -> ending 15 qty, 307.50 value
        await PostMrAsync(mr, "WAC-PC", new DateTime(2026, 9, 5, 9, 0, 0), 6m, 20m);
        await PostMiAsync(mi, "WAC-PC", new DateTime(2026, 9, 12, 9, 0, 0), 5m);
        await PostMrAsync(mr, "WAC-PC", new DateTime(2026, 9, 3, 9, 0, 0), 10m, 25m);

        var closeSep = await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = SepFrom, PeriodTo = SepTo });
        Assert.True(closeSep.Succeeded, closeSep.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var augSnap = await db.StockValuationPeriodSnapshotHdrs.Include(h => h.Lines)
            .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 1);
        var augLine = Assert.Single(augSnap.Lines, l => l.ItemCode == "WAC-PC");
        Assert.Equal(4m, augLine.ClosingQty);
        Assert.Equal(40m, augLine.ClosingValue);

        var sepSnap = await db.StockValuationPeriodSnapshotHdrs.Include(h => h.Lines)
            .SingleAsync(h => h.PeriodKey == "2026-09" && h.Revision == 1);
        var sepLine = Assert.Single(sepSnap.Lines, l => l.ItemCode == "WAC-PC");
        Assert.Equal(4m, sepLine.OpeningQty);
        Assert.Equal(40m, sepLine.OpeningValue);
        Assert.Equal(15m, sepLine.ClosingQty);
        Assert.Equal(307.50m, sepLine.ClosingValue);

        var st = await db.StockCostStates.SingleAsync(s => s.ItemCode == "WAC-PC" && s.CostMethod == StockCostMethods.MovingAverage);
        Assert.Equal(15m, st.OnHandBaseQty);
        Assert.Equal(307.50m, st.InventoryValue);
    }

    [Fact]
    public async Task Fifo_close_reopen_backdated_replay_and_reclose_reconciles_layers_and_snapshots()
    {
        await using (var seedDb = await _factory.CreateDbContextAsync())
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(seedDb);

        var mr = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, TodayOct, enableBackdatedReplay: true);
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, TodayOct, enableBackdatedReplay: true);
        var closeSvc = CreatePeriodCloseService(TodayOct);

        // Aug 5: MR 10 @ 10 = 100; Aug 15: MI 4 -> consumes 4 @ 10 = 40; remaining 6 qty, 60 value
        await PostMrAsync(mr, "FIFO-PC", new DateTime(2026, 8, 5, 9, 0, 0), 10m, 10m);
        await PostMiAsync(mi, "FIFO-PC", new DateTime(2026, 8, 15, 9, 0, 0), 4m);
        var closeAug1 = await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugFrom, PeriodTo = AugTo });
        Assert.True(closeAug1.Succeeded, closeAug1.ErrorMessage);

        var list = await closeSvc.ListAsync();
        var augHdr = Assert.Single(list.ListPage!.Headers);
        Assert.True((await closeSvc.ReopenAsync(new IvPeriodCloseRequest { Id = augHdr.Id, RowVersion = augHdr.RowVersion, ReopenReason = "Late Aug FIFO receipt" })).Succeeded);

        // Backdated Aug 3 MR +5 @ 20 (= 100): Aug 15 MI 4 now consumes from the $20 layer (= 80), leaving 1 @ 20 + 10 @ 10 = 11 qty, 120 value
        await PostMrAsync(mr, "FIFO-PC", new DateTime(2026, 8, 3, 9, 0, 0), 5m, 20m);

        var recloseAug = await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugFrom, PeriodTo = AugTo });
        Assert.True(recloseAug.Succeeded, recloseAug.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var st = await db.StockCostStates.SingleAsync(s => s.ItemCode == "FIFO-PC" && s.CostMethod == StockCostMethods.Fifo);
        Assert.Equal(11m, st.OnHandBaseQty);
        Assert.Equal(120m, st.InventoryValue);

        var openLayers = await db.StockFifoLayers.Where(l => l.ItemCode == "FIFO-PC" && l.Status == StockFifoLayerStatuses.Open).ToListAsync();
        Assert.Equal(11m, openLayers.Sum(l => l.RemainingQty));
        Assert.Equal(120m, openLayers.Sum(l => l.RemainingValue));

        var valSnap2 = await db.StockValuationPeriodSnapshotHdrs.Include(h => h.Lines)
            .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 2);
        var line2 = Assert.Single(valSnap2.Lines, l => l.ItemCode == "FIFO-PC");
        Assert.Equal(11m, line2.ClosingQty);
        Assert.Equal(120m, line2.ClosingValue);
    }

    [Fact]
    public async Task Cross_module_inventory_production_wip_and_period_close_reconcile()
    {
        await using (var seedDb = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(seedDb);
            var bom = new ErpWeb.Model.Entities.Planning.PrBomHdr
            {
                CompanyCode = "DEMO",
                ProdCode = "FG-PC",
                DefinitionCode = "STD",
                Status = ErpWeb.Model.Entities.Planning.PrBomStatuses.Active,
                Version = 1,
                IsDefaultDefinition = true
            };
            seedDb.PrBomHdrs.Add(bom);
            await seedDb.SaveChangesAsync();

            var wo = new ProductionWorkOrder
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WorkOrderNo = "WO-PC-001",
                SourceBomHdrId = bom.Uid,
                ProductCode = "FG-PC",
                ProductDescription = "Period Close FG",
                OutputUom = "EA",
                Status = ProductionWorkOrderStatuses.InProgress,
                PlannedQty = 10m,
                RemainingQty = 10m,
                SnapshotRevision = 1,
                SnapshotHash = new string('A', 64),
                SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                DefinitionEffectiveDate = AugFrom,
                PlannedStartDateTime = AugFrom,
                PlannedCompletionDateTime = AugTo
            };
            seedDb.ProductionWorkOrders.Add(wo);
            await seedDb.SaveChangesAsync();

            var loc = new ProductionLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                Code = "LOC-PC",
                Description = "Period Close Production Location",
                WorkCentreCode = "WC-PC",
                IsActive = true
            };
            seedDb.ProductionLocations.Add(loc);
            await seedDb.SaveChangesAsync();

            seedDb.ProductionBalLots.Add(new ProductionBalLot
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                Kind = ProductionBalLotKinds.MaterialIn,
                WorkOrderId = wo.Uid,
                WorkOrderNo = wo.WorkOrderNo,
                ItemCode = "WAC-PC",
                Description = "RM WIP",
                Qty = 5m,
                Uom = "EA",
                BaseQty = 5m,
                BaseUom = "EA",
                TotalCost = 50m,
                AverageUnitCost = 10m,
                WarehouseCode = "MAIN",
                LocationCode = "BIN1",
                LotNo = string.Empty,
                BalanceStage = "MATERIAL_IN",
                ProductionLocationId = loc.Id,
                StockStatusCode = "ACTIVE",
                OriginType = "ISSUED",
                LastMovementDate = AugTo,
                LastStockEventEffectiveAt = AugTo
            });
            await seedDb.SaveChangesAsync();
        }

        var mr = InventoryLedgerTestFixture.CreateMiscReceipt(_factory, TodayOct, enableBackdatedReplay: true);
        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, TodayOct, enableBackdatedReplay: true);
        var closeSvc = CreatePeriodCloseService(TodayOct);

        await PostMrAsync(mr, "WAC-PC", new DateTime(2026, 8, 5, 9, 0, 0), 20m, 10m);
        await PostMiAsync(mi, "WAC-PC", new DateTime(2026, 8, 12, 9, 0, 0), 5m);

        var close = await closeSvc.CloseAsync(new IvPeriodCloseRequest { PeriodFrom = AugFrom, PeriodTo = AugTo });
        Assert.True(close.Succeeded, close.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var qtySnap = await db.StockPeriodSnapshotHdrs.Include(h => h.Lines)
            .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 1);
        Assert.Equal(15m, Assert.Single(qtySnap.Lines, l => l.LedgerArea == "INVENTORY" && l.ItemCode == "WAC-PC").BaseQty);
        Assert.Equal(5m, Assert.Single(qtySnap.Lines, l => l.LedgerArea == "PRODUCTION" && l.ItemCode == "WAC-PC").BaseQty);

        var valSnap = await db.StockValuationPeriodSnapshotHdrs.Include(h => h.Lines)
            .SingleAsync(h => h.PeriodKey == "2026-08" && h.Revision == 1);
        var vLine = Assert.Single(valSnap.Lines, l => l.ItemCode == "WAC-PC");
        Assert.Equal(15m, vLine.ClosingQty);
        Assert.Equal(150m, vLine.ClosingValue);
    }

    private IvPeriodCloseService CreatePeriodCloseService(DateTime today)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var reconciliation = new IvInventoryReconciliationService(_factory, tenant, access.Object);
        return new IvPeriodCloseService(
            _factory, tenant, access.Object,
            new FixedCurrentDateService(today), reconciliation,
            NullLogger<IvPeriodCloseService>.Instance);
    }

    private static async Task<int> PostMrAsync(IvMiscReceiptService mr, string item, DateTime date, decimal qty, decimal price)
    {
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = date,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = item, ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = qty, Uom = "EA", UnitPrice = price, PriceConfirmed = true,
                    IClassCode = "RAW", IStatus = "ACTIVE", Reason = "PC_TEST"
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await mr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
        return save.BatchNo;
    }

    private async Task<int> PostMiAsync(IvMiscIssueService mi, string item, DateTime date, decimal qty)
    {
        int balId;
        await using (var db = await _factory.CreateDbContextAsync())
            balId = await db.IvBalLocs.Where(b => b.ICode == item).Select(b => b.Id).SingleAsync();
        var save = await mi.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = date,
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    FromBalLocId = balId, ICode = item, FrWarehouse = "MAIN", FrLocation = "BIN1",
                    Quantity = qty, Uom = "EA", IClassCode = "RAW", IStatus = "ACTIVE", Reason = "PC_TEST"
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
        return save.BatchNo;
    }
}
