using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ErpWeb.Tests.Infrastructure.Helpers;
using ErpWeb.Tests.Procurement.Transaction;

namespace ErpWeb.Tests.Inventory.Transaction;
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryGoodsReceipt)]
public class IvGoodsReceiptServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly FakePoOrderDocumentNumberingService _poNumbering = new();

    public IvGoodsReceiptServiceTests()
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
        db.Companies.Add(new Company
        {
            CompanyCode = "DEMO", CompanyName = "Demo Company", CurrencyCode = "MYR", IsActive = true
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "SR",
            Percentage = 6m,
            TaxGlCode = "GLTAX"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock A",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 25m,
            PurchaseTaxGroup = "SR",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            DefWarehouse = "MAIN"
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = "SUP01",
            SuppName = "Alpha Supplier",
            Currency = "MYR",
            GlCode = "AP001",
            IsActive = true,
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        db.PoVendorByItems.Add(new PoVendorByItem
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Vendor = "SUP01",
            ICode = "A100",
            PurUom = "EA",
            UnitPrice = 25m,
            Tolerance = 5m,
            OrdLevel = 0m,
            Status = "A",
            RowVersion = Guid.NewGuid().ToByteArray()
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveNew_draft_does_not_change_PoOrderDetail_RecvQty()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(0m, detail.RecvQty);
        Assert.Equal(10m, detail.BalanceQty);
        Assert.Equal(PoOrderStatuses.New, (await db.PoOrders.SingleAsync(x => x.PoNo == po.PoNo)).Status);
        var receiptLine = await db.IvTrxBatchDetails.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Equal("MYR", receiptLine.Currency);
        Assert.Equal(25m, receiptLine.CostPrice);
        Assert.Equal(25m, receiptLine.BaseUnitPrices);
        Assert.StartsWith("PO_PROVISIONAL|", receiptLine.PriceEvidence);
    }

    [Fact]
    public async Task SaveNew_future_dated_gr_and_edit_keep_po_and_stock_unposted()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var request = GrRequest(po, recvQty: 4m);
        request.TrxDate = FixedToday.AddDays(1);

        var saved = await sut.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        request.TrxDate = FixedToday.AddDays(2);
        var updated = await sut.UpdateAsync(saved.BatchNo, request);
        Assert.True(updated.Succeeded, updated.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Equal(FixedToday.AddDays(2), batch.TrxDtTime);
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(0m, detail.RecvQty);
        Assert.Equal(10m, detail.BalanceQty);
        Assert.Empty(await db.IvBalLocs.ToListAsync());
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
        Assert.Empty(await db.StockPostings.ToListAsync());
        Assert.Empty(await db.StockValuationFacts.ToListAsync());
        Assert.Empty(await db.StockFifoLayers.ToListAsync());
    }

    [Fact]
    public async Task Post_future_dated_gr_is_blocked_and_po_quantity_stays_unposted()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut(postingToday: FixedToday);
        var request = GrRequest(po, recvQty: 4m);
        request.TrxDate = FixedToday.AddDays(1);
        var saved = await sut.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        request.TrxDate = FixedToday.AddDays(2);
        var updated = await sut.UpdateAsync(saved.BatchNo, request);
        Assert.True(updated.Succeeded, updated.ErrorMessage);

        var post = await sut.PostAsync([saved.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("cannot be posted until", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Null(batch.PostedDate);
        Assert.Equal(0, await db.PoOrderDetails.Where(x => x.PoNo == po.PoNo).Select(x => x.RecvQty).SingleAsync());
        Assert.Empty(await db.IvBalLocs.ToListAsync());
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
        Assert.Empty(await db.StockPostings.ToListAsync());
        Assert.Empty(await db.StockValuationFacts.ToListAsync());
        Assert.Empty(await db.StockFifoLayers.ToListAsync());

        var postOnDate = await CreateGrSut(postingToday: FixedToday.AddDays(2)).PostAsync([saved.BatchNo]);
        Assert.True(postOnDate.Succeeded, postOnDate.ErrorMessage);
        await using var postedDb = await _factory.CreateDbContextAsync();
        Assert.Equal(4m, await postedDb.PoOrderDetails.Where(x => x.PoNo == po.PoNo).Select(x => x.RecvQty).SingleAsync());
        Assert.Equal(IvBatchStatuses.Posted,
            (await postedDb.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo)).BatchStatus);
        Assert.Single(await postedDb.IvTrxHistories.ToListAsync());
    }

    [Fact]
    public async Task Save_and_post_future_dated_ng_keeps_po_unposted_until_effective_date()
    {
        var po = await CreatePoAsync(qty: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var order = await db.PoOrders.SingleAsync(x => x.PoNo == po.PoNo);
            var line = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
            order.OneTime = true;
            line.OneTime = true;
            await db.SaveChangesAsync();
        }

        var sut = CreateGrSut(postingToday: FixedToday);
        var request = GrRequest(po, recvQty: 4m);
        request.TrxType = IvTrxTypes.NonStockGoodsReceive;
        request.TrxDate = FixedToday.AddDays(1);
        var saved = await sut.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        request.TrxDate = FixedToday.AddDays(2);
        var updated = await sut.UpdateAsync(saved.BatchNo, request);
        Assert.True(updated.Succeeded, updated.ErrorMessage);

        var post = await sut.PostAsync([saved.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("cannot be posted until", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        var batch = await verify.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Equal(FixedToday.AddDays(2), batch.TrxDtTime);
        Assert.Null(batch.PostedDate);
        Assert.Equal(0, await verify.PoOrderDetails.Where(x => x.PoNo == po.PoNo).Select(x => x.RecvQty).SingleAsync());
        Assert.Empty(await verify.IvTrxHistories.ToListAsync());
        Assert.Empty(await verify.StockPostings.ToListAsync());
        Assert.Empty(await verify.StockValuationFacts.ToListAsync());
        Assert.Empty(await verify.StockFifoLayers.ToListAsync());
    }

    [Fact]
    public async Task Post_updates_RecvQty_and_status()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await sut.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
        Assert.Equal(1, post.SucceededCount);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(4m, detail.RecvQty);
        Assert.Equal(6m, detail.BalanceQty);
        Assert.Equal(PoOrderStatuses.Received, (await db.PoOrders.SingleAsync(x => x.PoNo == po.PoNo)).Status);
        Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo)).BatchStatus);
    }

    [Fact]
    public async Task Double_post_forbidden()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 2m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded);

        var second = await sut.PostAsync([save.BatchNo]);
        Assert.False(second.Succeeded);
        Assert.Equal(0, second.SucceededCount);
        Assert.Equal(1, second.FailedCount);
    }

    [Fact]
    public async Task Rollback_returns_batch_to_NEW_and_reverses_RecvQty()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded);

        var rollback = await sut.RollbackAsync([save.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(0m, detail.RecvQty);
        Assert.Equal(10m, detail.BalanceQty);
        Assert.Equal(PoOrderStatuses.New, (await db.PoOrders.SingleAsync(x => x.PoNo == po.PoNo)).Status);
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo)).BatchStatus);
    }

    [Fact]
    public async Task Rollback_rejects_changed_purchase_quantity_before_stock_or_po_write()
    {
        await EnsureActiveStockLedgerAsync();
        var po = await CreatePoAsync(qty: 10m, unitPrice: 3.50m);
        var sut = CreateGrSut(withLedger: true);
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded);

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var line = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == save.BatchNo);
            line.ToPurQty = 1m;
            await tamper.SaveChangesAsync();
        }
        var rejected = await sut.RollbackAsync([save.BatchNo]);
        Assert.False(rejected.Succeeded);
        Assert.Contains("changed before rollback",
            rejected.ErrorMessage ?? string.Empty);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(3m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(3m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b =>
                    b.BatchNo == save.BatchNo)).BatchStatus);
            var line = await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == save.BatchNo);
            line.ToPurQty = 3m;
            await unchanged.SaveChangesAsync();
        }
        var rollback = await sut.RollbackAsync([save.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(0m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Fact]
    public async Task Backdated_wac_gr_reprices_later_issue_and_restores_po_on_rollback()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var order = await db.PoOrders.SingleAsync(p => p.PoNo == po.PoNo);
            order.PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var request = GrRequest(po, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var saved = await receipt.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var line = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            line.BaseUnitPrices = 99m;
            await tamper.SaveChangesAsync();
        }
        var rejectedCost = await receipt.PostAsync([saved.BatchNo]);
        Assert.False(rejectedCost.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(11m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            var line = await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            line.BaseUnitPrices = 10m;
            await unchanged.SaveChangesAsync();
        }
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "A100");
            Assert.Equal(16m, state.OnHandBaseQty);
            Assert.Equal(76m, state.InventoryValue);
            Assert.Equal(16m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            var laterHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [laterHistory]);
            Assert.Equal(19m, active[laterHistory.Id].Sum(f => f.CostAmount));
        }
        await using (var invoiced = await _factory.CreateDbContextAsync())
        {
            var line = await invoiced.PoOrderDetails.SingleAsync();
            line.InvoicedQty = 1m;
            await invoiced.SaveChangesAsync();
        }
        var rejectedRollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.False(rejectedRollback.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(16m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            var line = await unchanged.PoOrderDetails.SingleAsync();
            line.InvoicedQty = 0m;
            await unchanged.SaveChangesAsync();
        }
        var extraIssue = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 12m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(extraIssue.Succeeded, extraIssue.ErrorMessage);
        Assert.True((await issue.PostAsync([extraIssue.BatchNo])).Succeeded);
        var rejectedShortage = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.False(rejectedShortage.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(4m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        }
        Assert.True((await issue.RollbackAsync([extraIssue.BatchNo])).Succeeded);
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(33m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        var laterRollback = await issue.RollbackAsync([later.BatchNo]);
        Assert.True(laterRollback.Succeeded, laterRollback.ErrorMessage);
        await using var afterIssueRollback = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await afterIssueRollback.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(45m,
            (await afterIssueRollback.StockCostStates.SingleAsync()).InventoryValue);
        var postAfterIssueRollback = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(postAfterIssueRollback.Succeeded,
            postAfterIssueRollback.ErrorMessage);
        var rollbackAfterIssueRollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollbackAfterIssueRollback.Succeeded,
            rollbackAfterIssueRollback.ErrorMessage);
        await using var final = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await final.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(45m, (await final.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await final.PoOrderDetails.SingleAsync()).RecvQty);
    }

    private async Task<int> ReadPileIdAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.IvBalLocs.Select(b => b.Id).SingleAsync();
    }

    [Fact]
    public async Task Backdated_wac_gr_preserves_existing_lot_through_post_and_rollback()
    {
        await EnsureActiveStockLedgerAsync();
        int pileId;
        int lotId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var master = await db.IvStockMasters.SingleAsync(m => m.ICode == "A100");
            master.LotControl = true;
            master.ExpiryControl = IvExpiryControlModes.None;
            var lot = new IvLot
            {
                CompanyCode = "DEMO", ICode = "A100", LotNo = "LOT-1",
                ReceiptDate = FixedToday.AddDays(-10), IsActive = true
            };
            db.IvLots.Add(lot);
            await db.SaveChangesAsync();
            lotId = lot.Id;
            var pile = new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotId = lotId,
                LotNo = lot.LotNo, IStatus = "ACTIVE", StdQty = 15m,
                StdUom = "EA", TransDate = FixedToday.AddDays(-5)
            };
            db.IvBalLocs.Add(pile);
            await db.SaveChangesAsync();
            pileId = pile.Id;
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var order = await db.PoOrders.SingleAsync(p => p.PoNo == po.PoNo);
            order.PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = "LOT-1", Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday.AddDays(-2),
            Lines = [new IvGoodsReceiptLineRequest
            {
                PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                PoLineNo = po.PoLineNo, ToWarehouse = "MAIN",
                ToLocation = "BIN1", ToLotNo = "LOT-1", ToRecvQty = 5m
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(16m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(76m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            var receiptHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, receiptHistory.ToLotId);
            var receiptFact = await db.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == receiptHistory.Id && f.Direction == 1);
            Assert.Equal(lotId, receiptFact.LotId);
            var laterHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
            var activeIssue = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, "DEMO", "HQ", [laterHistory]);
            Assert.Equal(19m, activeIssue[laterHistory.Id].Sum(f => f.CostAmount));
            Assert.All(activeIssue[laterHistory.Id], f => Assert.Equal(lotId, f.LotId));
        }
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(lotId, (await restored.IvBalLocs.SingleAsync()).LotId);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        Assert.Equal(33m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
        var restoredIssue = await restored.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
        var restoredIssueFacts = await StockReplayValuationGenerationResolver
            .ResolveAsync(restored, "DEMO", "HQ", [restoredIssue]);
        Assert.Equal(12m, restoredIssueFacts[restoredIssue.Id]
            .Sum(f => f.CostAmount));
    }

    [Theory]
    [InlineData(StockCostMethods.MovingAverage, false, 65, 15, 15, 0, 0)]
    [InlineData(StockCostMethods.MovingAverage, true, 56.875, 10, 15, 8.125, 5)]
    [InlineData(StockCostMethods.Fifo, false, 65, 15, 15, 0, 0)]
    [InlineData(StockCostMethods.Fifo, true, 55, 10, 15, 10, 5)]
    [InlineData(StockCostMethods.Standard, false, 52, 19.5, 19.5, 0, 0)]
    [InlineData(StockCostMethods.Standard, true, 45.5, 13, 19.5, 6.5, 6.5)]
    public async Task Backdated_gr_posts_before_the_receipt_that_created_its_lot(
        string costMethod, bool laterIssue, decimal postedValue,
        decimal restoredValue, decimal laterReceiptValue,
        decimal postedIssueValue, decimal restoredIssueValue)
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var master = await db.IvStockMasters.SingleAsync(m => m.ICode == "A100");
            master.LotControl = true;
            master.ExpiryControl = IvExpiryControlModes.None;
            if (costMethod == StockCostMethods.Fifo)
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            else if (costMethod == StockCostMethods.Standard)
            {
                await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                    "A100", 6.5m);
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 0m, 6.5m, StockCostMethods.Standard);
            }
            else
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 0m, 0m);
        }
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN",
                ToLocation = "BIN1", ToLotNo = "LOT-1",
                Quantity = 3m, Uom = "EA", IClassCode = "RAW",
                IStatus = "ACTIVE", UnitPrice = 5m,
                PriceConfirmed = true,
                CostEvidenceType = InventoryCostEvidenceTypes.ManualApproved,
                CostOverrideReason = "Approved receipt cost",
                ExpiryDate = FixedToday.AddDays(30),
                Reason = "FOUND"
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        var laterPost = await receipt.PostAsync([later.BatchNo]);
        Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);
        int lotId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(3m, (await db.IvBalLocs.SingleAsync()).StdQty);
            lotId = (await db.IvLots.SingleAsync()).Id;
        }
        int? issueBatchNo = null;
        if (laterIssue)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var pileId = (await db.IvBalLocs.SingleAsync()).Id;
            var issue = InventoryLedgerTestFixture.CreateMiscIssue(
                _factory, FixedToday, enableBackdatedReplay: true);
            var issued = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = FixedToday,
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = pileId, ICode = "A100",
                    FrWarehouse = "MAIN", FrLocation = "BIN1",
                    FrLotNo = "LOT-1", Quantity = 1m, Uom = "EA",
                    IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 5m
                }]
            });
            Assert.True(issued.Succeeded, issued.ErrorMessage);
            Assert.True((await issue.PostAsync([issued.BatchNo])).Succeeded);
            issueBatchNo = issued.BatchNo;
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var goodsReceipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var saved = await goodsReceipt.SaveNewAsync(
            new IvGoodsReceiptSaveRequest
            {
                TrxType = IvTrxTypes.GoodsReceive,
                TrxDate = FixedToday.AddDays(-2),
                Lines = [new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo, ToWarehouse = "MAIN",
                    ToLocation = "BIN1", ToLotNo = "LOT-1",
                    ExpiryDate = FixedToday.AddDays(30),
                    ToRecvQty = 5m
                }]
            });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await goodsReceipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(laterIssue ? 7m : 8m,
                (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(lotId, (await db.IvBalLocs.SingleAsync()).LotId);
            Assert.Equal(postedValue,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            var history = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, history.ToLotId);
            var laterHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, laterHistory.ToLotId);
            var activeLater = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, "DEMO", "HQ", [laterHistory]);
            Assert.Equal(laterReceiptValue,
                activeLater[laterHistory.Id].Sum(f => f.CostAmount));
            Assert.All(activeLater[laterHistory.Id], f =>
                Assert.Equal(lotId, f.LotId));
            if (issueBatchNo is int postedIssueBatch)
            {
                var issueHistory = await db.IvTrxHistories.SingleAsync(h =>
                    h.BatchNo == postedIssueBatch && h.EntryRole == "ORIGINAL");
                var activeIssue = await StockReplayValuationGenerationResolver
                    .ResolveAsync(db, "DEMO", "HQ", [issueHistory]);
                Assert.Equal(postedIssueValue,
                    activeIssue[issueHistory.Id].Sum(f => f.CostAmount));
                Assert.All(activeIssue[issueHistory.Id], f =>
                    Assert.Equal(lotId, f.LotId));
            }
        }
        var rollback = await goodsReceipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(laterIssue ? 2m : 3m,
            (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(lotId, (await restored.IvBalLocs.SingleAsync()).LotId);
        Assert.Equal(restoredValue,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        var restoredLater = await restored.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
        Assert.Equal(lotId, restoredLater.ToLotId);
        var restoredFacts = await StockReplayValuationGenerationResolver
            .ResolveAsync(restored, "DEMO", "HQ", [restoredLater]);
        Assert.Equal(laterReceiptValue,
            restoredFacts[restoredLater.Id].Sum(f => f.CostAmount));
        Assert.All(restoredFacts[restoredLater.Id], f =>
            Assert.Equal(lotId, f.LotId));
        if (issueBatchNo is int restoredIssueBatch)
        {
            var issueHistory = await restored.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == restoredIssueBatch && h.EntryRole == "ORIGINAL");
            var activeIssue = await StockReplayValuationGenerationResolver
                .ResolveAsync(restored, "DEMO", "HQ", [issueHistory]);
            Assert.Equal(restoredIssueValue,
                activeIssue[issueHistory.Id].Sum(f => f.CostAmount));
            Assert.All(activeIssue[issueHistory.Id], f =>
                Assert.Equal(lotId, f.LotId));
        }
    }

    [Theory]
    [InlineData(StockCostMethods.MovingAverage)]
    [InlineData(StockCostMethods.Fifo)]
    [InlineData(StockCostMethods.Standard)]
    public async Task Backdated_gr_rollback_refuses_later_same_lot_issue_that_needs_its_stock(
        string costMethod)
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var master = await db.IvStockMasters.SingleAsync(m => m.ICode == "A100");
            master.LotControl = true;
            master.ExpiryControl = IvExpiryControlModes.None;
            if (costMethod == StockCostMethods.Fifo)
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            else if (costMethod == StockCostMethods.Standard)
            {
                await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                    "A100", 6.5m);
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 0m, 6.5m, StockCostMethods.Standard);
            }
            else
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 0m, 0m);
        }
        var receipt = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await receipt.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "A100", ToWarehouse = "MAIN",
                ToLocation = "BIN1", ToLotNo = "LOT-1",
                Quantity = 3m, Uom = "EA", IClassCode = "RAW",
                IStatus = "ACTIVE", UnitPrice = 5m,
                PriceConfirmed = true,
                CostEvidenceType = InventoryCostEvidenceTypes.ManualApproved,
                CostOverrideReason = "Approved receipt cost",
                ExpiryDate = FixedToday.AddDays(30),
                Reason = "FOUND"
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await receipt.PostAsync([later.BatchNo])).Succeeded);
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var goodsReceipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var request = GrRequest(po, recvQty: 5m, lotNo: "LOT-1");
        request.TrxDate = FixedToday.AddDays(-2);
        var earlier = await goodsReceipt.SaveNewAsync(request);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        Assert.True((await goodsReceipt.PostAsync([earlier.BatchNo])).Succeeded);

        int pileId;
        await using (var db = await _factory.CreateDbContextAsync())
            pileId = (await db.IvBalLocs.SingleAsync()).Id;
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var issued = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = "LOT-1", Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 5m
            }]
        });
        Assert.True(issued.Succeeded, issued.ErrorMessage);
        Assert.True((await issue.PostAsync([issued.BatchNo])).Succeeded);

        var rejected = await goodsReceipt.RollbackAsync([earlier.BatchNo]);
        Assert.False(rejected.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(4m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b =>
                    b.BatchNo == earlier.BatchNo)).BatchStatus);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b =>
                    b.BatchNo == issued.BatchNo)).BatchStatus);
        }

        Assert.True((await issue.RollbackAsync([issued.BatchNo])).Succeeded);
        if (costMethod == StockCostMethods.Fifo)
        {
            decimal exactCost;
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var reversalHistory = await db.IvTrxHistories.SingleAsync(h =>
                    h.BatchNo == issued.BatchNo && h.EntryRole == "REVERSAL");
                var reversalFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f =>
                    f.InventoryHistoryId == reversalHistory.Id
                    && f.ReversesValuationFactId != null);
                exactCost = reversalFact.CostAmount;
                await db.StockValuationFacts.Where(f => f.Id == reversalFact.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.CostAmount,
                        exactCost + 1m));
            }
            var corruptPair = await goodsReceipt.RollbackAsync([earlier.BatchNo]);
            Assert.False(corruptPair.Succeeded);
            Assert.Contains("FIFO",
                corruptPair.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                var reversalHistory = await db.IvTrxHistories.SingleAsync(h =>
                    h.BatchNo == issued.BatchNo && h.EntryRole == "REVERSAL");
                var reversalFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(f =>
                    f.InventoryHistoryId == reversalHistory.Id
                    && f.ReversesValuationFactId != null);
                await db.StockValuationFacts.Where(f => f.Id == reversalFact.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.CostAmount,
                        exactCost));
            }
        }
        var restored = await goodsReceipt.RollbackAsync([earlier.BatchNo]);
        Assert.True(restored.Succeeded, restored.ErrorMessage);
        await using var final = await _factory.CreateDbContextAsync();
        Assert.Equal(3m, (await final.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(0m, (await final.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Theory]
    [InlineData(StockCostMethods.Fifo, 60, 28, 45, 27)]
    [InlineData(StockCostMethods.Standard, 78, 45.5, 52, 52)]
    public async Task Backdated_gr_preserves_existing_lot_with_fifo_or_standard(
        string costMethod, decimal postedValue, decimal restoredValue,
        decimal postedIssueValue, decimal restoredIssueValue)
    {
        await EnsureActiveStockLedgerAsync();
        int pileId;
        int lotId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var master = await db.IvStockMasters.SingleAsync(m => m.ICode == "A100");
            master.LotControl = true;
            master.ExpiryControl = IvExpiryControlModes.None;
            if (costMethod == StockCostMethods.Fifo)
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            else
            {
                await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                    "A100", 6.5m);
            }
            var lot = new IvLot
            {
                CompanyCode = "DEMO", ICode = "A100", LotNo = "LOT-1",
                ReceiptDate = FixedToday.AddDays(-10), IsActive = true
            };
            db.IvLots.Add(lot);
            await db.SaveChangesAsync();
            lotId = lot.Id;
            var pile = new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotId = lotId,
                LotNo = lot.LotNo, IStatus = "ACTIVE", StdQty = 15m,
                StdUom = "EA", TransDate = FixedToday.AddDays(-5)
            };
            db.IvBalLocs.Add(pile);
            await db.SaveChangesAsync();
            pileId = pile.Id;
            if (costMethod == StockCostMethods.Fifo)
                await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [
                    new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                    new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-1))
                ]);
            else
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 15m, 6.5m, StockCostMethods.Standard);
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = "LOT-1", Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var saved = await receipt.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday.AddDays(-2),
            Lines = [new IvGoodsReceiptLineRequest
            {
                PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                PoLineNo = po.PoLineNo, ToWarehouse = "MAIN",
                ToLocation = "BIN1", ToLotNo = "LOT-1", ToRecvQty = 5m
            }]
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(12m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(postedValue,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            var receiptHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(lotId, receiptHistory.ToLotId);
            var receiptFact = await db.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == receiptHistory.Id && f.Direction == 1);
            Assert.Equal(lotId, receiptFact.LotId);
            var laterHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
            var activeIssue = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, "DEMO", "HQ", [laterHistory]);
            Assert.Equal(postedIssueValue,
                activeIssue[laterHistory.Id].Sum(f => f.CostAmount));
        }
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(lotId, (await restored.IvBalLocs.SingleAsync()).LotId);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        Assert.Equal(restoredValue,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        var restoredIssue = await restored.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
        var restoredFacts = await StockReplayValuationGenerationResolver
            .ResolveAsync(restored, "DEMO", "HQ", [restoredIssue]);
        Assert.Equal(restoredIssueValue,
            restoredFacts[restoredIssue.Id].Sum(f => f.CostAmount));
    }

    [Fact]
    public async Task Backdated_standard_gr_posts_and_rolls_back_with_po_quantity()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
        }
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 6.5m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var request = GrRequest(po, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var saved = await receipt.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            (await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo)).BaseUnitPrices = 99m;
            await tamper.SaveChangesAsync();
        }
        var rejectedCost = await receipt.PostAsync([saved.BatchNo]);
        Assert.False(rejectedCost.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(11m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            (await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo)).BaseUnitPrices = 10m;
            await unchanged.SaveChangesAsync();
        }
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(16m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(16m, (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
            Assert.Equal(104m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            var history = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(IvTrxTypes.GoodsReceive, history.TrxType);
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [history]);
            Assert.Equal(32.5m, Assert.Single(active[history.Id]).CostAmount);
        }
        await using (var invoiced = await _factory.CreateDbContextAsync())
        {
            (await invoiced.PoOrderDetails.SingleAsync()).InvoicedQty = 1m;
            await invoiced.SaveChangesAsync();
        }
        var rejectedRollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.False(rejectedRollback.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(16m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            (await unchanged.PoOrderDetails.SingleAsync()).InvoicedQty = 0m;
            await unchanged.SaveChangesAsync();
        }
        var extraIssue = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 12m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 6.5m
            }]
        });
        Assert.True(extraIssue.Succeeded, extraIssue.ErrorMessage);
        Assert.True((await issue.PostAsync([extraIssue.BatchNo])).Succeeded);
        var rejectedShortage = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.False(rejectedShortage.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(4m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
        }
        Assert.True((await issue.RollbackAsync([extraIssue.BatchNo])).Succeeded);
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(71.5m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Fact]
    public async Task Backdated_standard_gr_keeps_later_uninvoiced_gr_value_and_po_quantity()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                "A100", 6.5m);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var laterPo = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        var laterGr = await receipt.SaveNewAsync(GrRequest(laterPo, recvQty: 3m));
        Assert.True(laterGr.Succeeded, laterGr.ErrorMessage);
        Assert.True((await receipt.PostAsync([laterGr.BatchNo])).Succeeded);
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var laterMi = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 6.5m
            }]
        });
        Assert.True(laterMi.Succeeded, laterMi.ErrorMessage);
        Assert.True((await issue.PostAsync([laterMi.BatchNo])).Succeeded);

        var earlierPo = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync(p =>
                p.PoNo == earlierPo.PoNo)).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var request = GrRequest(earlierPo, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var earlierGr = await receipt.SaveNewAsync(request);
        Assert.True(earlierGr.Succeeded, earlierGr.ErrorMessage);
        var posted = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(19m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(123.5m,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == earlierPo.PoNo)).RecvQty);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == laterPo.PoNo)).RecvQty);
            var laterHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == laterGr.BatchNo && h.EntryRole == "ORIGINAL");
            var facts = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [laterHistory]);
            Assert.Equal(19.5m, Assert.Single(facts[laterHistory.Id]).CostAmount);
        }
        var rollback = await receipt.RollbackAsync([earlierGr.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(14m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(91m,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == earlierPo.PoNo)).RecvQty);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == laterPo.PoNo)).RecvQty);
            (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == laterPo.PoNo)).InvoicedQty = 1m;
            await db.SaveChangesAsync();
        }
        var blocked = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(blocked.Succeeded);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(14m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync(p =>
            p.PoNo == earlierPo.PoNo)).RecvQty);
        (await unchanged.PoOrderDetails.SingleAsync(p =>
            p.PoNo == laterPo.PoNo)).InvoicedQty = 0m;
        var settledHistory = await unchanged.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == laterGr.BatchNo && h.EntryRole == "ORIGINAL");
        var laterFact = await unchanged.StockValuationFacts.SingleAsync(f =>
            f.InventoryHistoryId == settledHistory.Id
            && f.ReversesValuationFactId == null);
        await unchanged.StockValuationFacts.Where(f => f.Id == laterFact.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.SourceDocumentType,
                IvTrxTypes.MiscellaneousReceipt));
        var blockedProvenance = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(blockedProvenance.Succeeded);
        Assert.Contains("later", blockedProvenance.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await unchanged.StockValuationFacts.Where(f => f.Id == laterFact.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.SourceDocumentType,
                IvTrxTypes.GoodsReceive));
        unchanged.PurchaseReceiptCostSettlements.Add(
            new PurchaseReceiptCostSettlement
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                PiDocNo = "PI-STANDARD-LATER", PiLineNo = 1,
                PoNo = laterPo.PoNo, PoRelNo = laterPo.PoRelNo,
                PoLineNo = laterPo.PoLineNo, ItemCode = "A100",
                ReceiptValuationFactId = laterFact.Id,
                ReceiptInventoryHistoryId = settledHistory.Id,
                ReceiptBatchNo = laterGr.BatchNo,
                SettledBaseQty = 1m,
                StockPostingId = laterFact.StockPostingId,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
        await unchanged.SaveChangesAsync();
        var blockedSettlement = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(blockedSettlement.Succeeded);
        await using var unchangedAfterFailure = await _factory.CreateDbContextAsync();
        Assert.Equal(14m, (await unchangedAfterFailure.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(0m, (await unchangedAfterFailure.PoOrderDetails.SingleAsync(p =>
            p.PoNo == earlierPo.PoNo)).RecvQty);
    }

    [Fact]
    public async Task Backdated_fifo_gr_reprices_later_issue_and_restores_po_on_rollback()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-1)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-1))
            ]);
        }
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 999m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var request = GrRequest(po, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var saved = await receipt.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            (await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo)).BaseUnitPrices = 99m;
            await tamper.SaveChangesAsync();
        }
        var rejectedCost = await receipt.PostAsync([saved.BatchNo]);
        Assert.False(rejectedCost.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            (await unchanged.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo)).BaseUnitPrices = 10m;
            await unchanged.SaveChangesAsync();
        }
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(12m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(60m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            var laterHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [laterHistory]);
            Assert.Equal(45m, active[laterHistory.Id].Sum(f => f.CostAmount));
        }
        await using (var invoiced = await _factory.CreateDbContextAsync())
        {
            (await invoiced.PoOrderDetails.SingleAsync()).InvoicedQty = 1m;
            await invoiced.SaveChangesAsync();
        }
        var rejectedRollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.False(rejectedRollback.Succeeded);
        await using (var unchanged = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(12m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            (await unchanged.PoOrderDetails.SingleAsync()).InvoicedQty = 0m;
            await unchanged.SaveChangesAsync();
        }
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using (var restored = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(28m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        }
        var repost = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
        await using (var replayed = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(12m, (await replayed.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(60m, (await replayed.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(5m, (await replayed.PoOrderDetails.SingleAsync()).RecvQty);
        }
        var secondRollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(secondRollback.Succeeded, secondRollback.ErrorMessage);
        await using var restoredAgain = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restoredAgain.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(28m, (await restoredAgain.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restoredAgain.PoOrderDetails.SingleAsync()).RecvQty);
        var laterRollback = await issue.RollbackAsync([later.BatchNo]);
        Assert.True(laterRollback.Succeeded, laterRollback.ErrorMessage);
        await using var afterIssueRollback = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await afterIssueRollback.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(55m,
            (await afterIssueRollback.StockCostStates.SingleAsync()).InventoryValue);
        var postAfterIssueRollback = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(postAfterIssueRollback.Succeeded,
            postAfterIssueRollback.ErrorMessage);
        var rollbackAfterIssueRollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollbackAfterIssueRollback.Succeeded,
            rollbackAfterIssueRollback.ErrorMessage);
        await using var final = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await final.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(55m, (await final.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await final.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Fact]
    public async Task Backdated_fifo_gr_keeps_later_uninvoiced_gr_and_reprices_issue()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-1)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-1))
            ]);
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var laterPo = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        var laterGr = await receipt.SaveNewAsync(GrRequest(laterPo, recvQty: 3m));
        Assert.True(laterGr.Succeeded, laterGr.ErrorMessage);
        Assert.True((await receipt.PostAsync([laterGr.BatchNo])).Succeeded);
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var laterMi = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 999m
            }]
        });
        Assert.True(laterMi.Succeeded, laterMi.ErrorMessage);
        Assert.True((await issue.PostAsync([laterMi.BatchNo])).Succeeded);

        var earlierPo = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync(p =>
                p.PoNo == earlierPo.PoNo)).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var request = GrRequest(earlierPo, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var earlierGr = await receipt.SaveNewAsync(request);
        Assert.True(earlierGr.Succeeded, earlierGr.ErrorMessage);
        var posted = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(15m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(90m,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == earlierPo.PoNo)).RecvQty);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == laterPo.PoNo)).RecvQty);
            var laterHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == laterGr.BatchNo && h.EntryRole == "ORIGINAL");
            var laterFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [laterHistory]);
            Assert.Equal(30m, Assert.Single(laterFacts[laterHistory.Id]).CostAmount);
            var issueHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == laterMi.BatchNo && h.EntryRole == "ORIGINAL");
            var issueFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [issueHistory]);
            Assert.Equal(45m, issueFacts[issueHistory.Id].Sum(f => f.CostAmount));
        }
        var rollback = await receipt.RollbackAsync([earlierGr.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(58m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == earlierPo.PoNo)).RecvQty);
        Assert.Equal(3m, (await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == laterPo.PoNo)).RecvQty);
        (await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == laterPo.PoNo)).InvoicedQty = 1m;
        await restored.SaveChangesAsync();
        var blockedInvoice = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(blockedInvoice.Succeeded);
        (await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == laterPo.PoNo)).InvoicedQty = 0m;
        var settledHistory = await restored.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == laterGr.BatchNo && h.EntryRole == "ORIGINAL");
        var laterFact = await restored.StockValuationFacts.SingleAsync(f =>
            f.InventoryHistoryId == settledHistory.Id
            && f.ReversesValuationFactId == null);
        await restored.StockValuationFacts.Where(f => f.Id == laterFact.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.SourceDocumentType,
                IvTrxTypes.MiscellaneousReceipt));
        var blockedProvenance = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(blockedProvenance.Succeeded);
        Assert.Contains("receipt", blockedProvenance.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await restored.StockValuationFacts.Where(f => f.Id == laterFact.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.SourceDocumentType,
                IvTrxTypes.GoodsReceive));
        restored.PurchaseReceiptCostSettlements.Add(
            new PurchaseReceiptCostSettlement
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                PiDocNo = "PI-FIFO-LATER", PiLineNo = 1,
                PoNo = laterPo.PoNo, PoRelNo = laterPo.PoRelNo,
                PoLineNo = laterPo.PoLineNo, ItemCode = "A100",
                ReceiptValuationFactId = laterFact.Id,
                ReceiptInventoryHistoryId = settledHistory.Id,
                ReceiptBatchNo = laterGr.BatchNo,
                SettledBaseQty = 1m,
                StockPostingId = laterFact.StockPostingId,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
        await restored.SaveChangesAsync();
        var blockedSettlement = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(blockedSettlement.Succeeded);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync(p =>
            p.PoNo == earlierPo.PoNo)).RecvQty);
    }

    [Fact]
    public async Task Backdated_wac_gr_keeps_later_uninvoiced_gr_value_and_reprices_issue()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var laterPo = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        var laterGr = await receipt.SaveNewAsync(GrRequest(laterPo, recvQty: 3m));
        Assert.True(laterGr.Succeeded, laterGr.ErrorMessage);
        Assert.True((await receipt.PostAsync([laterGr.BatchNo])).Succeeded);
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var laterMi = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(laterMi.Succeeded, laterMi.ErrorMessage);
        Assert.True((await issue.PostAsync([laterMi.BatchNo])).Succeeded);
        decimal priorValue;
        decimal priorIssueValue;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            priorValue = (await db.StockCostStates.SingleAsync()).InventoryValue;
            var issueHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == laterMi.BatchNo && h.EntryRole == "ORIGINAL");
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [issueHistory]);
            priorIssueValue = active[issueHistory.Id].Sum(f => f.CostAmount);
        }

        var earlierPo = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var po = await db.PoOrders.SingleAsync(p => p.PoNo == earlierPo.PoNo);
            po.PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var earlierRequest = GrRequest(earlierPo, recvQty: 5m);
        earlierRequest.TrxDate = FixedToday.AddDays(-2);
        var earlierGr = await receipt.SaveNewAsync(earlierRequest);
        Assert.True(earlierGr.Succeeded, earlierGr.ErrorMessage);
        var posted = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(19m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == earlierPo.PoNo)).RecvQty);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync(p =>
                p.PoNo == laterPo.PoNo)).RecvQty);
            var laterGrHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == laterGr.BatchNo && h.EntryRole == "ORIGINAL");
            var laterFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [laterGrHistory]);
            Assert.Equal(30m, laterFacts[laterGrHistory.Id].Sum(f => f.CostAmount));
            var issueHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == laterMi.BatchNo && h.EntryRole == "ORIGINAL");
            var active = await StockReplayValuationGenerationResolver.ResolveAsync(
                db, "DEMO", "HQ", [issueHistory]);
            Assert.True(active[issueHistory.Id].Sum(f => f.CostAmount)
                > priorIssueValue);
        }
        var rollback = await receipt.RollbackAsync([earlierGr.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(14m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(priorValue,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == earlierPo.PoNo)).RecvQty);
        Assert.Equal(3m, (await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == laterPo.PoNo)).RecvQty);
        var laterPoLine = await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == laterPo.PoNo);
        laterPoLine.InvoicedQty = 1m;
        await restored.SaveChangesAsync();
        var rejectedInvoiceQty = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(rejectedInvoiceQty.Succeeded);
        Assert.Contains("procurement replay",
            rejectedInvoiceQty.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        laterPoLine.InvoicedQty = 0m;
        await restored.SaveChangesAsync();
        var laterHistory = await restored.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == laterGr.BatchNo && h.EntryRole == "ORIGINAL");
        var laterFact = await restored.StockValuationFacts.SingleAsync(f =>
            f.InventoryHistoryId == laterHistory.Id
            && f.ReversesValuationFactId == null);
        restored.PurchaseReceiptCostSettlements.Add(new PurchaseReceiptCostSettlement
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            PiDocNo = "PI-LATER", PiLineNo = 1,
            PoNo = laterPo.PoNo, PoRelNo = laterPo.PoRelNo,
            PoLineNo = laterPo.PoLineNo, ItemCode = "A100",
            ReceiptValuationFactId = laterFact.Id,
            ReceiptInventoryHistoryId = laterHistory.Id,
            ReceiptBatchNo = laterGr.BatchNo,
            SettledBaseQty = 1m,
            StockPostingId = laterFact.StockPostingId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST"
        });
        await restored.SaveChangesAsync();
        var rejectedSettlement = await receipt.PostAsync([earlierGr.BatchNo]);
        Assert.False(rejectedSettlement.Succeeded);
        Assert.Contains("procurement settlement",
            rejectedSettlement.ErrorMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(14m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync(p =>
            p.PoNo == earlierPo.PoNo)).RecvQty);
    }

    [Fact]
    public async Task Backdated_wac_gr_uses_approved_historical_foreign_currency_rate()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            db.SaCurrencies.Add(new SaCurrency
            {
                CompanyCode = "DEMO", CurrCode = "USD", IsActive = true
            });
            db.SaCurrRates.Add(new SaCurrRate
            {
                CurrCode = "USD", StartDate = FixedToday.AddDays(-10),
                EndDate = FixedToday.AddDays(10),
                HomeCurPerUnit = 2.5d, Status = true
            });
            (await db.PoSuppliers.SingleAsync()).Currency = "USD";
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 4m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m,
            currency: "USD");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var request = GrRequest(po, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        IvGoodsReceiptOperationResult saved;
        var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("fr-FR");
            saved = await receipt.SaveNewAsync(request);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = priorCulture;
        }
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            Assert.Equal("USD", line.Currency);
            Assert.Equal(25m, line.BaseUnitPrices);
            Assert.EndsWith("RATE=2.5", line.PriceEvidence);
            var rate = await db.SaCurrRates.SingleAsync();
            rate.HomeCurPerUnit = 3d;
            await db.SaveChangesAsync();
        }
        var rejectedRate = await receipt.PostAsync([saved.BatchNo]);
        Assert.False(rejectedRate.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(11m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            (await db.SaCurrRates.SingleAsync()).HomeCurPerUnit = 2.5d;
            await db.SaveChangesAsync();
        }
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(16m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(136m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        }
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(11m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(33m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Fact]
    public async Task Pi_allocation_sees_only_its_current_unsealed_gr_replay_root()
    {
        await EnsureActiveStockLedgerAsync();
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        var receipt = CreateGrSut(withLedger: true);
        var first = await receipt.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True((await receipt.PostAsync([first.BatchNo])).Succeeded);
        var second = await receipt.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.True((await receipt.PostAsync([second.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var postings = await db.StockPostings
            .Where(p => p.SourceDocumentType == IvTrxTypes.GoodsReceive
                && p.PostingRole == "PRIMARY")
            .OrderBy(p => p.PostingSequence).ToArrayAsync();
        Assert.Equal(2, postings.Length);
        foreach (var posting in postings)
            posting.SealedAtUtc = null;
        await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var epoch = await db.StockLedgerEpochs.SingleAsync();
        var root = new StockPostingContext(db, epoch, postings[0], "TEST");
        var service = new PurchaseReceiptCostSettlementService();

        PoInvoice Invoice(decimal qty) => new()
        {
            CompanyCode = "DEMO", BranchCode = "HQ", DocNo = "PI-REPLAY",
            Type = PoInvoiceTypes.Invoice, DocDate = FixedToday,
            CurrRate = 1m,
            Details = [new PoInvoiceDetail
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                DocNo = "PI-REPLAY", Line = 1, ICode = "A100",
                StdQty = qty, NetAmount = qty * 10m,
                PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                PoLineNo = po.PoLineNo
            }]
        };

        var defaultResult = await service.AllocateInvoiceAsync(db,
            Invoice(4m), StockCostMethods.MovingAverage,
            postings[0].Id, "TEST");
        Assert.False(defaultResult.Succeeded);
        var tooMuch = await service.AllocateInvoiceAsync(db,
            Invoice(5m), StockCostMethods.MovingAverage,
            postings[0].Id, "TEST", replayRoot: root);
        Assert.False(tooMuch.Succeeded);
        db.ChangeTracker.Clear();
        var ownRoot = await service.AllocateInvoiceAsync(db,
            Invoice(4m), StockCostMethods.MovingAverage,
            postings[0].Id, "TEST", replayRoot: root);
        Assert.True(ownRoot.Succeeded, ownRoot.Error);
        var settlement = Assert.Single(ownRoot.Settlements);
        Assert.Equal(first.BatchNo, settlement.ReceiptBatchNo);
        Assert.Equal(4m, settlement.SettledBaseQty);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Backdated_fifo_gr_remaps_zero_variance_pi_on_post_and_rollback()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-1))
            ]);
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var later = await receipt.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await receipt.PostAsync([later.BatchNo])).Succeeded);
        long laterFactId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var laterFact = await db.StockValuationFacts.SingleAsync(f =>
                f.SourceDocumentType == IvTrxTypes.GoodsReceive);
            laterFactId = laterFact.Id;
            Assert.Equal(30m, laterFact.CostAmount);
            (await db.PoOrderDetails.SingleAsync()).InvoicedQty = 3m;
            var invoice = new PoInvoice
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                DocNo = "PI-FIFO-GR", Type = PoInvoiceTypes.Invoice,
                Status = PoInvoiceStatuses.Posted, VendorCode = "VEND",
                DocDate = FixedToday, CurrRate = 1m,
                Details =
                [
                    new PoInvoiceDetail
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ",
                        DocNo = "PI-FIFO-GR", Line = 1,
                        ICode = "A100", Qty = 3m, StdQty = 3m,
                        NetAmount = 30m, PoNo = po.PoNo,
                        PoRelNo = po.PoRelNo, PoLineNo = po.PoLineNo
                    }
                ]
            };
            db.PoInvoices.Add(invoice);
            db.PurchaseReceiptCostSettlements.Add(
                new PurchaseReceiptCostSettlement
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    PiDocNo = invoice.DocNo, PiLineNo = 1,
                    PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo, ItemCode = "A100",
                    ReceiptValuationFactId = laterFact.Id,
                    ReceiptInventoryHistoryId = laterFact.InventoryHistoryId,
                    ReceiptBatchNo = later.BatchNo,
                    SettledBaseQty = 3m,
                    ReceiptCommercialUnitCost = 10m,
                    ReceiptCommercialBaseAmount = 30m,
                    ReceiptValuationUnitCost = 10m,
                    ReceiptValuationBaseAmount = 30m,
                    AllocatedActualBaseAmount = 30m,
                    StockPostingId = laterFact.StockPostingId,
                    CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
                });
            db.PurchaseCostAdjustments.Add(new PurchaseCostAdjustment
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                AdjustmentType = "PURCHASE_PRICE_VARIANCE",
                SourceDocumentType = "PO_INVOICE",
                SourceDocumentNo = invoice.DocNo,
                SourceDocumentLine = 1,
                PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                PoLineNo = po.PoLineNo, ItemCode = "A100",
                CostMethod = StockCostMethods.Fifo,
                BaseQty = 3m, ActualBaseAmount = 30m,
                ReferenceBaseAmount = 30m,
                CommercialReferenceAmount = 30m,
                StockPostingId = laterFact.StockPostingId,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
            await db.SaveChangesAsync();
        }
        var pileId = await ReadPileIdAsync();
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var laterIssue = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = pileId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 999m
            }]
        });
        Assert.True(laterIssue.Succeeded, laterIssue.ErrorMessage);
        Assert.True((await issue.PostAsync([laterIssue.BatchNo])).Succeeded);
        var request = GrRequest(po, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var earlier = await receipt.SaveNewAsync(request);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var posted = await receipt.PostAsync([earlier.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(15m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(90m,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            var issueHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == laterIssue.BatchNo && h.EntryRole == "ORIGINAL");
            var repricedIssue = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, "DEMO", "HQ", [issueHistory]);
            Assert.Equal(45m,
                repricedIssue[issueHistory.Id].Sum(f => f.CostAmount));
            var active = await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == "PI-FIFO-GR"
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id)).SingleAsync();
            Assert.NotEqual(laterFactId, active.ReceiptValuationFactId);
            var activeAdjustment = await db.PurchaseCostAdjustments.AsNoTracking()
                .Where(a => a.SourceDocumentNo == "PI-FIFO-GR"
                    && a.ReversesAdjustmentId == null
                    && !db.PurchaseCostAdjustments.Any(r =>
                        r.ReversesAdjustmentId == a.Id)).SingleAsync();
            Assert.Equal(StockCostMethods.Fifo,
                activeAdjustment.CostMethod);
        }
        var rollback = await receipt.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(58m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(3m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        var restoredIssueHistory = await restored.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == laterIssue.BatchNo && h.EntryRole == "ORIGINAL");
        var restoredIssue = await StockReplayValuationGenerationResolver
            .ResolveAsync(restored, "DEMO", "HQ", [restoredIssueHistory]);
        Assert.Equal(27m,
            restoredIssue[restoredIssueHistory.Id].Sum(f => f.CostAmount));
        var activeRestored = await restored.PurchaseReceiptCostSettlements
            .AsNoTracking().Where(s => s.PiDocNo == "PI-FIFO-GR"
                && s.ReversesSettlementId == null
                && !restored.PurchaseReceiptCostSettlements.Any(r =>
                    r.ReversesSettlementId == s.Id)).SingleAsync();
        Assert.Equal(laterFactId, activeRestored.ReceiptValuationFactId);
        var restoredAdjustment = await restored.PurchaseCostAdjustments
            .AsNoTracking().Where(a => a.SourceDocumentNo == "PI-FIFO-GR"
                && a.ReversesAdjustmentId == null
                && !restored.PurchaseCostAdjustments.Any(r =>
                    r.ReversesAdjustmentId == a.Id)).SingleAsync();
        Assert.Equal(StockCostMethods.Fifo,
            restoredAdjustment.CostMethod);
        await restored.PoInvoiceDetails
            .Where(d => d.DocNo == "PI-FIFO-GR" && d.Line == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NetAmount, 33m));
        var rejectedVariance = await receipt.PostAsync([earlier.BatchNo]);
        Assert.False(rejectedVariance.Succeeded);
        Assert.Contains("variance", rejectedVariance.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(58m,
            (await unchanged.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(3m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Backdated_standard_gr_remaps_pi_on_post_and_rollback(
        int purchaseVariance)
    {
        var invoiceAmount = 19.5m + purchaseVariance;
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                "A100", 6.5m);
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 6.5m, StockCostMethods.Standard);
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var later = await receipt.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await receipt.PostAsync([later.BatchNo])).Succeeded);
        long laterFactId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var laterFact = await db.StockValuationFacts.SingleAsync(f =>
                f.SourceDocumentType == IvTrxTypes.GoodsReceive);
            laterFactId = laterFact.Id;
            Assert.Equal(19.5m, laterFact.CostAmount);
            (await db.PoOrderDetails.SingleAsync()).InvoicedQty = 3m;
            var invoice = new PoInvoice
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                DocNo = "PI-STANDARD-GR", Type = PoInvoiceTypes.Invoice,
                Status = PoInvoiceStatuses.Posted, VendorCode = "VEND",
                DocDate = FixedToday, CurrRate = 1m,
                Details =
                [
                    new PoInvoiceDetail
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ",
                        DocNo = "PI-STANDARD-GR", Line = 1,
                        ICode = "A100", Qty = 3m, StdQty = 3m,
                        NetAmount = invoiceAmount, PoNo = po.PoNo,
                        PoRelNo = po.PoRelNo, PoLineNo = po.PoLineNo
                    }
                ]
            };
            db.PoInvoices.Add(invoice);
            db.PurchaseReceiptCostSettlements.Add(
                new PurchaseReceiptCostSettlement
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    PiDocNo = invoice.DocNo, PiLineNo = 1,
                    PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo, ItemCode = "A100",
                    ReceiptValuationFactId = laterFact.Id,
                    ReceiptInventoryHistoryId = laterFact.InventoryHistoryId,
                    ReceiptBatchNo = later.BatchNo,
                    SettledBaseQty = 3m,
                    ReceiptCommercialUnitCost = 10m,
                    ReceiptCommercialBaseAmount = 30m,
                    ReceiptValuationUnitCost = 6.5m,
                    ReceiptValuationBaseAmount = 19.5m,
                    AllocatedActualBaseAmount = invoiceAmount,
                    CommercialVarianceAmount = invoiceAmount - 30m,
                    ValuationVarianceAmount = purchaseVariance,
                    StockPostingId = laterFact.StockPostingId,
                    CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
                });
            db.PurchaseCostAdjustments.Add(new PurchaseCostAdjustment
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                AdjustmentType = "PURCHASE_PRICE_VARIANCE",
                SourceDocumentType = "PO_INVOICE",
                SourceDocumentNo = invoice.DocNo,
                SourceDocumentLine = 1,
                PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                PoLineNo = po.PoLineNo, ItemCode = "A100",
                CostMethod = StockCostMethods.Standard,
                BaseQty = 3m, ActualBaseAmount = invoiceAmount,
                ReferenceBaseAmount = 19.5m,
                CommercialReferenceAmount = 30m,
                TotalAdjustmentAmount = purchaseVariance,
                ConsumedVarianceAmount = purchaseVariance,
                StockPostingId = laterFact.StockPostingId,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
            await db.SaveChangesAsync();
        }
        var request = GrRequest(po, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var earlier = await receipt.SaveNewAsync(request);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var posted = await receipt.PostAsync([earlier.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(23m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(149.5m,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            var active = await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == "PI-STANDARD-GR"
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id)).SingleAsync();
            Assert.NotEqual(laterFactId, active.ReceiptValuationFactId);
            Assert.Equal(19.5m, active.ReceiptValuationBaseAmount);
            Assert.Equal(30m, active.ReceiptCommercialBaseAmount);
            Assert.Equal(invoiceAmount, active.AllocatedActualBaseAmount);
            var activeAdjustment = await db.PurchaseCostAdjustments.AsNoTracking()
                .Where(a => a.SourceDocumentNo == "PI-STANDARD-GR"
                    && a.ReversesAdjustmentId == null
                    && !db.PurchaseCostAdjustments.Any(r =>
                        r.ReversesAdjustmentId == a.Id)).SingleAsync();
            Assert.Equal(purchaseVariance,
                activeAdjustment.TotalAdjustmentAmount);
            Assert.Equal(0m,
                activeAdjustment.InventoryAdjustmentAmount);
            Assert.Equal(purchaseVariance,
                activeAdjustment.ConsumedVarianceAmount);
        }
        var rollback = await receipt.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(18m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(117m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(3m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        var activeRestored = await restored.PurchaseReceiptCostSettlements
            .AsNoTracking().Where(s => s.PiDocNo == "PI-STANDARD-GR"
                && s.ReversesSettlementId == null
                && !restored.PurchaseReceiptCostSettlements.Any(r =>
                    r.ReversesSettlementId == s.Id)).SingleAsync();
        Assert.Equal(laterFactId, activeRestored.ReceiptValuationFactId);
        Assert.Equal(invoiceAmount, activeRestored.AllocatedActualBaseAmount);
        var restoredAdjustment = await restored.PurchaseCostAdjustments
            .AsNoTracking().Where(a => a.SourceDocumentNo == "PI-STANDARD-GR"
                && a.ReversesAdjustmentId == null
                && !restored.PurchaseCostAdjustments.Any(r =>
                    r.ReversesAdjustmentId == a.Id)).SingleAsync();
        Assert.Equal(purchaseVariance,
            restoredAdjustment.TotalAdjustmentAmount);
        Assert.Equal(0m,
            restoredAdjustment.InventoryAdjustmentAmount);
        await restored.PoInvoiceDetails
            .Where(d => d.DocNo == "PI-STANDARD-GR" && d.Line == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NetAmount,
                invoiceAmount + 3m));
        var rejectedVariance = await receipt.PostAsync([earlier.BatchNo]);
        Assert.False(rejectedVariance.Succeeded);
        Assert.Contains("variance", rejectedVariance.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(18m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(117m,
            (await unchanged.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(3m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Fact]
    public async Task Backdated_wac_gr_remaps_zero_variance_pi_on_post_and_rollback()
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotNo = string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var later = await receipt.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await receipt.PostAsync([later.BatchNo])).Succeeded);

        long laterFactId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var laterFact = await db.StockValuationFacts.SingleAsync(f =>
                f.SourceDocumentType == IvTrxTypes.GoodsReceive);
            laterFactId = laterFact.Id;
            (await db.PoOrderDetails.SingleAsync()).InvoicedQty = 3m;
            var invoice = new PoInvoice
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                DocNo = "PI-GR-REPLAY", Type = PoInvoiceTypes.Invoice,
                Status = PoInvoiceStatuses.Posted, VendorCode = "VEND",
                DocDate = FixedToday, CurrRate = 1m,
                Details =
                [
                    new PoInvoiceDetail
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ",
                        DocNo = "PI-GR-REPLAY", Line = 1,
                        ICode = "A100", Qty = 3m, StdQty = 3m,
                        NetAmount = 30m, PoNo = po.PoNo,
                        PoRelNo = po.PoRelNo, PoLineNo = po.PoLineNo
                    }
                ]
            };
            db.PoInvoices.Add(invoice);
            db.PurchaseReceiptCostSettlements.Add(
                new PurchaseReceiptCostSettlement
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    PiDocNo = invoice.DocNo, PiLineNo = 1,
                    PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo, ItemCode = "A100",
                    ReceiptValuationFactId = laterFact.Id,
                    ReceiptInventoryHistoryId = laterFact.InventoryHistoryId,
                    ReceiptBatchNo = later.BatchNo,
                    SettledBaseQty = 3m,
                    ReceiptCommercialUnitCost = 10m,
                    ReceiptCommercialBaseAmount = 30m,
                    ReceiptValuationUnitCost = 10m,
                    ReceiptValuationBaseAmount = 30m,
                    AllocatedActualBaseAmount = 30m,
                    StockPostingId = laterFact.StockPostingId,
                    CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
                });
            db.PurchaseCostAdjustments.Add(new PurchaseCostAdjustment
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                AdjustmentType = "PURCHASE_PRICE_VARIANCE",
                SourceDocumentType = "PO_INVOICE",
                SourceDocumentNo = invoice.DocNo,
                SourceDocumentLine = 1,
                PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                PoLineNo = po.PoLineNo, ItemCode = "A100",
                CostMethod = StockCostMethods.MovingAverage,
                BaseQty = 3m, ActualBaseAmount = 30m,
                ReferenceBaseAmount = 30m,
                CommercialReferenceAmount = 30m,
                StockPostingId = laterFact.StockPostingId,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
            await db.SaveChangesAsync();
        }
        var request = GrRequest(po, recvQty: 5m);
        request.TrxDate = FixedToday.AddDays(-2);
        var earlier = await receipt.SaveNewAsync(request);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        var posted = await receipt.PostAsync([earlier.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        long earlierFactId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            earlierFactId = (await db.StockValuationFacts.SingleAsync(f =>
                f.SourceDocumentType == IvTrxTypes.GoodsReceive
                && f.SourceDocumentNo == earlier.BatchNo.ToString())).Id;
            Assert.Equal(23m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(125m,
                (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            var active = await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == "PI-GR-REPLAY"
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id))
                .SingleAsync();
            Assert.Equal(earlierFactId, active.ReceiptValuationFactId);
        }
        var rollback = await receipt.RollbackAsync([earlier.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(18m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(75m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(3m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
        Assert.Equal(3m, (await restored.PoOrderDetails.SingleAsync()).InvoicedQty);
        var activeRestored = await restored.PurchaseReceiptCostSettlements
            .AsNoTracking().Where(s => s.PiDocNo == "PI-GR-REPLAY"
                && s.ReversesSettlementId == null
                && !restored.PurchaseReceiptCostSettlements.Any(r =>
                    r.ReversesSettlementId == s.Id))
            .SingleAsync();
        Assert.Equal(laterFactId, activeRestored.ReceiptValuationFactId);

        await restored.PoInvoiceDetails
            .Where(d => d.DocNo == "PI-GR-REPLAY" && d.Line == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NetAmount, 33m));
        var rejectedVariance = await receipt.PostAsync([earlier.BatchNo]);
        Assert.False(rejectedVariance.Succeeded);
        Assert.Contains("variance", rejectedVariance.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(18m, (await restored.IvBalLocs.AsNoTracking()
            .SingleAsync()).StdQty);
        Assert.Equal(3m, (await restored.PoOrderDetails.AsNoTracking()
            .SingleAsync()).RecvQty);
        Assert.Equal(3m, (await restored.PoOrderDetails.AsNoTracking()
            .SingleAsync()).InvoicedQty);
        Assert.Equal(IvBatchStatuses.New,
            (await restored.IvTrxBatches.AsNoTracking().SingleAsync(b =>
                b.BatchNo == earlier.BatchNo)).BatchStatus);
        await restored.PoInvoiceDetails
            .Where(d => d.DocNo == "PI-GR-REPLAY" && d.Line == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NetAmount, 30m));
        restored.PoInvoices.Add(new PoInvoice
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            DocNo = "CN-GR-REPLAY", Type = PoInvoiceTypes.CreditNote,
            Status = PoInvoiceStatuses.New, VendorCode = "VEND",
            DocDate = FixedToday, InvNo = "PI-GR-REPLAY"
        });
        restored.PurchaseCostAdjustments.Add(new PurchaseCostAdjustment
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            AdjustmentType = "PURCHASE_CN_PRICE_ADJUSTMENT",
            SourceDocumentType = "PO_INVOICE_CN",
            SourceDocumentNo = "CN-GR-REPLAY",
            ItemCode = "A100", StockPostingId = activeRestored.StockPostingId,
            CreatedBy = "TEST", CreatedAtUtc = DateTime.UtcNow
        });
        await restored.SaveChangesAsync();
        Assert.True(await restored.PoInvoices.AsNoTracking().AnyAsync(i =>
            i.DocNo == "CN-GR-REPLAY"
            && i.Type == PoInvoiceTypes.CreditNote
            && i.Status == PoInvoiceStatuses.New
            && i.DeletedAtUtc == null
            && i.InvNo == "PI-GR-REPLAY"));
        var rejectedCreditNote = await receipt.PostAsync([earlier.BatchNo]);
        Assert.False(rejectedCreditNote.Succeeded);
        Assert.Contains("credit/debit note", rejectedCreditNote.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        (await restored.PoInvoices.SingleAsync(i =>
            i.DocNo == "CN-GR-REPLAY")).DeletedAtUtc = DateTime.UtcNow;
        await restored.SaveChangesAsync();
        var rejectedArchivedCreditNote = await receipt.PostAsync(
            [earlier.BatchNo]);
        Assert.False(rejectedArchivedCreditNote.Succeeded);
        Assert.Contains("credit/debit note",
            rejectedArchivedCreditNote.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
        await using var unchanged = await _factory.CreateDbContextAsync();
        Assert.Equal(18m, (await unchanged.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(75m,
            (await unchanged.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(3m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
        Assert.Equal(laterFactId, (await unchanged.PurchaseReceiptCostSettlements
            .AsNoTracking().Where(s => s.PiDocNo == "PI-GR-REPLAY"
                && s.ReversesSettlementId == null
                && !unchanged.PurchaseReceiptCostSettlements.Any(r =>
                    r.ReversesSettlementId == s.Id))
            .SingleAsync()).ReceiptValuationFactId);
    }

    [Theory]
    [InlineData(StockCostMethods.MovingAverage, false)]
    [InlineData(StockCostMethods.Fifo, false)]
    [InlineData(StockCostMethods.Standard, false)]
    [InlineData(StockCostMethods.MovingAverage, true)]
    [InlineData(StockCostMethods.Fifo, true)]
    [InlineData(StockCostMethods.Standard, true)]
    public async Task Posted_pi_rolls_back_after_backdated_gr_remap(
        string costMethod, bool lotControlled)
    {
        await EnsureActiveStockLedgerAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            if (costMethod == StockCostMethods.Fifo)
                await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            else if (costMethod == StockCostMethods.Standard)
            {
                await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db,
                    "A100", 6.5m);
            }
            int? lotId = null;
            if (lotControlled)
            {
                var master = await db.IvStockMasters.SingleAsync(m =>
                    m.ICode == "A100");
                master.LotControl = true;
                master.ExpiryControl = IvExpiryControlModes.None;
                var lot = new IvLot
                {
                    CompanyCode = "DEMO", ICode = "A100", LotNo = "LOT-1",
                    ReceiptDate = FixedToday.AddDays(-10), IsActive = true
                };
                db.IvLots.Add(lot);
                await db.SaveChangesAsync();
                lotId = lot.Id;
            }
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A100",
                WhCode = "MAIN", LocCode = "BIN1", LotId = lotId,
                LotNo = lotControlled ? "LOT-1" : string.Empty,
                IStatus = "ACTIVE", StdQty = 15m, StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            if (costMethod == StockCostMethods.Fifo)
                await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [
                    new FifoLayerSeed(5m, 3m,
                        new DateTime(2026, 2, 1)),
                    new FifoLayerSeed(10m, 4m,
                        FixedToday.AddDays(-1))
                ]);
            else
                await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                    "A100", 15m,
                    costMethod == StockCostMethods.Standard ? 6.5m : 3m,
                    costMethod);
        }
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.PoOrders.SingleAsync()).PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var receipt = CreateGrSut(withLedger: true,
            enableBackdatedReplay: true);
        var later = await receipt.SaveNewAsync(GrRequest(po, recvQty: 3m,
            lotNo: lotControlled ? "LOT-1" : null));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await receipt.PostAsync([later.BatchNo])).Succeeded);

        var invoiceService = CreatePiSut();
        var invoice = await invoiceService.SaveNewAsync(new PoInvoiceSaveRequest
        {
            Type = PoInvoiceTypes.Invoice,
            DocDate = FixedToday,
            VendorCode = "SUP01",
            InvNo = "SUP-REPLAY-1",
            Currency = "MYR",
            CurrRate = 1m,
            Lines =
            [
                new PoInvoiceLineRequest
                {
                    ICode = "A100", IDesc = "Stock A", Qty = 3m,
                    UnitPrice = 10m, SellingUom = "EA",
                    PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo, TaxGroup = "SR",
                    ItemGlCode = "GLPUR"
                }
            ]
        });
        Assert.True(invoice.Succeeded, invoice.ErrorMessage);
        var piDocNo = invoice.DocNo!;
        var piPost = await invoiceService.PostAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = piDocNo,
                RowVersion = invoice.Document!.RowVersion
            }]);
        Assert.True(piPost.Succeeded, piPost.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var postedPi = await db.PoInvoices.AsNoTracking()
                .Include(i => i.Details).SingleAsync(i => i.DocNo == piDocNo);
            var settlement = await db.PurchaseReceiptCostSettlements
                .AsNoTracking().SingleAsync(s => s.PiDocNo == piDocNo);
            var laterHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.BatchNo == later.BatchNo
                    && h.EntryRole == "ORIGINAL");
            Assert.Equal(postedPi.CostingRevision,
                settlement.PiCostingRevision);
            Assert.Equal(laterHistory.Id,
                settlement.ReceiptInventoryHistoryId);
            Assert.Equal(laterHistory.PoNo,
                postedPi.Details.Single().PoNo);
            Assert.Equal(laterHistory.PoRelNo,
                postedPi.Details.Single().PoRelNo);
            Assert.Equal(laterHistory.PoLineNo,
                postedPi.Details.Single().PoLineNo);
            if (lotControlled)
                Assert.Equal((await db.IvLots.SingleAsync()).Id,
                    laterHistory.ToLotId);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            Assert.Equal(PoInvoiceStatuses.Posted, postedPi.Status);
        }

        var request = GrRequest(po, recvQty: 5m,
            lotNo: lotControlled ? "LOT-1" : null);
        request.TrxDate = FixedToday.AddDays(-2);
        var earlier = await receipt.SaveNewAsync(request);
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        if (costMethod == StockCostMethods.MovingAverage && !lotControlled)
        {
            await using (var db = await _factory.CreateDbContextAsync())
            {
                (await db.PoInvoices.SingleAsync(i => i.DocNo == piDocNo))
                    .DeletedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            var archivedPi = await receipt.PostAsync([earlier.BatchNo]);
            Assert.False(archivedPi.Succeeded);
            Assert.Contains("one posted single-line invoice",
                archivedPi.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            await using var restoredInvoice = await _factory.CreateDbContextAsync();
            Assert.Equal(18m, (await restoredInvoice.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(3m, (await restoredInvoice.PoOrderDetails.SingleAsync()).RecvQty);
            (await restoredInvoice.PoInvoices.SingleAsync(i => i.DocNo == piDocNo))
                .DeletedAtUtc = null;
            await restoredInvoice.SaveChangesAsync();
        }
        var grPost = await receipt.PostAsync([earlier.BatchNo]);
        Assert.True(grPost.Succeeded, grPost.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var earlierFact = await db.StockValuationFacts.SingleAsync(f =>
                f.SourceDocumentType == IvTrxTypes.GoodsReceive
                && f.SourceDocumentNo == earlier.BatchNo.ToString());
            if (lotControlled)
                Assert.Equal((await db.IvLots.SingleAsync()).Id,
                    earlierFact.LotId);
            var active = await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id)).SingleAsync();
            Assert.Equal(earlierFact.Id, active.ReceiptValuationFactId);
            Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
        }

        if (costMethod is StockCostMethods.MovingAverage
            or StockCostMethods.Fifo or StockCostMethods.Standard)
        {
            var fullCredit = await invoiceService.SaveNewAsync(new PoInvoiceSaveRequest
            {
                Type = PoInvoiceTypes.CreditNote,
                DocDate = FixedToday,
                VendorCode = "SUP01",
                InvNo = piDocNo,
                Currency = "MYR",
                CurrRate = 1m,
                Lines =
                [
                    new PoInvoiceLineRequest
                    {
                        ICode = "A100", IDesc = "Stock A", Qty = 3m,
                        UnitPrice = 10m, SellingUom = "EA",
                        PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                        PoLineNo = po.PoLineNo,
                        ReferencedInvoiceLineNo = 1,
                        TaxGroup = "SR", ItemGlCode = "GLPUR"
                    }
                ]
            });
            Assert.True(fullCredit.Succeeded, fullCredit.ErrorMessage);
            var fullCnNo = fullCredit.DocNo!;
            var fullCnPost = await invoiceService.PostAsync(
                [new PoInvoiceKeyedRequest
                {
                    DocNo = fullCnNo,
                    RowVersion = fullCredit.Document!.RowVersion
                }]);
            Assert.True(fullCnPost.Succeeded, fullCnPost.ErrorMessage);
            await using (var db = await _factory.CreateDbContextAsync())
                Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            var extraRequest = GrRequest(po, recvQty: 1m,
                lotNo: lotControlled ? "LOT-1" : null);
            extraRequest.TrxDate = FixedToday.AddDays(-1);
            var extraReceipt = await receipt.SaveNewAsync(extraRequest);
            Assert.True(extraReceipt.Succeeded, extraReceipt.ErrorMessage);
            var zeroNetPost = await receipt.PostAsync([extraReceipt.BatchNo]);
            if (costMethod is StockCostMethods.MovingAverage
                or StockCostMethods.Fifo or StockCostMethods.Standard)
            {
                Assert.True(zeroNetPost.Succeeded, zeroNetPost.ErrorMessage);
                var extraRollback = await receipt.RollbackAsync(
                    [extraReceipt.BatchNo]);
                Assert.True(extraRollback.Succeeded,
                    extraRollback.ErrorMessage);
            }
            else
            {
                Assert.False(zeroNetPost.Succeeded);
                Assert.Contains("net invoice quantity is zero",
                    zeroNetPost.ErrorMessage,
                    StringComparison.OrdinalIgnoreCase);
            }
            var zeroNetRollback = await receipt.RollbackAsync([earlier.BatchNo]);
            if (costMethod is StockCostMethods.MovingAverage
                or StockCostMethods.Fifo or StockCostMethods.Standard)
            {
                Assert.True(zeroNetRollback.Succeeded,
                    zeroNetRollback.ErrorMessage);
                var zeroNetRepost = await receipt.PostAsync([earlier.BatchNo]);
                Assert.True(zeroNetRepost.Succeeded,
                    zeroNetRepost.ErrorMessage);
            }
            else
            {
                Assert.False(zeroNetRollback.Succeeded);
                Assert.Contains("net invoice quantity is zero",
                    zeroNetRollback.ErrorMessage,
                    StringComparison.OrdinalIgnoreCase);
            }
            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(23m, (await db.IvBalLocs.SingleAsync()).StdQty);
                Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                if (costMethod == StockCostMethods.Standard)
                    Assert.Equal(149.5m,
                        (await db.StockCostStates.SingleAsync()).InventoryValue);
                Assert.Equal(IvBatchStatuses.New,
                    (await db.IvTrxBatches.SingleAsync(b =>
                        b.BatchNo == extraReceipt.BatchNo)).BatchStatus);
            }
            if (costMethod == StockCostMethods.MovingAverage && !lotControlled)
            {
                for (var generation = 0; generation < 2; generation++)
                {
                    var remove = await receipt.RollbackAsync([earlier.BatchNo]);
                    Assert.True(remove.Succeeded, remove.ErrorMessage);
                    var restore = await receipt.PostAsync([earlier.BatchNo]);
                    Assert.True(restore.Succeeded, restore.ErrorMessage);
                }
                var overLimit = await receipt.RollbackAsync([earlier.BatchNo]);
                Assert.False(overLimit.Succeeded);
                Assert.Contains("generation limit", overLimit.ErrorMessage,
                    StringComparison.OrdinalIgnoreCase);
                await using (var capDb = await _factory.CreateDbContextAsync())
                {
                    Assert.Equal(23m, (await capDb.IvBalLocs.SingleAsync()).StdQty);
                    Assert.Equal(8m,
                        (await capDb.PoOrderDetails.SingleAsync()).RecvQty);
                    Assert.Equal(16, await capDb.StockReplayEffects.AsNoTracking()
                        .CountAsync(x => x.EffectKind == StockReplayEffectTypes
                            .PurchaseCreditNoteCostReplacement));
                }
                await using (var archiveDb = await _factory.CreateDbContextAsync())
                {
                    (await archiveDb.PoInvoices.SingleAsync(i =>
                        i.DocNo == piDocNo)).DeletedAtUtc = DateTime.UtcNow;
                    await archiveDb.SaveChangesAsync();
                }
                var archivedZeroNetPost = await receipt.PostAsync(
                    [extraReceipt.BatchNo]);
                Assert.False(archivedZeroNetPost.Succeeded);
                Assert.Contains("one posted single-line invoice",
                    archivedZeroNetPost.ErrorMessage,
                    StringComparison.OrdinalIgnoreCase);
                var archivedZeroNetRollback = await receipt.RollbackAsync(
                    [earlier.BatchNo]);
                Assert.False(archivedZeroNetRollback.Succeeded);
                Assert.Contains("one posted single-line invoice",
                    archivedZeroNetRollback.ErrorMessage,
                    StringComparison.OrdinalIgnoreCase);
                await using var restoredPi = await _factory.CreateDbContextAsync();
                Assert.Equal(23m, (await restoredPi.IvBalLocs.SingleAsync()).StdQty);
                Assert.Equal(8m, (await restoredPi.PoOrderDetails.SingleAsync()).RecvQty);
                Assert.Equal(IvBatchStatuses.New,
                    (await restoredPi.IvTrxBatches.SingleAsync(b =>
                        b.BatchNo == extraReceipt.BatchNo)).BatchStatus);
                (await restoredPi.PoInvoices.SingleAsync(i =>
                    i.DocNo == piDocNo)).DeletedAtUtc = null;
                await restoredPi.SaveChangesAsync();
            }
            var postedFullCn = (await invoiceService.GetAsync(fullCnNo)).Document!;
            var fullCnRollback = await invoiceService.RollbackAsync(
                [new PoInvoiceKeyedRequest
                {
                    DocNo = fullCnNo,
                    RowVersion = postedFullCn.RowVersion
                }]);
            Assert.True(fullCnRollback.Succeeded, fullCnRollback.ErrorMessage);
            await using (var db = await _factory.CreateDbContextAsync())
                Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
        }

        var credit = await invoiceService.SaveNewAsync(new PoInvoiceSaveRequest
        {
            Type = PoInvoiceTypes.CreditNote,
            DocDate = FixedToday,
            VendorCode = "SUP01",
            InvNo = piDocNo,
            Currency = "MYR",
            CurrRate = 1m,
            Lines =
            [
                new PoInvoiceLineRequest
                {
                    ICode = "A100", IDesc = "Stock A", Qty = 1m,
                    UnitPrice = 10m, SellingUom = "EA",
                    PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ReferencedInvoiceLineNo = 1,
                    TaxGroup = "SR", ItemGlCode = "GLPUR"
                }
            ]
        });
        Assert.True(credit.Succeeded, credit.ErrorMessage);
        var cnDocNo = credit.DocNo!;
        if (costMethod == StockCostMethods.MovingAverage && !lotControlled)
        {
            await using var archiveDb = await _factory.CreateDbContextAsync();
            (await archiveDb.PoInvoices.SingleAsync(i => i.DocNo == cnDocNo))
                .DeletedAtUtc = DateTime.UtcNow;
            await archiveDb.SaveChangesAsync();
        }
        var grRollbackWithNewCn = await receipt.RollbackAsync(
            [earlier.BatchNo]);
        Assert.True(grRollbackWithNewCn.Succeeded,
            grRollbackWithNewCn.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(18m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(3m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
        }
        var grRepostWithNewCn = await receipt.PostAsync(
            [earlier.BatchNo]);
        Assert.True(grRepostWithNewCn.Succeeded,
            grRepostWithNewCn.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(23m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(3m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
        }
        if (costMethod == StockCostMethods.MovingAverage && !lotControlled)
        {
            await using var restoredDraft = await _factory.CreateDbContextAsync();
            (await restoredDraft.PoInvoices.SingleAsync(i => i.DocNo == cnDocNo))
                .DeletedAtUtc = null;
            await restoredDraft.SaveChangesAsync();
        }
        var currentCredit = (await invoiceService.GetAsync(cnDocNo)).Document!;
        var cnPost = await invoiceService.PostAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = cnDocNo,
                RowVersion = currentCredit.RowVersion
            }]);
        Assert.True(cnPost.Succeeded, cnPost.ErrorMessage);
        if (costMethod == StockCostMethods.MovingAverage && !lotControlled)
            await AssertCreditNoteReplacementOwnerResolutionAsync(
                cnDocNo, earlier.BatchNo);
        string expectedRemovalPlanHash;
        long expectedRemovalWatermark;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var original = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.BatchNo == earlier.BatchNo
                    && h.EntryRole == "ORIGINAL"
                    && !db.IvTrxHistories.Any(r =>
                        r.ReversesHistoryId == h.Id));
            var owner = await db.StockPostings.AsNoTracking()
                .SingleAsync(p => p.Id == original.StockPostingId);
            var key = new StockReplayEventKey(original.TrxDtTime,
                StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
                $"H{original.Id:D10}", 0);
            var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
                db, "DEMO", "HQ", original.ToBalLocId!.Value,
                new StockReplayPhysicalEvent(original.Id, key,
                    original.ToStdQty!.Value));
            expectedRemovalWatermark = physical.BranchWatermark;
            expectedRemovalPlanHash = costMethod switch
            {
                StockCostMethods.MovingAverage =>
                    (await StockReplayWacRemovalPlanner.BuildAsync(db,
                        "DEMO", "HQ", original.ICode!, physical)).PlanHash,
                StockCostMethods.Fifo =>
                    (await StockReplayFifoRemovalPlanner.BuildAsync(db,
                        "DEMO", "HQ", original.ICode!, physical)).PlanHash,
                _ => physical.PlanHash
            };
        }
        var grRollbackWithPostedCn = await receipt.RollbackAsync(
            [earlier.BatchNo]);
        if (costMethod is StockCostMethods.MovingAverage
            or StockCostMethods.Fifo or StockCostMethods.Standard)
        {
            Assert.True(grRollbackWithPostedCn.Succeeded,
                grRollbackWithPostedCn.ErrorMessage);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(18m, (await db.IvBalLocs.SingleAsync()).StdQty);
                Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                if (costMethod == StockCostMethods.Standard)
                    Assert.Equal(117m,
                        (await db.StockCostStates.SingleAsync()).InventoryValue);
                var cnOwnerId = await db.StockPostings.AsNoTracking()
                    .Where(p => p.SourceModule == "PROCUREMENT"
                        && p.SourceDocumentType == "PO_INVOICE_CN"
                        && p.SourceDocumentNo == cnDocNo
                        && p.PostingRole == "PRIMARY")
                    .Select(p => p.Id).SingleAsync();
                Assert.Equal(2, await db.StockReplayEffects.AsNoTracking()
                    .CountAsync(x => x.BusinessOwnerPostingId == cnOwnerId
                        && x.EffectKind == StockReplayEffectTypes
                            .PurchaseCreditNoteCostReplacement));
                var replayRunId = await db.StockReplayEffects.AsNoTracking()
                    .Where(x => x.BusinessOwnerPostingId == cnOwnerId
                        && x.EffectKind == StockReplayEffectTypes
                            .PurchaseCreditNoteCostReplacement)
                    .Select(x => x.ReplayRunId).Distinct().SingleAsync();
                var run = await db.StockReplayRuns.AsNoTracking()
                    .SingleAsync(r => r.Id == replayRunId);
                var root = await db.StockPostings.AsNoTracking()
                    .SingleAsync(p => p.Id == run.RootStockPostingId);
                Assert.Equal(expectedRemovalWatermark, run.PlanWatermark);
                if (costMethod == StockCostMethods.Standard)
                {
                    Assert.Equal(64, run.PlanHash.Length);
                    Assert.NotEqual(expectedRemovalPlanHash, run.PlanHash);
                }
                else
                    Assert.Equal(expectedRemovalPlanHash, run.PlanHash);
                Assert.Equal(run.PlanWatermark + 1, root.PostingSequence);
            }
            var grRepostWithPostedCn = await receipt.PostAsync(
                [earlier.BatchNo]);
            Assert.True(grRepostWithPostedCn.Succeeded,
                grRepostWithPostedCn.ErrorMessage);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(23m, (await db.IvBalLocs.SingleAsync()).StdQty);
                Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                if (costMethod == StockCostMethods.Standard)
                    Assert.Equal(149.5m,
                        (await db.StockCostStates.SingleAsync()).InventoryValue);
                var cnOwnerId = await db.StockPostings.AsNoTracking()
                    .Where(p => p.SourceModule == "PROCUREMENT"
                        && p.SourceDocumentType == "PO_INVOICE_CN"
                        && p.SourceDocumentNo == cnDocNo
                        && p.PostingRole == "PRIMARY")
                    .Select(p => p.Id).SingleAsync();
                Assert.Equal(4, await db.StockReplayEffects.AsNoTracking()
                    .CountAsync(x => x.BusinessOwnerPostingId == cnOwnerId
                        && x.EffectKind == StockReplayEffectTypes
                            .PurchaseCreditNoteCostReplacement));
            }
        }
        else
        {
            Assert.False(grRollbackWithPostedCn.Succeeded);
            Assert.Contains("credit/debit note",
                grRollbackWithPostedCn.ErrorMessage,
                StringComparison.OrdinalIgnoreCase);
        }
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(2m,
                (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            var postedPi = await db.PoInvoices.AsNoTracking()
                .Include(i => i.Details).SingleAsync(i => i.DocNo == piDocNo);
            var piPostingId = await db.StockPostings.AsNoTracking()
                .Where(p => p.SourceModule == "PROCUREMENT"
                    && p.SourceDocumentType == "PO_INVOICE"
                    && p.SourceDocumentNo == piDocNo
                    && p.PostingRole == "PRIMARY")
                .Select(p => p.Id).SingleAsync();
            var partial = await new PurchaseReceiptCostSettlementService()
                .ReverseInvoiceAsync(db, postedPi, piPostingId, "TEST");
            Assert.False(partial.Succeeded);
            Assert.Contains("partially reversed settlement",
                partial.Error, StringComparison.OrdinalIgnoreCase);
        }
        if (costMethod == StockCostMethods.MovingAverage && !lotControlled)
        {
            await using (var archiveDb = await _factory.CreateDbContextAsync())
            {
                (await archiveDb.PoInvoices.SingleAsync(i => i.DocNo == cnDocNo))
                    .DeletedAtUtc = DateTime.UtcNow;
                await archiveDb.SaveChangesAsync();
            }
            var archivedCnRollback = await receipt.RollbackAsync(
                [earlier.BatchNo]);
            Assert.False(archivedCnRollback.Succeeded);
            Assert.Contains("credit/debit note", archivedCnRollback.ErrorMessage,
                StringComparison.OrdinalIgnoreCase);
            var nextRequest = GrRequest(po, recvQty: 1m);
            nextRequest.TrxDate = FixedToday.AddDays(-1);
            var next = await receipt.SaveNewAsync(nextRequest);
            Assert.True(next.Succeeded, next.ErrorMessage);
            var archivedCnPost = await receipt.PostAsync([next.BatchNo]);
            Assert.False(archivedCnPost.Succeeded);
            Assert.Contains("credit/debit note", archivedCnPost.ErrorMessage,
                StringComparison.OrdinalIgnoreCase);
            await using (var verifyDb = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(23m, (await verifyDb.IvBalLocs.SingleAsync()).StdQty);
                Assert.Equal(8m, (await verifyDb.PoOrderDetails.SingleAsync()).RecvQty);
                Assert.Equal(IvBatchStatuses.New,
                    (await verifyDb.IvTrxBatches.SingleAsync(b =>
                        b.BatchNo == next.BatchNo)).BatchStatus);
                (await verifyDb.PoInvoices.SingleAsync(i => i.DocNo == cnDocNo))
                    .DeletedAtUtc = null;
                await verifyDb.SaveChangesAsync();
            }
            await using var db = await _factory.CreateDbContextAsync();
            var postedNote = await db.PoInvoices.AsNoTracking()
                .Include(i => i.Details).SingleAsync(i => i.DocNo == cnDocNo);
            var cnPrimaryId = await db.StockPostings.AsNoTracking()
                .Where(p => p.SourceModule == "PROCUREMENT"
                    && p.SourceDocumentType == "PO_INVOICE_CN"
                    && p.SourceDocumentNo == cnDocNo
                    && p.PostingRole == "PRIMARY")
                .Select(p => p.Id).SingleAsync();
            var adjustment = await db.PurchaseCostAdjustments.AsNoTracking()
                .SingleAsync(a =>
                a.StockPostingId == cnPrimaryId
                && a.SourceDocumentType == "PO_INVOICE_CN");
            await db.PurchaseCostAdjustments.Where(a => a.Id == adjustment.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(a => a.BaseQty,
                    adjustment.BaseQty + 1m));
            var mismatched = await new PurchaseReceiptCostSettlementService()
                .RollbackCreditNoteAsync(db, postedNote,
                    cnPrimaryId, cnPrimaryId, "TEST");
            Assert.False(mismatched.Succeeded);
            Assert.Contains("active costing generation", mismatched.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2m, (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            await db.PurchaseCostAdjustments.Where(a => a.Id == adjustment.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(a => a.BaseQty,
                    adjustment.BaseQty));
        }
        var postedCredit = (await invoiceService.GetAsync(cnDocNo)).Document!;
        var cnRollback = await invoiceService.RollbackAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = cnDocNo,
                RowVersion = postedCredit.RowVersion
            }]);
        Assert.True(cnRollback.Succeeded, cnRollback.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(3m,
                (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            Assert.Equal(3m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
            var rolledBackCredit = await db.PoInvoices.AsNoTracking()
                .Include(i => i.Details).SingleAsync(i => i.DocNo == cnDocNo);
            var cnPrimary = await db.StockPostings.AsNoTracking()
                .Where(p => p.SourceModule == "PROCUREMENT"
                    && p.SourceDocumentType == "PO_INVOICE_CN"
                    && p.SourceDocumentNo == cnDocNo
                    && p.PostingRole == "PRIMARY")
                .SingleAsync();
            rolledBackCredit.CostingRevision = cnPrimary.DocumentRevision;
            var stale = await new PurchaseReceiptCostSettlementService()
                .RollbackCreditNoteAsync(db, rolledBackCredit,
                    cnPrimary.Id, cnPrimary.Id, "TEST");
            Assert.False(stale.Succeeded);
            Assert.Contains("active costing generation", stale.Error,
                StringComparison.OrdinalIgnoreCase);
        }

        var grRollbackWithDraftCn = await receipt.RollbackAsync(
            [earlier.BatchNo]);
        Assert.True(grRollbackWithDraftCn.Succeeded,
            grRollbackWithDraftCn.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(18m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(3m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(3m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
        }
        var grRepostWithDraftCn = await receipt.PostAsync(
            [earlier.BatchNo]);
        Assert.True(grRepostWithDraftCn.Succeeded,
            grRepostWithDraftCn.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(23m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(8m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(3m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
        }

        var reopenedCredit = (await invoiceService.GetAsync(cnDocNo)).Document!;
        Assert.Equal(PoInvoiceStatuses.New, reopenedCredit.Status);
        var cnRepost = await invoiceService.PostAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = cnDocNo,
                RowVersion = reopenedCredit.RowVersion
            }]);
        Assert.True(cnRepost.Succeeded, cnRepost.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(2m,
                (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            Assert.Equal(2m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
        }
        var repostedCredit = (await invoiceService.GetAsync(cnDocNo)).Document!;
        var cnSecondRollback = await invoiceService.RollbackAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = cnDocNo,
                RowVersion = repostedCredit.RowVersion
            }]);
        Assert.True(cnSecondRollback.Succeeded,
            cnSecondRollback.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(3m,
                (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            Assert.Equal(3m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
        }

        var postedInvoice = (await invoiceService.GetAsync(piDocNo)).Document!;
        var piRollback = await invoiceService.RollbackAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = piDocNo,
                RowVersion = postedInvoice.RowVersion
            }]);
        Assert.True(piRollback.Succeeded, piRollback.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            Assert.Equal(0m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
            Assert.False(await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .AnyAsync(s => s.PiDocNo == piDocNo
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id)));
            Assert.Equal(23m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(costMethod switch
                {
                    StockCostMethods.Fifo => 135m,
                    StockCostMethods.Standard => 149.5m,
                    _ => 125m
                },
                (await db.StockCostStates.SingleAsync()).InventoryValue);
        }
        var grRollback = await receipt.RollbackAsync([earlier.BatchNo]);
        Assert.True(grRollback.Succeeded, grRollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(18m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(costMethod switch
            {
                StockCostMethods.Fifo => 85m,
                StockCostMethods.Standard => 117m,
                _ => 75m
            },
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(3m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);

        var reopenedInvoice = (await invoiceService.GetAsync(piDocNo)).Document!;
        Assert.Equal(PoInvoiceStatuses.New, reopenedInvoice.Status);
        var piRepost = await invoiceService.PostAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = piDocNo,
                RowVersion = reopenedInvoice.RowVersion
            }]);
        Assert.True(piRepost.Succeeded, piRepost.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(3m,
                (await db.PoOrderDetails.SingleAsync()).InvoicedQty);
            Assert.Equal(3m, SignedPiSettlementQty(await db
                .PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
            var currentRevision = await db.PoInvoices.AsNoTracking()
                .Where(i => i.DocNo == piDocNo)
                .Select(i => i.CostingRevision).SingleAsync();
            var active = await db.PurchaseReceiptCostSettlements.AsNoTracking()
                .Where(s => s.PiDocNo == piDocNo
                    && s.PiCostingRevision == currentRevision
                    && s.ReversesSettlementId == null
                    && !db.PurchaseReceiptCostSettlements.Any(r =>
                        r.ReversesSettlementId == s.Id)).SingleAsync();
            Assert.Equal(later.BatchNo, active.ReceiptBatchNo);
        }
        var repostedInvoice = (await invoiceService.GetAsync(piDocNo)).Document!;
        var piSecondRollback = await invoiceService.RollbackAsync(
            [new PoInvoiceKeyedRequest
            {
                DocNo = piDocNo,
                RowVersion = repostedInvoice.RowVersion
            }]);
        Assert.True(piSecondRollback.Succeeded,
            piSecondRollback.ErrorMessage);
        await using var final = await _factory.CreateDbContextAsync();
        Assert.Equal(0m,
            (await final.PoOrderDetails.SingleAsync()).InvoicedQty);
        Assert.Equal(0m, SignedPiSettlementQty(await final
            .PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(s => s.PiDocNo == piDocNo).ToArrayAsync()));
    }

    [Fact]
    public async Task Zero_variance_pi_remap_moves_active_allocation_to_earlier_gr_root()
    {
        await EnsureActiveStockLedgerAsync();
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        var receipt = CreateGrSut(withLedger: true);
        var earlier = await receipt.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(earlier.Succeeded, earlier.ErrorMessage);
        Assert.True((await receipt.PostAsync([earlier.BatchNo])).Succeeded);
        var later = await receipt.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await receipt.PostAsync([later.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var facts = await db.StockValuationFacts
            .Where(f => f.SourceDocumentType == IvTrxTypes.GoodsReceive)
            .OrderBy(f => f.StockPosting!.PostingSequence).ToArrayAsync();
        Assert.Equal(2, facts.Length);
        var invoice = new PoInvoice
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            DocNo = "PI-ZERO-REMAP", Type = PoInvoiceTypes.Invoice,
            Status = PoInvoiceStatuses.Posted, VendorCode = "VEND",
            DocDate = FixedToday, CurrRate = 1m,
            Details =
            [
                new PoInvoiceDetail
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    DocNo = "PI-ZERO-REMAP", Line = 1, ICode = "A100",
                    StdQty = 3m, NetAmount = 30m,
                    PoNo = po.PoNo, PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo
                }
            ]
        };
        db.PoInvoices.Add(invoice);
        var oldSettlement = new PurchaseReceiptCostSettlement
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            PiDocNo = invoice.DocNo, PiLineNo = 1,
            PoNo = po.PoNo, PoRelNo = po.PoRelNo,
            PoLineNo = po.PoLineNo, ItemCode = "A100",
            ReceiptValuationFactId = facts[1].Id,
            ReceiptInventoryHistoryId = facts[1].InventoryHistoryId,
            ReceiptBatchNo = later.BatchNo,
            SettledBaseQty = 3m,
            ReceiptCommercialUnitCost = 10m,
            ReceiptCommercialBaseAmount = 30m,
            ReceiptValuationUnitCost = 10m,
            ReceiptValuationBaseAmount = 30m,
            AllocatedActualBaseAmount = 30m,
            StockPostingId = facts[1].StockPostingId,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
        };
        db.PurchaseReceiptCostSettlements.Add(oldSettlement);
        var oldAdjustment = new PurchaseCostAdjustment
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            AdjustmentType = "PURCHASE_PRICE_VARIANCE",
            SourceDocumentType = "PO_INVOICE",
            SourceDocumentNo = invoice.DocNo,
            SourceDocumentLine = 1,
            PoNo = po.PoNo, PoRelNo = po.PoRelNo,
            PoLineNo = po.PoLineNo, ItemCode = "A100",
            CostMethod = StockCostMethods.MovingAverage,
            BaseQty = 3m, ActualBaseAmount = 30m,
            ReferenceBaseAmount = 30m,
            CommercialReferenceAmount = 30m,
            StockPostingId = facts[1].StockPostingId,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
        };
        db.PurchaseCostAdjustments.Add(oldAdjustment);
        await db.SaveChangesAsync();

        await using var tx = await db.Database.BeginTransactionAsync();
        var posting = await db.StockPostings.SingleAsync(p =>
            p.Id == facts[0].StockPostingId);
        posting.SealedAtUtc = null;
        await db.SaveChangesAsync();
        var root = new StockPostingContext(db,
            await db.StockLedgerEpochs.SingleAsync(), posting, "TEST");
        var result = await new PurchaseReceiptCostSettlementService()
            .RemapValueNeutralInvoiceAsync(root, invoice,
                new PurchaseCostAdjustmentPostingService(null!,
                    new StockValueAdjustmentWriter()));
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(facts[0].Id,
            Assert.Single(result.Settlements).ReceiptValuationFactId);
        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(s => s.PiDocNo == invoice.DocNo).ToArrayAsync();
        Assert.Equal(3, settlements.Length);
        Assert.Contains(settlements, s =>
            s.ReversesSettlementId == oldSettlement.Id);
        var adjustments = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(a => a.SourceDocumentNo == invoice.DocNo).ToArrayAsync();
        Assert.Equal(3, adjustments.Length);
        Assert.Contains(adjustments, a =>
            a.ReversesAdjustmentId == oldAdjustment.Id);

        await db.PoInvoiceDetails
            .Where(d => d.CompanyCode == "DEMO"
                && d.BranchCode == "HQ"
                && d.DocNo == invoice.DocNo && d.Line == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NetAmount, 33m));
        await Assert.ThrowsAsync<StockLedgerException>(() =>
            new PurchaseReceiptCostSettlementService()
                .RemapValueNeutralInvoiceAsync(root, invoice,
                    new PurchaseCostAdjustmentPostingService(null!,
                        new StockValueAdjustmentWriter())));
        await tx.RollbackAsync();
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Single(await restored.PurchaseReceiptCostSettlements
            .Where(s => s.PiDocNo == invoice.DocNo).ToArrayAsync());
        Assert.Single(await restored.PurchaseCostAdjustments
            .Where(a => a.SourceDocumentNo == invoice.DocNo).ToArrayAsync());
        Assert.NotNull((await restored.StockPostings.SingleAsync(p =>
            p.Id == facts[0].StockPostingId)).SealedAtUtc);
    }

    [Fact]
    public async Task Pi_rollback_reverses_active_replacement_settlement_generation()
    {
        await EnsureActiveStockLedgerAsync();
        var po = await CreatePoAsync(qty: 10m, unitPrice: 10m);
        var receipt = CreateGrSut(withLedger: true);
        var saved = await receipt.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True((await receipt.PostAsync([saved.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var fact = await db.StockValuationFacts.SingleAsync(f =>
            f.SourceDocumentType == IvTrxTypes.GoodsReceive);
        PurchaseReceiptCostSettlement Settlement(long? reversesId = null) => new()
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            PiDocNo = "PI-REMAP", PiLineNo = 1,
            PiCostingRevision = 0,
            PoNo = po.PoNo, PoRelNo = po.PoRelNo,
            PoLineNo = po.PoLineNo, ItemCode = "A100",
            ReceiptValuationFactId = fact.Id,
            ReceiptInventoryHistoryId = fact.InventoryHistoryId,
            ReceiptBatchNo = saved.BatchNo,
            SettledBaseQty = 2m,
            ReceiptCommercialUnitCost = 10m,
            ReceiptCommercialBaseAmount = 20m,
            ReceiptValuationUnitCost = 10m,
            ReceiptValuationBaseAmount = 20m,
            AllocatedActualBaseAmount = 20m,
            StockPostingId = fact.StockPostingId,
            ReversesSettlementId = reversesId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST"
        };
        var original = Settlement();
        db.PurchaseReceiptCostSettlements.Add(original);
        await db.SaveChangesAsync();
        db.PurchaseReceiptCostSettlements.Add(Settlement(original.Id));
        var replacement = Settlement();
        db.PurchaseReceiptCostSettlements.Add(replacement);
        await db.SaveChangesAsync();

        var invoice = new PoInvoice
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            DocNo = "PI-REMAP", Type = PoInvoiceTypes.Invoice,
            CostingRevision = 0
        };
        var missingAdjustment = await new PurchaseReceiptCostSettlementService()
            .ReverseInvoiceAsync(db, invoice, fact.StockPostingId, "TEST");
        Assert.False(missingAdjustment.Succeeded);
        Assert.Contains("adjustment evidence", missingAdjustment.Error,
            StringComparison.OrdinalIgnoreCase);

        PurchaseCostAdjustment Adjustment(long? reversesId = null) => new()
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            AdjustmentType = "PURCHASE_PRICE_VARIANCE",
            SourceDocumentType = "PO_INVOICE",
            SourceDocumentNo = "PI-REMAP",
            SourceDocumentLine = 1,
            SourceCostingRevision = 0,
            PoNo = po.PoNo, PoRelNo = po.PoRelNo,
            PoLineNo = po.PoLineNo, ItemCode = "A100",
            CostMethod = StockCostMethods.MovingAverage,
            BaseQty = 2m, ActualBaseAmount = 20m,
            ReferenceBaseAmount = 20m,
            TotalAdjustmentAmount = 0m,
            InventoryAdjustmentAmount = 0m,
            ConsumedVarianceAmount = 0m,
            StockPostingId = fact.StockPostingId,
            ReversesAdjustmentId = reversesId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST"
        };
        var originalAdjustment = Adjustment();
        db.PurchaseCostAdjustments.Add(originalAdjustment);
        await db.SaveChangesAsync();
        db.PurchaseCostAdjustments.Add(Adjustment(originalAdjustment.Id));
        var replacementAdjustment = Adjustment();
        db.PurchaseCostAdjustments.Add(replacementAdjustment);
        await db.SaveChangesAsync();

        await db.PurchaseCostAdjustments
            .Where(x => x.Id == replacementAdjustment.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BaseQty, 1m));
        var mismatchedAdjustment = await new PurchaseReceiptCostSettlementService()
            .ReverseInvoiceAsync(db, invoice, fact.StockPostingId, "TEST");
        Assert.False(mismatchedAdjustment.Succeeded);
        Assert.Contains("adjustment evidence", mismatchedAdjustment.Error,
            StringComparison.OrdinalIgnoreCase);
        await db.PurchaseCostAdjustments
            .Where(x => x.Id == replacementAdjustment.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BaseQty, 2m));

        invoice.Status = PoInvoiceStatuses.Posted;
        invoice.Details.Add(new PoInvoiceDetail
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            DocNo = invoice.DocNo, Line = 1, ICode = "A100",
            StdQty = 2m, NetAmount = 20m,
            PoNo = po.PoNo, PoRelNo = po.PoRelNo,
            PoLineNo = po.PoLineNo
        });
        var credit = new PoInvoice
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            DocNo = "CN-REMAP", Type = PoInvoiceTypes.CreditNote,
            InvNo = invoice.DocNo,
            Details =
            [
                new PoInvoiceDetail
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    DocNo = "CN-REMAP", Line = 1, ICode = "A100",
                    StdQty = 1m, ReferencedInvoiceLineNo = 1
                }
            ]
        };
        var creditReversal = await new PurchaseReceiptCostSettlementService()
            .ReverseCreditNoteAsync(db, credit, invoice,
                StockCostMethods.MovingAverage, fact.StockPostingId, "TEST");
        Assert.True(creditReversal.Succeeded, creditReversal.Error);
        Assert.Equal(replacement.Id,
            Assert.Single(creditReversal.Settlements).ReversesSettlementId);
        Assert.Equal(replacementAdjustment.Id,
            Assert.Single(creditReversal.Adjustments).ReversesAdjustmentId);
        db.ChangeTracker.Clear();

        var reversed = await new PurchaseReceiptCostSettlementService()
            .ReverseInvoiceAsync(db, invoice, fact.StockPostingId, "TEST");
        Assert.True(reversed.Succeeded, reversed.Error);
        var reversal = Assert.Single(reversed.Settlements);
        Assert.Equal(replacement.Id, reversal.ReversesSettlementId);
        Assert.NotEqual(original.Id, reversal.ReversesSettlementId);
        var adjustmentReversal = Assert.Single(reversed.Adjustments);
        Assert.Equal(replacementAdjustment.Id,
            adjustmentReversal.ReversesAdjustmentId);
        await db.SaveChangesAsync();
        var again = await new PurchaseReceiptCostSettlementService()
            .ReverseInvoiceAsync(db, invoice, fact.StockPostingId, "TEST");
        Assert.False(again.Succeeded);
    }

    [Fact]
    public async Task Ledger_post_rollback_repost_updates_status_and_RecvQty()
    {
        await EnsureActiveStockLedgerAsync();
        var po = await CreatePoAsync(qty: 10m, unitPrice: 3.50m);
        var sut = CreateGrSut(withLedger: true);
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded, "initial post");
        Assert.True((await sut.RollbackAsync([save.BatchNo])).Succeeded, "rollback");

        var repost = await sut.PostAsync([save.BatchNo]);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
        Assert.Equal(1, repost.SucceededCount);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Equal(IvBatchStatuses.Posted, batch.BatchStatus);
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(4m, detail.RecvQty);
        Assert.Equal(6m, detail.BalanceQty);
        Assert.True(await db.IvTrxBatchDetails.AnyAsync(x => x.BatchId == batch.Id && x.DocumentRevision > 0));
    }

    [Fact]
    public async Task Double_rollback_forbidden()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 2m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await sut.RollbackAsync([save.BatchNo])).Succeeded);

        var second = await sut.RollbackAsync([save.BatchNo]);
        Assert.False(second.Succeeded);
        Assert.Equal(0, second.SucceededCount);
        Assert.Equal(1, second.FailedCount);
    }

    [Fact]
    public async Task SearchPoLines_subtracts_NEW_draft_ToPurQty()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();

        var before = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(before.Succeeded, before.ErrorMessage);
        var beforeRow = Assert.Single(before.PoLines);
        Assert.Equal(10m, beforeRow.BalanceQty);
        Assert.Equal(0m, beforeRow.DraftQty);
        Assert.Equal(10m, beforeRow.AvailableQty);

        var draft = await sut.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        var during = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(during.Succeeded, during.ErrorMessage);
        var duringRow = Assert.Single(during.PoLines);
        Assert.Equal(10m, duringRow.BalanceQty);
        Assert.Equal(4m, duringRow.DraftQty);
        Assert.Equal(6m, duringRow.AvailableQty);

        Assert.True((await sut.DeleteAsync([draft.BatchNo])).Succeeded);

        var after = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(after.Succeeded, after.ErrorMessage);
        var afterRow = Assert.Single(after.PoLines);
        Assert.Equal(0m, afterRow.DraftQty);
        Assert.Equal(10m, afterRow.AvailableQty);
    }

    [Fact]
    public async Task Post_with_stale_FrPurQty_validates_locked_balance()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();

        var draft = await sut.SaveNewAsync(GrRequest(po, recvQty: 8m));
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batchDetail = await db.IvTrxBatchDetails.SingleAsync(x => x.BatchNo == draft.BatchNo);
            Assert.Equal(10m, batchDetail.FrPurQty);
        }

        var other = await sut.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(other.Succeeded, other.ErrorMessage);
        Assert.True((await sut.PostAsync([other.BatchNo])).Succeeded);

        var stalePost = await sut.PostAsync([draft.BatchNo]);
        Assert.False(stalePost.Succeeded);
        Assert.Equal(0, stalePost.SucceededCount);
        Assert.Equal(1, stalePost.FailedCount);
        Assert.Contains(stalePost.Posting!.Batches, b => !b.Succeeded && b.ErrorMessage!.Contains("exceeds allowed receive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveNew_uses_po_warehouse_over_item_def_warehouse()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "ALT",
                IsActive = true
            });
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "ALT",
                LocCode = "BINA",
                IsActive = true
            });
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "BIN1";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: "ALT");
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToLocation = "BINA",
                    ToRecvQty = 2m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var doc = await sut.GetAsync(save.BatchNo);
        Assert.True(doc.Succeeded, doc.ErrorMessage);
        var line = Assert.Single(doc.Document!.Lines);
        Assert.Equal("ALT", line.ToWarehouse);
        Assert.Equal("BINA", line.ToLocation);
    }

    [Fact]
    public async Task SaveNew_uses_item_def_warehouse_when_po_warehouse_blank()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "BIN1";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: null);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = Assert.Single((await sut.GetAsync(save.BatchNo)).Document!.Lines);
        Assert.Equal("MAIN", line.ToWarehouse);
        Assert.Equal("BIN1", line.ToLocation);
    }

    [Fact]
    public async Task SaveNew_requires_warehouse_when_po_and_def_invalid()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MISSING";
            item.DefLocation = null;
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: "GONE");
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("warehouse is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_ignores_def_location_belonging_to_other_warehouse()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WHB",
                IsActive = true
            });
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WHB",
                LocCode = "LOCB",
                IsActive = true
            });
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "LOCB";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: "MAIN");
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("location is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_allocates_blank_lot_and_preserves_manual_lot()
    {
        await EnsureLotItemAsync("LOT1");
        var po = await CreatePoForItemAsync("LOT1", qty: 10m);
        var sut = CreateGrSut();

        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 2m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(30)
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var autoLot = Assert.Single((await sut.GetAsync(save.BatchNo)).Document!.Lines).ToLotNo;
        Assert.Equal("260912001", autoLot);

        var po2 = await CreatePoForItemAsync("LOT1", qty: 5m);
        var manual = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po2.PoNo,
                    PoRelNo = po2.PoRelNo,
                    PoLineNo = po2.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = "SUP-LOT-9",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(10)
                }
            ]
        });
        Assert.True(manual.Succeeded, manual.ErrorMessage);
        Assert.Equal("SUP-LOT-9", Assert.Single((await sut.GetAsync(manual.BatchNo)).Document!.Lines).ToLotNo);
    }

    [Fact]
    public async Task SaveNew_two_blank_lots_same_item_are_unique()
    {
        await EnsureLotItemAsync("LOT2");
        var poA = await CreatePoForItemAsync("LOT2", qty: 5m);
        var poB = await CreatePoForItemAsync("LOT2", qty: 5m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = poA.PoNo,
                    PoRelNo = poA.PoRelNo,
                    PoLineNo = poA.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(5)
                },
                new IvGoodsReceiptLineRequest
                {
                    PoNo = poB.PoNo,
                    PoRelNo = poB.PoRelNo,
                    PoLineNo = poB.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(5)
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var lots = (await sut.GetAsync(save.BatchNo)).Document!.Lines.Select(x => x.ToLotNo).ToList();
        Assert.Equal(2, lots.Count);
        Assert.Equal(2, lots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("260912001", lots);
        Assert.Contains("260912002", lots);
    }

    [Fact]
    public async Task AllocateLot_skips_existing_IvLot_and_NEW_drafts_including_NG()
    {
        await EnsureLotItemAsync("LOT3");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLots.Add(new IvLot
            {
                CompanyCode = "DEMO",
                ICode = "LOT3",
                LotNo = "260912001",
                SourceType = IvTrxTypes.MiscellaneousReceipt,
                IsActive = true
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9001,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.NonStockGoodsReceive,
                BatchStatus = IvBatchStatuses.New,
                Details =
                [
                    new IvTrxBatchDetail
                    {
                        CompanyCode = "DEMO",
                        BranchCode = "HQ",
                        BatchNo = 9001,
                        TrxLineNo = 1,
                        TrxType = IvTrxTypes.NonStockGoodsReceive,
                        ICode = "LOT3",
                        ToLotNo = "260912002"
                    }
                ]
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9002,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.MiscellaneousReceipt,
                BatchStatus = IvBatchStatuses.New,
                Details =
                [
                    new IvTrxBatchDetail
                    {
                        CompanyCode = "DEMO",
                        BranchCode = "HQ",
                        BatchNo = 9002,
                        TrxLineNo = 1,
                        TrxType = IvTrxTypes.MiscellaneousReceipt,
                        ICode = "LOT3",
                        ToLotNo = "260912003"
                    }
                ]
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9003,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.GoodsReceive,
                BatchStatus = IvBatchStatuses.Posted,
                Details =
                [
                    new IvTrxBatchDetail
                    {
                        CompanyCode = "DEMO",
                        BranchCode = "HQ",
                        BatchNo = 9003,
                        TrxLineNo = 1,
                        TrxType = IvTrxTypes.GoodsReceive,
                        ICode = "LOT3",
                        ToLotNo = "260912004"
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateGrSut();
        var allocate = await sut.AllocateLotAsync("LOT3");
        Assert.True(allocate.Succeeded, allocate.ErrorMessage);
        Assert.Equal("260912004", allocate.AllocatedLotNo);
    }

    [Fact]
    public async Task SaveNew_Lot_None_AllowsBlankExpiry()
    {
        await EnsureLotItemAsync("LOT4_NONE", IvExpiryControlModes.None);
        var po = await CreatePoForItemAsync("LOT4_NONE", qty: 3m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = Assert.Single((await sut.GetAsync(save.BatchNo)).Document!.Lines);
        Assert.False(string.IsNullOrWhiteSpace(line.ToLotNo));
        Assert.Null(line.ExpiryDate);
        Assert.Equal(IvExpiryControlModes.None, line.ExpiryControl);
    }

    [Fact]
    public async Task SaveNew_Lot_Optional_AllowsBlankExpiry()
    {
        await EnsureLotItemAsync("LOT4_OPTIONAL", IvExpiryControlModes.Optional);
        var po = await CreatePoForItemAsync("LOT4_OPTIONAL", qty: 3m);
        var sut = CreateGrSut();

        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 1m));

        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = Assert.Single((await sut.GetAsync(save.BatchNo)).Document!.Lines);
        Assert.Null(line.ExpiryDate);
        Assert.Equal(IvExpiryControlModes.Optional, line.ExpiryControl);
    }

    [Fact]
    public async Task SaveNew_Lot_Optional_PersistsEnteredExpiry()
    {
        await EnsureLotItemAsync("LOT4_OPTIONAL_DATE", IvExpiryControlModes.Optional);
        var po = await CreatePoForItemAsync("LOT4_OPTIONAL_DATE", qty: 3m);
        var sut = CreateGrSut();
        var expiry = FixedToday.AddDays(10);

        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    ExpiryDate = expiry,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });

        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = Assert.Single((await sut.GetAsync(save.BatchNo)).Document!.Lines);
        Assert.Equal(expiry, line.ExpiryDate);
        Assert.Equal(IvExpiryControlModes.Optional, line.ExpiryControl);
    }

    [Fact]
    public async Task SaveNew_Lot_Required_RejectsBlankExpiry()
    {
        await EnsureLotItemAsync("LOT4_REQUIRED", IvExpiryControlModes.Required);
        var po = await CreatePoForItemAsync("LOT4_REQUIRED", qty: 3m);
        var sut = CreateGrSut();

        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 1m));

        Assert.False(save.Succeeded);
        Assert.Contains("expiry date is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_Lot_Required_AcceptsValidExpiry()
    {
        await EnsureLotItemAsync("LOT4_REQUIRED_DATE", IvExpiryControlModes.Required);
        var po = await CreatePoForItemAsync("LOT4_REQUIRED_DATE", qty: 3m);
        var sut = CreateGrSut();

        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    ExpiryDate = FixedToday.AddDays(1),
                    IStatus = IvItemStatuses.Active
                }
            ]
        });

        Assert.True(save.Succeeded, save.ErrorMessage);
    }

    [Fact]
    public async Task SaveNew_EnteredExpiryBeforeTransactionDate_Fails()
    {
        await EnsureLotItemAsync("LOT4_BEFORE", IvExpiryControlModes.Optional);
        var po = await CreatePoForItemAsync("LOT4_BEFORE", qty: 3m);
        var sut = CreateGrSut();

        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    ExpiryDate = FixedToday.AddDays(-1),
                    IStatus = IvItemStatuses.Active
                }
            ]
        });

        Assert.False(save.Succeeded);
        Assert.Contains("transaction date", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_BackdatedReceipt_ExpiryAfterReceiptButBeforeToday_Succeeds()
    {
        await EnsureLotItemAsync("LOT4_BACKDATED", IvExpiryControlModes.Required);
        var po = await CreatePoForItemAsync("LOT4_BACKDATED", qty: 3m);
        var sut = CreateGrSut();
        var receiptDate = FixedToday.AddDays(-7);
        var expiry = FixedToday.AddDays(-2);

        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = receiptDate,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    ExpiryDate = expiry,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });

        Assert.True(save.Succeeded, save.ErrorMessage);
    }

    [Fact]
    public async Task SaveNew_rejects_blank_po_line()
    {
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = "",
                    PoRelNo = 0,
                    PoLineNo = 0,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m
                }
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("PO line is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchPoLines_includes_def_warehouse_and_location()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "BIN1";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 2m);
        var sut = CreateGrSut();
        var rows = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(rows.Succeeded, rows.ErrorMessage);
        var row = Assert.Single(rows.PoLines);
        Assert.Equal("MAIN", row.DefWarehouse);
        Assert.Equal("BIN1", row.DefLocation);
    }

    [Fact]
    public async Task SearchPoLines_includes_expiry_control()
    {
        await EnsureLotItemAsync("LOT4_LOOKUP", IvExpiryControlModes.Optional);
        var po = await CreatePoForItemAsync("LOT4_LOOKUP", qty: 2m);
        var sut = CreateGrSut();

        var rows = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);

        Assert.True(rows.Succeeded, rows.ErrorMessage);
        var row = Assert.Single(rows.PoLines);
        Assert.True(row.LotControl);
        Assert.Equal(IvExpiryControlModes.Optional, row.ExpiryControl);
    }

    [Fact]
    public async Task SearchPoLines_includes_po_unit_price()
    {
        var po = await CreatePoAsync(qty: 2m, unitPrice: 3.50m);
        var sut = CreateGrSut();
        var rows = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(rows.Succeeded, rows.ErrorMessage);
        var row = Assert.Single(rows.PoLines);
        Assert.Equal(3.50m, row.UnitPrice);
    }

    private async Task EnsureLotItemAsync(
        string iCode,
        string expiryControl = IvExpiryControlModes.Required)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == iCode))
        {
            return;
        }

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = $"Lot item {iCode}",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 10m,
            PurchaseTaxGroup = "SR",
            StockControl = true,
            LotControl = true,
            ExpiryControl = expiryControl,
            IsActive = true,
            DefWarehouse = "MAIN",
            DefLocation = "BIN1"
        });
        db.PoVendorByItems.Add(new PoVendorByItem
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Vendor = "SUP01",
            ICode = iCode,
            PurUom = "EA",
            UnitPrice = 10m,
            Tolerance = 5m,
            OrdLevel = 0m,
            Status = "A",
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The reported warehouse scenario: a goods receipt is rolled back while a later issue depends on
    /// it. The current balance is positive, but replaying the later issue at the receipt's original
    /// position would go negative, so the rollback must still fail. The technical ledger message is
    /// preserved for logs; the posting result gains an operator explanation that names no ledger term.
    /// </summary>
    [Fact]
    public async Task Replay_negative_grn_rollback_still_fails_and_gains_operator_message()
    {
        await EnsureActiveStockLedgerAsync();

        // Opening balance: 15 EA @ 3.00 in the single pile.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                ICode = "A100",
                WhCode = "MAIN",
                LocCode = "BIN1",
                LotNo = string.Empty,
                IStatus = "ACTIVE",
                StdQty = 15m,
                StdUom = "EA",
                TransDate = FixedToday.AddDays(-5)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 15m, 3m);
        }

        var po = await CreatePoAsync(qty: 10m, unitPrice: 3m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var order = await db.PoOrders.SingleAsync(p => p.PoNo == po.PoNo);
            order.PoDate = FixedToday.AddDays(-3);
            await db.SaveChangesAsync();
        }

        var pileId = await ReadPileIdAsync();
        var evidenceReader = new CountingFailureEvidenceReader(new IvFailureEvidenceReader(_factory));
        var receipt = CreateGrSut(withLedger: true, enableBackdatedReplay: true,
            failureEvidence: evidenceReader);

        // Goods receipt dated earlier: balance 25.
        var grRequest = GrRequest(po, recvQty: 10m);
        grRequest.TrxDate = FixedToday.AddDays(-2);
        var saved = await receipt.SaveNewAsync(grRequest);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await receipt.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);

        // Later issue of 20: balance 5, still positive today.
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    FromBalLocId = pileId,
                    ICode = "A100",
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    FrLotNo = string.Empty,
                    Quantity = 20m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = 3m
                }
            ]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        var laterPosted = await issue.PostAsync([later.BatchNo]);
        Assert.True(laterPosted.Succeeded, laterPosted.ErrorMessage);

        decimal quantityBefore;
        decimal valueBefore;
        decimal poReceivedBefore;
        int historyBefore;
        int rollbackCountBefore;
        string statusBefore;
        await using (var snapshot = await _factory.CreateDbContextAsync())
        {
            quantityBefore = (await snapshot.IvBalLocs.SingleAsync()).StdQty;
            valueBefore = (await snapshot.StockCostStates.SingleAsync(s => s.ItemCode == "A100")).InventoryValue;
            poReceivedBefore = (await snapshot.PoOrderDetails.SingleAsync()).RecvQty;
            historyBefore = await snapshot.IvTrxHistories.CountAsync();
            var batch = await snapshot.IvTrxBatches.SingleAsync(b => b.BatchNo == saved.BatchNo);
            statusBefore = batch.BatchStatus;
            rollbackCountBefore = batch.RollbackCount;
        }
        Assert.Equal(5m, quantityBefore);
        // No explanation query ran for either successful posting.
        Assert.Equal(0, evidenceReader.Calls);

        var rollback = await receipt.RollbackAsync([saved.BatchNo]);

        Assert.False(rollback.Succeeded);

        // The original internal message is untouched, for logs and support.
        Assert.NotNull(rollback.ErrorMessage);
        Assert.Contains("negative", rollback.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event", rollback.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Operator text is added next to it; the technical text stays internal.
        Assert.NotNull(rollback.Posting);
        Assert.Equal(0, rollback.SucceededCount);
        Assert.Equal(1, rollback.FailedCount);
        var operatorError = Assert.Single(rollback.Posting!.OperatorErrors);
        Assert.Equal("INVENTORY.HISTORICAL_NEGATIVE_STOCK_REPLAYING", operatorError.PresentationCode);
        Assert.Equal($"Cannot roll back Goods Receipt {saved.BatchNo}.", operatorError.Title);
        Assert.Contains("historical", operatorError.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(operatorError.NextStep));
        Assert.False(operatorError.MayRetryAfterRefresh);
        Assert.False(string.IsNullOrWhiteSpace(operatorError.ReferenceId));

        var operatorText = operatorError.ToOperatorText();
        foreach (var forbidden in new[] { "slice", "pile", "event", "replaying", "epoch", "lineage", "rowversion" })
        {
            Assert.DoesNotContain(forbidden, operatorText, StringComparison.OrdinalIgnoreCase);
        }

        // The explanation was proven from the history row of the earlier issue, not invented.
        Assert.Equal(1, evidenceReader.Calls);
        var affected = Assert.Single(operatorError.Details, d => d.Label == "Affected transaction");
        Assert.Equal($"Miscellaneous Issue {later.BatchNo}, dated {FixedToday:dd/MM/yyyy}", affected.Value);
        Assert.Contains($"on {FixedToday:dd/MM/yyyy}", operatorError.Explanation, StringComparison.Ordinal);
        Assert.StartsWith("A100", Assert.Single(operatorError.Details, d => d.Label == "Item").Value);

        // Business outcome and stored state are exactly as before this change.
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(quantityBefore, (await after.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(valueBefore,
            (await after.StockCostStates.SingleAsync(s => s.ItemCode == "A100")).InventoryValue);
        Assert.Equal(poReceivedBefore, (await after.PoOrderDetails.SingleAsync()).RecvQty);
        Assert.Equal(historyBefore, await after.IvTrxHistories.CountAsync());
        var unchangedBatch = await after.IvTrxBatches.SingleAsync(b => b.BatchNo == saved.BatchNo);
        Assert.Equal(statusBefore, unchangedBatch.BatchStatus);
        Assert.Equal(rollbackCountBefore, unchangedBatch.RollbackCount);
    }

    [Fact]
    public async Task Replay_negative_rollback_caps_explanation_lookups_per_operation()
    {
        await EnsureActiveStockLedgerAsync();

        // Opening 0 EA: the four receipts below add stock that a later issue consumes entirely.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                ICode = "A100",
                WhCode = "MAIN",
                LocCode = "BIN1",
                LotNo = string.Empty,
                IStatus = "ACTIVE",
                StdQty = 0m,
                StdUom = "EA",
                TransDate = FixedToday.AddDays(-6)
            });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 0m, 3m);
        }

        var pileId = await ReadPileIdAsync();
        var evidenceReader = new CountingFailureEvidenceReader(new IvFailureEvidenceReader(_factory));
        var receipt = CreateGrSut(withLedger: true, enableBackdatedReplay: true,
            failureEvidence: evidenceReader);

        var receipts = new List<int>();
        for (var day = 4; day >= 1; day--)
        {
            var po = await CreatePoAsync(qty: 5m, unitPrice: 3m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var order = await db.PoOrders.SingleAsync(p => p.PoNo == po.PoNo);
                order.PoDate = FixedToday.AddDays(-day - 1);
                await db.SaveChangesAsync();
            }

            var request = GrRequest(po, recvQty: 5m);
            request.TrxDate = FixedToday.AddDays(-day);
            var saved = await receipt.SaveNewAsync(request);
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            Assert.True((await receipt.PostAsync([saved.BatchNo])).Succeeded);
            receipts.Add(saved.BatchNo);
        }

        // Later issue of all 20: today's balance is 0, so removing any receipt leaves negative stock.
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday, enableBackdatedReplay: true);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvMiscIssueLineRequest
                {
                    FromBalLocId = pileId,
                    ICode = "A100",
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    FrLotNo = string.Empty,
                    Quantity = 20m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = 3m
                }
            ]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        Assert.Equal(0, evidenceReader.Calls);

        var rollback = await receipt.RollbackAsync(receipts);

        Assert.False(rollback.Succeeded);
        Assert.Equal(0, rollback.SucceededCount);
        Assert.Equal(4, rollback.FailedCount);
        // One lookup per refused batch, but never more than the per-operation cap.
        Assert.Equal(3, evidenceReader.Calls);

        var coded = rollback.Posting!.Batches.Select(x => x.OperatorError).ToArray();
        Assert.All(coded, error => Assert.NotNull(error));
        Assert.Equal(3, coded.Count(error => error!.Details.Any(d => d.Label == "Affected transaction")));
        Assert.DoesNotContain(coded[3]!.Details, d => d.Label == "Affected transaction");
        Assert.Contains("historical", coded[3]!.Explanation, StringComparison.OrdinalIgnoreCase);

        // Refusing all four changed nothing.
        await using var after = await _factory.CreateDbContextAsync();
        Assert.Equal(0m, (await after.IvBalLocs.SingleAsync()).StdQty);
    }

    private async Task<(string PoNo, short PoRelNo, short PoLineNo)> CreatePoForItemAsync(string iCode, decimal qty)
        => await CreatePoAsync(qty, warehouse: "MAIN", iCode: iCode);

    private async Task<(string PoNo, short PoRelNo, short PoLineNo)> CreatePoAsync(
        decimal qty,
        string? warehouse = "MAIN",
        string iCode = "A100",
        decimal unitPrice = 0m,
        string currency = "MYR")
    {
        var poSut = CreatePoSut();
        var save = await poSut.SaveNewAsync(new PoOrderSaveRequest
        {
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = currency,
            Lines =
            [
                new PoOrderLineDto
                {
                    ICode = iCode,
                    PoPurQty = qty,
                    PoQty = qty,
                    PoUnitPrice = unitPrice,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    CurCode = currency,
                    TaxGroup = "SR",
                    ToWarehouse = warehouse
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        return (save.PoNo!, save.Document!.PoRelNo, save.Document.Lines[0].Line);
    }

    private static IvGoodsReceiptSaveRequest GrRequest(
        (string PoNo, short PoRelNo, short PoLineNo) po,
        decimal recvQty, string? lotNo = null) =>
        new()
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = lotNo,
                    ToRecvQty = recvQty
                }
            ]
        };

    private PoOrderService CreatePoSut()
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "MAIN");
        var attachRoot = Path.Combine(Path.GetTempPath(), "gr-po-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachRoot);
        var access = GrAccess();
        var attach = new PoOrderAttachmentService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = attachRoot }),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance);

        return new PoOrderService(
            _factory,
            tenant,
            access.Object,
            _poNumbering,
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attach,
            NullLogger<PoOrderService>.Instance);
    }

    private IvGoodsReceiptService CreateGrSut(bool withLedger = false,
        DateTime? postingToday = null, bool enableBackdatedReplay = false,
        IIvFailureEvidenceReader? failureEvidence = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "MAIN");
        var access = GrAccess();
        var postingRepo = new IvStockPostingRepository();
        IStockPostingCoordinator? coordinator = null;
        IIvInventoryHistoryWriter? historyWriter = null;
        if (withLedger)
        {
            historyWriter = new IvInventoryHistoryWriter();
            coordinator = new StockPostingCoordinator(
                _factory,
                tenant,
                new BranchStockTransactionLock(),
                new StockPeriodGuard(),
                new NoActiveStockFreezeGuard(),
                new InventoryValuationService());
        }

        var posting = new IvInventoryPostingService(
            _factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance,
            currentDate: new FixedCurrentDateService(postingToday ?? DateTime.MaxValue.Date),
            historyWriter: historyWriter,
            coordinator: coordinator,
            configuration: enableBackdatedReplay
                ? new ConfigurationManager
                {
                    ["OpenPeriodBackdatedReplayEnabled"] = "true"
                }
                : null,
            purchaseCostAdjustments: withLedger && enableBackdatedReplay
                ? new PurchaseCostAdjustmentPostingService(coordinator!,
                    new StockValueAdjustmentWriter())
                : null,
            failureEvidence: failureEvidence);

        return new IvGoodsReceiptService(
            _factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            NullLogger<IvGoodsReceiptService>.Instance);
    }

    private async Task AssertCreditNoteReplacementOwnerResolutionAsync(
        string cnDocNo, int grBatchNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var note = await db.PoInvoices.AsNoTracking().Include(i => i.Details)
            .SingleAsync(i => i.DocNo == cnDocNo);
        var owner = await db.StockPostings.AsNoTracking().SingleAsync(p =>
            p.SourceModule == "PROCUREMENT"
            && p.SourceDocumentType == "PO_INVOICE_CN"
            && p.SourceDocumentNo == cnDocNo
            && p.PostingRole == "PRIMARY");
        var oldSettlement = await db.PurchaseReceiptCostSettlements
            .AsNoTracking().SingleAsync(s => s.StockPostingId == owner.Id
                && s.ReversesSettlementId != null);
        var oldAdjustment = await db.PurchaseCostAdjustments
            .AsNoTracking().SingleAsync(a => a.StockPostingId == owner.Id
                && a.SourceDocumentType == "PO_INVOICE_CN"
                && a.ReversesAdjustmentId != null);
        var grOwner = await db.StockPostings.AsNoTracking().Where(p =>
            p.SourceModule == "INVENTORY"
            && p.SourceDocumentType == IvTrxTypes.GoodsReceive
            && p.SourceDocumentNo == grBatchNo.ToString()
            && p.PostingRole == "PRIMARY")
            .OrderByDescending(p => p.Id).FirstAsync();
        var replayRoot = (StockPosting)db.Entry(grOwner).CurrentValues.ToObject();
        replayRoot.Id = 0;
        replayRoot.RequestId = Guid.NewGuid();
        replayRoot.PostingSequence = await db.StockPostings.MaxAsync(p =>
            p.PostingSequence) + 1;
        replayRoot.PostingRole = "REVERSAL";
        replayRoot.ReversesPostingId = grOwner.Id;
        replayRoot.SealedAtUtc = DateTime.UtcNow;
        db.StockPostings.Add(replayRoot);
        await db.SaveChangesAsync();
        var run = new StockReplayRun
        {
            CompanyCode = note.CompanyCode,
            BranchCode = note.BranchCode,
            RootStockPostingId = replayRoot.Id,
            RootRequestId = replayRoot.RequestId,
            RootDocumentType = replayRoot.SourceDocumentType,
            RootDocumentId = replayRoot.SourceDocumentId,
            EarliestEffectiveAt = replayRoot.EffectiveAt,
            LatestEffectiveAt = replayRoot.EffectiveAt,
            PlanHash = new string('a', 64),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST"
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync();

        var oldSettlementReversal = (PurchaseReceiptCostSettlement)
            db.Entry(oldSettlement).CurrentValues.ToObject();
        oldSettlementReversal.Id = 0;
        oldSettlementReversal.StockPostingId = replayRoot.Id;
        oldSettlementReversal.ReversesSettlementId = oldSettlement.Id;
        db.PurchaseReceiptCostSettlements.Add(oldSettlementReversal);
        var oldAdjustmentReversal = (PurchaseCostAdjustment)
            db.Entry(oldAdjustment).CurrentValues.ToObject();
        oldAdjustmentReversal.Id = 0;
        oldAdjustmentReversal.StockPostingId = replayRoot.Id;
        oldAdjustmentReversal.ReversesAdjustmentId = oldAdjustment.Id;
        oldAdjustmentReversal.TotalAdjustmentAmount =
            -oldAdjustment.TotalAdjustmentAmount;
        oldAdjustmentReversal.InventoryAdjustmentAmount =
            -oldAdjustment.InventoryAdjustmentAmount;
        oldAdjustmentReversal.ConsumedVarianceAmount =
            -oldAdjustment.ConsumedVarianceAmount;
        db.PurchaseCostAdjustments.Add(oldAdjustmentReversal);
        await db.SaveChangesAsync();

        var oldPiSettlement = await db.PurchaseReceiptCostSettlements
            .AsNoTracking().SingleAsync(s =>
                s.Id == oldSettlement.ReversesSettlementId);
        var replacementPiSettlement = (PurchaseReceiptCostSettlement)
            db.Entry(oldPiSettlement).CurrentValues.ToObject();
        replacementPiSettlement.Id = 0;
        replacementPiSettlement.StockPostingId = replayRoot.Id;
        replacementPiSettlement.ReversesSettlementId = null;
        var oldPiAdjustment = await db.PurchaseCostAdjustments
            .AsNoTracking().SingleAsync(a =>
                a.Id == oldAdjustment.ReversesAdjustmentId);
        var replacementPiAdjustment = (PurchaseCostAdjustment)
            db.Entry(oldPiAdjustment).CurrentValues.ToObject();
        replacementPiAdjustment.Id = 0;
        replacementPiAdjustment.StockPostingId = replayRoot.Id;
        replacementPiAdjustment.ReversesAdjustmentId = null;
        db.PurchaseReceiptCostSettlements.Add(replacementPiSettlement);
        db.PurchaseCostAdjustments.Add(replacementPiAdjustment);
        await db.SaveChangesAsync();

        var replacementSettlement = (PurchaseReceiptCostSettlement)
            db.Entry(oldSettlement).CurrentValues.ToObject();
        replacementSettlement.Id = 0;
        replacementSettlement.StockPostingId = replayRoot.Id;
        replacementSettlement.ReversesSettlementId = replacementPiSettlement.Id;
        var replacementAdjustment = (PurchaseCostAdjustment)
            db.Entry(oldAdjustment).CurrentValues.ToObject();
        replacementAdjustment.Id = 0;
        replacementAdjustment.StockPostingId = replayRoot.Id;
        replacementAdjustment.ReversesAdjustmentId = replacementPiAdjustment.Id;
        db.PurchaseReceiptCostSettlements.Add(replacementSettlement);
        db.PurchaseCostAdjustments.Add(replacementAdjustment);
        await db.SaveChangesAsync();

        db.StockReplayEffects.Add(new StockReplayEffect
        {
            CompanyCode = note.CompanyCode,
            BranchCode = note.BranchCode,
            ReplayRunId = run.Id,
            RootStockPostingId = replayRoot.Id,
            BusinessOwnerPostingId = owner.Id,
            EffectKind = StockReplayEffectTypes.PurchaseCreditNoteCostReplacement,
            AffectedEntityType = StockReplayEffectTypes.PurchaseReceiptSettlement,
            AffectedEntityId = replacementSettlement.Id,
            OriginalEntityId = oldSettlement.Id,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new PurchaseReceiptCostSettlementService();
        var incomplete = await service.RollbackCreditNoteAsync(db, note,
            owner.Id, replayRoot.Id, "TEST");
        Assert.False(incomplete.Succeeded);
        Assert.Contains("replay ownership", incomplete.Error,
            StringComparison.OrdinalIgnoreCase);

        db.StockReplayEffects.Add(new StockReplayEffect
        {
            CompanyCode = note.CompanyCode,
            BranchCode = note.BranchCode,
            ReplayRunId = run.Id,
            RootStockPostingId = replayRoot.Id,
            BusinessOwnerPostingId = owner.Id,
            EffectKind = StockReplayEffectTypes.PurchaseCreditNoteCostReplacement,
            AffectedEntityType = StockReplayEffectTypes.PurchaseCostAdjustment,
            AffectedEntityId = replacementAdjustment.Id,
            OriginalEntityId = oldAdjustment.Id,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var resolved = await service.RollbackCreditNoteAsync(db, note,
            owner.Id, replayRoot.Id, "TEST");
        Assert.True(resolved.Succeeded, resolved.Error);
        Assert.Equal(replacementSettlement.Id,
            Assert.Single(resolved.Settlements).ReversesSettlementId);
        Assert.Equal(replacementAdjustment.Id,
            Assert.Single(resolved.Adjustments).ReversesAdjustmentId);
        db.ChangeTracker.Clear();

        var secondRoot = (StockPosting)db.Entry(grOwner).CurrentValues.ToObject();
        secondRoot.Id = 0;
        secondRoot.RequestId = Guid.NewGuid();
        secondRoot.PostingSequence = await db.StockPostings.MaxAsync(p =>
            p.PostingSequence) + 1;
        secondRoot.PostingRole = "PRIMARY";
        secondRoot.DocumentRevision = grOwner.DocumentRevision + 100;
        secondRoot.ReversesPostingId = null;
        secondRoot.SealedAtUtc = DateTime.UtcNow;
        db.StockPostings.Add(secondRoot);
        await db.SaveChangesAsync();
        var secondRun = new StockReplayRun
        {
            CompanyCode = note.CompanyCode,
            BranchCode = note.BranchCode,
            RootStockPostingId = secondRoot.Id,
            RootRequestId = secondRoot.RequestId,
            RootDocumentType = secondRoot.SourceDocumentType,
            RootDocumentId = secondRoot.SourceDocumentId,
            EarliestEffectiveAt = secondRoot.EffectiveAt,
            LatestEffectiveAt = secondRoot.EffectiveAt,
            PlanHash = new string('b', 64),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST"
        };
        db.StockReplayRuns.Add(secondRun);
        await db.SaveChangesAsync();

        var secondSettlementReversal = (PurchaseReceiptCostSettlement)
            db.Entry(replacementSettlement).CurrentValues.ToObject();
        secondSettlementReversal.Id = 0;
        secondSettlementReversal.StockPostingId = secondRoot.Id;
        secondSettlementReversal.ReversesSettlementId = replacementSettlement.Id;
        var secondAdjustmentReversal = (PurchaseCostAdjustment)
            db.Entry(replacementAdjustment).CurrentValues.ToObject();
        secondAdjustmentReversal.Id = 0;
        secondAdjustmentReversal.StockPostingId = secondRoot.Id;
        secondAdjustmentReversal.ReversesAdjustmentId = replacementAdjustment.Id;
        secondAdjustmentReversal.TotalAdjustmentAmount =
            -replacementAdjustment.TotalAdjustmentAmount;
        secondAdjustmentReversal.InventoryAdjustmentAmount =
            -replacementAdjustment.InventoryAdjustmentAmount;
        secondAdjustmentReversal.ConsumedVarianceAmount =
            -replacementAdjustment.ConsumedVarianceAmount;
        db.PurchaseReceiptCostSettlements.Add(secondSettlementReversal);
        db.PurchaseCostAdjustments.Add(secondAdjustmentReversal);
        await db.SaveChangesAsync();

        var secondPiSettlement = (PurchaseReceiptCostSettlement)
            db.Entry(replacementPiSettlement).CurrentValues.ToObject();
        secondPiSettlement.Id = 0;
        secondPiSettlement.StockPostingId = secondRoot.Id;
        secondPiSettlement.ReversesSettlementId = null;
        var secondPiAdjustment = (PurchaseCostAdjustment)
            db.Entry(replacementPiAdjustment).CurrentValues.ToObject();
        secondPiAdjustment.Id = 0;
        secondPiAdjustment.StockPostingId = secondRoot.Id;
        secondPiAdjustment.ReversesAdjustmentId = null;
        db.PurchaseReceiptCostSettlements.Add(secondPiSettlement);
        db.PurchaseCostAdjustments.Add(secondPiAdjustment);
        await db.SaveChangesAsync();

        var secondSettlement = (PurchaseReceiptCostSettlement)
            db.Entry(replacementSettlement).CurrentValues.ToObject();
        secondSettlement.Id = 0;
        secondSettlement.StockPostingId = secondRoot.Id;
        secondSettlement.ReversesSettlementId = secondPiSettlement.Id;
        var secondAdjustment = (PurchaseCostAdjustment)
            db.Entry(replacementAdjustment).CurrentValues.ToObject();
        secondAdjustment.Id = 0;
        secondAdjustment.StockPostingId = secondRoot.Id;
        secondAdjustment.ReversesAdjustmentId = secondPiAdjustment.Id;
        db.PurchaseReceiptCostSettlements.Add(secondSettlement);
        db.PurchaseCostAdjustments.Add(secondAdjustment);
        await db.SaveChangesAsync();

        db.StockReplayEffects.AddRange(
            new StockReplayEffect
            {
                CompanyCode = note.CompanyCode,
                BranchCode = note.BranchCode,
                ReplayRunId = secondRun.Id,
                RootStockPostingId = secondRoot.Id,
                BusinessOwnerPostingId = owner.Id,
                EffectKind = StockReplayEffectTypes.PurchaseCreditNoteCostReplacement,
                AffectedEntityType = StockReplayEffectTypes.PurchaseReceiptSettlement,
                AffectedEntityId = secondSettlement.Id,
                OriginalEntityId = replacementSettlement.Id,
                CreatedAtUtc = DateTime.UtcNow
            },
            new StockReplayEffect
            {
                CompanyCode = note.CompanyCode,
                BranchCode = note.BranchCode,
                ReplayRunId = secondRun.Id,
                RootStockPostingId = secondRoot.Id,
                BusinessOwnerPostingId = owner.Id,
                EffectKind = StockReplayEffectTypes.PurchaseCreditNoteCostReplacement,
                AffectedEntityType = StockReplayEffectTypes.PurchaseCostAdjustment,
                AffectedEntityId = secondAdjustment.Id,
                OriginalEntityId = replacementAdjustment.Id,
                CreatedAtUtc = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        var repeated = await service.RollbackCreditNoteAsync(db, note,
            owner.Id, secondRoot.Id, "TEST");
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Equal(secondSettlement.Id,
            Assert.Single(repeated.Settlements).ReversesSettlementId);
        Assert.Equal(secondAdjustment.Id,
            Assert.Single(repeated.Adjustments).ReversesAdjustmentId);
        await tx.RollbackAsync();
    }

    private PoInvoiceService CreatePiSut()
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            location: "MAIN");
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseInvoice,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var coordinator = new StockPostingCoordinator(
            _factory, tenant, new BranchStockTransactionLock(),
            new StockPeriodGuard(), new NoActiveStockFreezeGuard(),
            new InventoryValuationService());
        return new PoInvoiceService(
            _factory, tenant, access.Object,
            new FakePoInvoiceDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoInvoiceRepository(), new PoOrderRepository(),
            new PoCdnRepository(), Options.Create(new PoOrderOptions()),
            NullLogger<PoInvoiceService>.Instance,
            stockCoordinator: coordinator,
            costPostingCommands: new PurchaseCostPostingCommandFactory(),
            costAdjustments: new PurchaseCostAdjustmentPostingService(
                coordinator, new StockValueAdjustmentWriter()),
            receiptSettlements: new PurchaseReceiptCostSettlementService());
    }

    private static decimal SignedPiSettlementQty(
        IReadOnlyCollection<PurchaseReceiptCostSettlement> rows)
    {
        var byId = rows.ToDictionary(x => x.Id);
        decimal total = 0m;
        foreach (var row in rows)
        {
            var sign = 1m;
            var parentId = row.ReversesSettlementId;
            var seen = new HashSet<long> { row.Id };
            while (parentId is long id && byId.TryGetValue(id, out var parent)
                && seen.Add(id))
            {
                sign = -sign;
                parentId = parent.ReversesSettlementId;
            }
            total += sign * row.SettledBaseQty;
        }
        return total;
    }

    private async Task EnsureActiveStockLedgerAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (!await db.StockLedgerEpochs.AnyAsync(x =>
                x.CompanyCode == "DEMO"
                && x.BranchCode == "HQ"
                && x.Status == StockLedgerEpochStatuses.Active))
        {
            db.StockLedgerEpochs.Add(new StockLedgerEpoch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                EffectiveFrom = new DateTime(2026, 1, 1),
                Version = 2,
                Status = StockLedgerEpochStatuses.Active,
                MigrationBatchId = Guid.NewGuid(),
                ReconciliationManifestHash = new string('A', 64)
            });
        }

        if (!await db.StockCostPolicyRevisions.AnyAsync(x =>
                x.CompanyCode == "DEMO"
                && x.BranchCode == "HQ"
                && x.Status == StockCostPolicyStatuses.Active))
        {
            db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                CostMethod = StockCostMethods.MovingAverage,
                EffectiveFrom = new DateTime(2026, 1, 1),
                Status = StockCostPolicyStatuses.Active,
                ApprovedBy = "TEST",
                ApprovedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "TEST"
            });
        }

        await db.SaveChangesAsync();
    }

    private static Mock<IAccessRightService> GrAccess()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PurchaseOrder,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryGoodsReceipt,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
