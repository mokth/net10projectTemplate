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
    }

    private async Task<int> ReadPileIdAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.IvBalLocs.Select(b => b.Id).SingleAsync();
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
        var rollback = await receipt.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(28m, (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
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

        var invoice = new PoInvoice
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            DocNo = "PI-REMAP", Type = PoInvoiceTypes.Invoice,
            CostingRevision = 0
        };
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
        decimal recvQty) =>
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
        DateTime? postingToday = null, bool enableBackdatedReplay = false)
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
                : null);

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
