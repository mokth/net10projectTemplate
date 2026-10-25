using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryVendorReturn)]
public sealed class IvVendorReturnLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal PoCommercialPrice = 9.5m;
    private const decimal BalanceReferencePrice = 11m;
    private const string PoNo = "PO-VR-1";
    private const short PoRelNo = 1;
    private const short PoLineNo = 1;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvVendorReturnLedgerAuthorityTests()
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
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            UomCode = "EA",
            IsActive = true
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IClassCode = "RAW",
            IsActive = true
        });
        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IStatus = "ACTIVE",
            IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = PurchasePrice
        });
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            PoNo = PoNo,
            PoRelNo = PoRelNo,
            PoDate = FixedToday,
            Status = PoOrderStatuses.Received,
            VendCode = "SUP01",
            VendName = "Supplier 1",
            CurCode = InventoryLedgerTestFixture.BaseCurrency,
            OneTime = false,
            FinClosed = false
        });
        db.PoOrderDetails.Add(new PoOrderDetail
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            PoNo = PoNo,
            PoRelNo = PoRelNo,
            Line = PoLineNo,
            ICode = "A100",
            IDesc = "Stock item",
            OneTime = false,
            PoQty = 1000m,
            PoPurQty = 1000m,
            RecvQty = 1000m,
            ReturnQty = 0m,
            BalanceQty = 0m,
            OverRecvQty = 0m,
            InvoicedQty = 0m,
            PoUnitPrice = PoCommercialPrice,
            PackSz = 1m,
            StdUom = "EA",
            PurchaseUom = "EA"
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Moving_average_return_ignores_request_and_po_price()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(BalanceReferencePrice, detail.UnitPrice);
        }

        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "VENDOR_RETURN_OUT");
        Assert.Equal(-1, fact.Direction);
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        Assert.NotEqual(PoCommercialPrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(90m, state.OnHandBaseQty);
        Assert.Equal(180m, state.InventoryValue);
        var poLine = await verify.PoOrderDetails.SingleAsync();
        Assert.Equal(10m, poLine.ReturnQty);
        Assert.Equal(PoCommercialPrice, poLine.PoUnitPrice);
    }

    [Fact]
    public async Task Backdated_wac_return_updates_stock_and_po_on_post_and_rollback()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 15m, 3m);
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var service = InventoryLedgerTestFixture.CreateVendorReturn(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(balId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var saved = await service.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var exhaustPo = await _factory.CreateDbContextAsync())
        {
            var poLine = await exhaustPo.PoOrderDetails.SingleAsync();
            poLine.ReturnQty = poLine.RecvQty;
            await exhaustPo.SaveChangesAsync();
        }
        var insufficientPo = await service.PostAsync([saved.BatchNo]);
        Assert.False(insufficientPo.Succeeded);
        await using (var afterRejectedPost = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7m, (await afterRejectedPost.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(21m,
                (await afterRejectedPost.StockCostStates.SingleAsync()).InventoryValue);
            Assert.DoesNotContain(await afterRejectedPost.IvTrxHistories.ToArrayAsync(),
                h => h.BatchNo == saved.BatchNo);
            var poLine = await afterRejectedPost.PoOrderDetails.SingleAsync();
            Assert.Equal(poLine.RecvQty, poLine.ReturnQty);
            poLine.ReturnQty = 0m;
            await afterRejectedPost.SaveChangesAsync();
        }
        var posted = await service.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var afterPost = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await afterPost.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(18m,
                (await afterPost.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(1m,
                (await afterPost.PoOrderDetails.SingleAsync()).ReturnQty);
            var history = await afterPost.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(PoNo, history.PoNo);
            Assert.Equal(PoRelNo, history.PoRelNo);
            Assert.Equal(PoLineNo, history.PoLineNo);
            var fact = await afterPost.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == history.Id);
            Assert.Equal("VENDOR_RETURN_OUT", fact.MovementCode);
            Assert.Equal(3m, fact.CostAmount);
        }

        await using (var tamper = await _factory.CreateDbContextAsync())
        {
            var detail = await tamper.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.PoLineNo = 2;
            await tamper.SaveChangesAsync();
        }
        var rejected = await service.RollbackAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        await using (var restoreDetail = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await restoreDetail.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(1m,
                (await restoreDetail.PoOrderDetails.SingleAsync()).ReturnQty);
            var detail = await restoreDetail.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.PoLineNo = PoLineNo;
            detail.FrPurQty = 2m;
            await restoreDetail.SaveChangesAsync();
        }
        var wrongPurchaseQty = await service.RollbackAsync([saved.BatchNo]);
        Assert.False(wrongPurchaseQty.Succeeded);
        await using (var restoreQuantity = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await restoreQuantity.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(1m,
                (await restoreQuantity.PoOrderDetails.SingleAsync()).ReturnQty);
            var detail = await restoreQuantity.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.FrPurQty = 1m;
            await restoreQuantity.SaveChangesAsync();
        }
        var rollback = await service.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(21m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).ReturnQty);
    }

    [Fact]
    public async Task Fifo_backdated_return_posts_and_rolls_back_before_later_issue()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [new FifoLayerSeed(15m, 3m, FixedToday.AddDays(-5))]);
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var service = InventoryLedgerTestFixture.CreateVendorReturn(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(balId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var saved = await service.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var poLine = await db.PoOrderDetails.SingleAsync();
            poLine.ReturnQty = poLine.RecvQty;
            await db.SaveChangesAsync();
        }
        var insufficientPo = await service.PostAsync([saved.BatchNo]);
        Assert.False(insufficientPo.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.False(await db.IvTrxHistories.AnyAsync(h =>
                h.BatchNo == saved.BatchNo));
            var poLine = await db.PoOrderDetails.SingleAsync();
            Assert.Equal(poLine.RecvQty, poLine.ReturnQty);
            poLine.ReturnQty = 0m;
            await db.SaveChangesAsync();
        }
        var posted = await service.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(18m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(1m, (await db.PoOrderDetails.SingleAsync()).ReturnQty);
            var history = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(PoNo, history.PoNo);
            Assert.Equal(PoRelNo, history.PoRelNo);
            Assert.Equal(PoLineNo, history.PoLineNo);
        }
        var rollback = await service.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(21m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).ReturnQty);
    }

    [Fact]
    public async Task Fifo_backdated_return_reprices_later_issue_and_restores_it_on_rollback()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 2m, FixedToday.AddDays(-6)),
                new FifoLayerSeed(10m, 4m, FixedToday.AddDays(-5))
            ]);
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var service = InventoryLedgerTestFixture.CreateVendorReturn(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(balId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var saved = await service.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        var posted = await service.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(24m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(1m, (await db.PoOrderDetails.SingleAsync()).ReturnQty);
            var issueHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
            var activeIssueFacts = await db.StockValuationFacts
                .Where(f => f.InventoryHistoryId == issueHistory.Id
                    && f.ReversesValuationFactId == null
                    && !db.StockValuationFacts.Any(r =>
                        r.ReversesValuationFactId == f.Id))
                .ToArrayAsync();
            Assert.Equal(24m, activeIssueFacts.Sum(f => f.CostAmount));
        }
        var rollback = await service.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(28m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).ReturnQty);
        var restoredIssueHistory = await restored.IvTrxHistories.SingleAsync(h =>
            h.BatchNo == later.BatchNo && h.EntryRole == "ORIGINAL");
        var restoredIssueFacts = await restored.StockValuationFacts
            .Where(f => f.InventoryHistoryId == restoredIssueHistory.Id
                && f.ReversesValuationFactId == null
                && !restored.StockValuationFacts.Any(r =>
                    r.ReversesValuationFactId == f.Id))
            .ToArrayAsync();
        Assert.Equal(22m, restoredIssueFacts.Sum(f => f.CostAmount));
    }

    [Fact]
    public async Task Fifo_return_rollback_rejects_changed_po_line_without_mutation()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
                [new FifoLayerSeed(15m, 3m, FixedToday.AddDays(-5))]);
        }
        var service = InventoryLedgerTestFixture.CreateVendorReturn(
            _factory, FixedToday.AddDays(1), enableBackdatedReplay: true);
        var request = Request(balId, 1m);
        request.TrxDate = FixedToday;
        var saved = await service.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True((await service.PostAsync([saved.BatchNo])).Succeeded);

        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday.AddDays(1));
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday.AddDays(1),
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.PoLineNo = 2;
            await db.SaveChangesAsync();
        }
        var rejected = await service.RollbackAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(1m, (await db.PoOrderDetails.SingleAsync()).ReturnQty);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b =>
                    b.BatchNo == saved.BatchNo)).BatchStatus);
            var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.PoLineNo = PoLineNo;
            await db.SaveChangesAsync();
            var history = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(history.PoNo, detail.PoNo);
            Assert.Equal(history.PoRelNo, detail.PoRelNo);
            Assert.Equal(history.PoLineNo, detail.PoLineNo);
            Assert.Equal(history.FrPurQty, detail.FrPurQty);
            Assert.Equal(history.FrPurUom, detail.FrPurUom);
        }
        var rollback = await service.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
    }

    [Fact]
    public async Task Standard_backdated_return_posts_and_rolls_back_before_later_issue()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 3m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 15m, 3m, StockCostMethods.Standard);
        }
        var issue = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);

        var service = InventoryLedgerTestFixture.CreateVendorReturn(
            _factory, FixedToday, enableBackdatedReplay: true);
        var request = Request(balId, 1m);
        request.TrxDate = FixedToday.AddDays(-1);
        var saved = await service.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var poLine = await db.PoOrderDetails.SingleAsync();
            poLine.ReturnQty = poLine.RecvQty;
            await db.SaveChangesAsync();
        }
        var insufficientPo = await service.PostAsync([saved.BatchNo]);
        Assert.False(insufficientPo.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(7m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(21m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.False(await db.IvTrxHistories.AnyAsync(h =>
                h.BatchNo == saved.BatchNo));
            var poLine = await db.PoOrderDetails.SingleAsync();
            Assert.Equal(poLine.RecvQty, poLine.ReturnQty);
            poLine.ReturnQty = 0m;
            await db.SaveChangesAsync();
        }
        var posted = await service.PostAsync([saved.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(18m, (await db.StockCostStates.SingleAsync()).InventoryValue);
            Assert.Equal(1m, (await db.PoOrderDetails.SingleAsync()).ReturnQty);
            var history = await db.IvTrxHistories.SingleAsync(h =>
                h.BatchNo == saved.BatchNo && h.EntryRole == "ORIGINAL");
            Assert.Equal(PoNo, history.PoNo);
            Assert.Equal(PoRelNo, history.PoRelNo);
            Assert.Equal(PoLineNo, history.PoLineNo);
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.PoLineNo = 2;
            await db.SaveChangesAsync();
        }
        var rejected = await service.RollbackAsync([saved.BatchNo]);
        Assert.False(rejected.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(6m, (await db.IvBalLocs.SingleAsync()).StdQty);
            Assert.Equal(1m, (await db.PoOrderDetails.SingleAsync()).ReturnQty);
            var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == saved.BatchNo);
            detail.PoLineNo = PoLineNo;
            await db.SaveChangesAsync();
        }
        var rollback = await service.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var restored = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await restored.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(21m,
            (await restored.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).ReturnQty);
    }

    [Fact]
    public async Task Wac_return_rollback_ignores_a_later_issue_already_reversed()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db,
                "A100", 15m, 3m);
        }
        var service = InventoryLedgerTestFixture.CreateVendorReturn(
            _factory, FixedToday.AddDays(1), enableBackdatedReplay: true);
        var saved = await service.SaveNewAsync(Request(balId, 1m));
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True((await service.PostAsync([saved.BatchNo])).Succeeded);

        var issue = InventoryLedgerTestFixture.CreateMiscIssue(
            _factory, FixedToday.AddDays(1));
        var later = await issue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday.AddDays(1),
            Lines = [new IvMiscIssueLineRequest
            {
                FromBalLocId = balId, ICode = "A100",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                FrLotNo = string.Empty, Quantity = 8m, Uom = "EA",
                IClassCode = "RAW", IStatus = "ACTIVE", UnitPrice = 3m
            }]
        });
        Assert.True(later.Succeeded, later.ErrorMessage);
        Assert.True((await issue.PostAsync([later.BatchNo])).Succeeded);
        Assert.True((await issue.RollbackAsync([later.BatchNo])).Succeeded);

        var rollback = await service.RollbackAsync([saved.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(15m, (await verify.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(45m, (await verify.StockCostStates.SingleAsync()).InventoryValue);
        Assert.Equal(0m, (await verify.PoOrderDetails.SingleAsync()).ReturnQty);
    }

    [Fact]
    public async Task Fifo_return_consumes_layers_and_ignores_request_price()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts
            .Where(x => x.MovementCode == "VENDOR_RETURN_OUT")
            .OrderBy(x => x.SplitOrdinal)
            .ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(27m, facts.Sum(x => x.CostAmount));
        Assert.All(facts, fact =>
        {
            Assert.NotEqual(TamperPrice, fact.UnitCost);
            Assert.NotEqual(PurchasePrice, fact.UnitCost);
            Assert.NotEqual(PoCommercialPrice, fact.UnitCost);
            Assert.Equal(StockValuationSources.Fifo, fact.ValuationSource);
        });
        Assert.Equal(8m, await verify.PoOrderDetails.Select(x => x.ReturnQty).SingleAsync());
    }

    [Fact]
    public async Task Standard_return_uses_effective_standard_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "VENDOR_RETURN_OUT");
        Assert.Equal(6.5m, fact.UnitCost);
        Assert.Equal(65m, fact.CostAmount);
        Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PoCommercialPrice, fact.UnitCost);
    }

    [Fact]
    public async Task Rollback_restores_stock_and_po_return_qty()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await service.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0m, await verify.PoOrderDetails.Select(x => x.ReturnQty).SingleAsync());
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
        Assert.Equal(StockValuationSources.OriginalReversal, facts[1].ValuationSource);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(200m, state.InventoryValue);
    }

    [Fact]
    public async Task Purchase_credit_note_owned_return_cannot_be_posted()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m, "PCN/PCN2609-0001"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await service.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("purchase credit note", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await verify.StockValuationFacts.CountAsync(x => x.MovementCode == "VENDOR_RETURN_OUT"));
        Assert.Equal(0m, await verify.PoOrderDetails.Select(x => x.ReturnQty).SingleAsync());
    }

    private async Task<int> SeedBalanceAsync(decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            UnitPrice = BalanceReferencePrice,
            TransDate = FixedToday
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static IvVendorReturnSaveRequest Request(int fromBalLocId, decimal qty, string? refNo = null) =>
        new()
        {
            TrxDate = FixedToday,
            RefNo = refNo,
            Lines =
            [
                new IvVendorReturnLineRequest
                {
                    FromBalLocId = fromBalLocId,
                    ICode = "A100",
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    FrLotNo = string.Empty,
                    Quantity = qty,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = TamperPrice,
                    PoNo = PoNo,
                    PoRelNo = PoRelNo,
                    PoLineNo = PoLineNo
                }
            ]
        };
}
