using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Inventory.Transaction;
/// <summary>
/// Business-logic posting/rollback tests (SQLite). Does not prove SQL Server UPDLOCK/HOLDLOCK.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPosting)]
public class IvInventoryPostingServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvInventoryPostingServiceTests()
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
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A100",
                IDesc = "Stock item",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = true,
                LotControl = false,
                IsActive = true
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "LOT1",
                IDesc = "Lot item",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = true,
                LotControl = true,
                ExpiryControl = IvExpiryControlModes.Required,
                IsActive = true
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "NS01",
                IDesc = "Non-stock",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = false,
                LotControl = false,
                IsActive = true
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
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Post_stock_controlled_creates_bal_history_and_posts()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(Request(100m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await mr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Equal(IvBatchStatuses.Posted, batch.BatchStatus);
        Assert.NotNull(batch.PostedDate);
        Assert.Equal(1, batch.PostedCount);
        Assert.NotNull(batch.PostingOperationId);

        var bal = Assert.Single(await db.IvBalLocs.ToListAsync());
        Assert.Equal(100m, bal.StdQty);
        Assert.Equal("SITE", bal.LocationCode);
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
        await AssertInvariantAsync(db, bal.Id);
    }

    [Fact]
    public async Task Post_revalidates_a_named_destination_bin_after_it_is_deactivated()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(Request(10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var location = await db.IvLocations.SingleAsync(x => x.LocCode == "BIN1");
            location.IsActive = false;
            await db.SaveChangesAsync();
        }

        var post = await mr.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("inactive", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.New, await verify.IvTrxBatches
            .Where(x => x.BatchNo == save.BatchNo)
            .Select(x => x.BatchStatus)
            .SingleAsync());
        Assert.Empty(await verify.IvBalLocs.ToListAsync());
        Assert.Empty(await verify.IvTrxHistories.ToListAsync());
    }

    [Fact]
    public async Task Post_same_slice_lines_aggregate_to_one_bal_three_history()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            Lines =
            [
                Line("A100", 10m),
                Line("A100", 20m),
                Line("A100", 30m)
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await mr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var bal = Assert.Single(await db.IvBalLocs.ToListAsync());
        Assert.Equal(60m, bal.StdQty);
        Assert.Equal(3, await db.IvTrxHistories.CountAsync());
        await AssertInvariantAsync(db, bal.Id);
    }

    [Fact]
    public async Task Post_twice_fails_qty_unchanged()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(Request(50m));
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        var second = await mr.PostAsync([save.BatchNo]);
        Assert.False(second.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(50m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Rollback_restores_qty_deletes_history_returns_new()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(Request(100m));
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        var rb = await mr.RollbackAsync([save.BatchNo]);
        Assert.True(rb.Succeeded, rb.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Equal(1, batch.RollbackCount);
        Assert.NotNull(batch.RollbackOperationId);
        Assert.Equal(0m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        var detail = await db.IvTrxBatchDetails.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Null(detail.ToBalLocId);
        Assert.Null(detail.ToLotId);
    }

    [Fact]
    public async Task Rollback_negative_fails_atomically()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(Request(100m));
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync();
            bal.StdQty = 20m;
            await db.SaveChangesAsync();
        }

        var rb = await mr.RollbackAsync([save.BatchNo]);
        Assert.False(rb.Succeeded);
        Assert.Contains("negative", rb.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.Posted, await verify.IvTrxBatches.Select(x => x.BatchStatus).SingleAsync());
        Assert.Equal(20m, await verify.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await verify.IvTrxHistories.CountAsync());
        Assert.Equal(0, await verify.IvTrxBatches.Select(x => x.RollbackCount).SingleAsync());
    }

    [Fact]
    public async Task Posted_document_cannot_be_edited_or_deleted()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(Request(10m));
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        var update = await mr.UpdateAsync(save.BatchNo, Request(99m));
        Assert.False(update.Succeeded);
        Assert.Contains("NEW", update.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var delete = await mr.DeleteAsync([save.BatchNo]);
        Assert.False(delete.Succeeded);
    }

    [Fact]
    public async Task Non_stock_writes_history_only()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            Lines = [Line("NS01", 5m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        var hist = Assert.Single(await db.IvTrxHistories.ToListAsync());
        Assert.Null(hist.ToBalLocId);

        Assert.True((await mr.RollbackAsync([save.BatchNo])).Succeeded);
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Lot_controlled_creates_and_reuses_lot()
    {
        var mr = CreateMr();
        var req = new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = "LOT1",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = "L-001",
                    Quantity = 7m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    ExpiryDate = FixedToday.AddDays(30),
                    Reason = "FOUND"
                }
            ]
        };
        var save1 = await mr.SaveNewAsync(req);
        Assert.True((await mr.PostAsync([save1.BatchNo])).Succeeded);

        var save2 = await mr.SaveNewAsync(req);
        Assert.True((await mr.PostAsync([save2.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var lot = Assert.Single(await db.IvLots.ToListAsync());
        Assert.Equal("L-001", lot.LotNo);
        Assert.Equal(IvTrxTypes.MiscellaneousReceipt, lot.SourceType);
        Assert.Equal("SUP01", lot.SupplierCode);
        Assert.Equal(14m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task PostGoodsReceipt_LotNone_BlankExpiry_Succeeds()
    {
        await SetLot1ExpiryControlAsync(IvExpiryControlModes.None);
        var batchNo = await CreateGoodsReceiptDraftAsync(expiry: null);

        var result = await CreatePosting().PostAsync(IvTrxTypes.GoodsReceive, [batchNo]);

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var lot = Assert.Single(await db.IvLots.ToListAsync());
        Assert.Null(lot.ExpiryDate);
        Assert.Equal(3m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task PostGoodsReceipt_LotOptional_BlankExpiry_Succeeds()
    {
        await SetLot1ExpiryControlAsync(IvExpiryControlModes.Optional);
        var batchNo = await CreateGoodsReceiptDraftAsync(expiry: null);

        var result = await CreatePosting().PostAsync(IvTrxTypes.GoodsReceive, [batchNo]);

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var lot = Assert.Single(await db.IvLots.ToListAsync());
        Assert.Null(lot.ExpiryDate);
        Assert.Equal(3m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task PostGoodsReceipt_LotRequired_BlankExpiry_FailsClosed()
    {
        await SetLot1ExpiryControlAsync(IvExpiryControlModes.Required);
        var batchNo = await CreateGoodsReceiptDraftAsync(expiry: null);

        var result = await CreatePosting().PostAsync(IvTrxTypes.GoodsReceive, [batchNo]);

        Assert.False(result.Succeeded);
        Assert.Contains("expiry date is required", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.IvLots.ToListAsync());
        Assert.Empty(await db.IvBalLocs.ToListAsync());
    }

    [Fact]
    public async Task PostGoodsReceipt_EnteredExpiryBeforeBatchDate_FailsClosed()
    {
        await SetLot1ExpiryControlAsync(IvExpiryControlModes.Optional);
        var batchNo = await CreateGoodsReceiptDraftAsync(expiry: FixedToday.AddDays(-1));

        var result = await CreatePosting().PostAsync(IvTrxTypes.GoodsReceive, [batchNo]);

        Assert.False(result.Succeeded);
        Assert.Contains("transaction date", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.IvLots.ToListAsync());
        Assert.Empty(await db.IvBalLocs.ToListAsync());
    }

    [Fact]
    public async Task RollbackGoodsReceipt_WithNullLotExpiry_Succeeds()
    {
        await SetLot1ExpiryControlAsync(IvExpiryControlModes.None);
        var batchNo = await CreateGoodsReceiptDraftAsync(expiry: null);
        var posting = CreatePosting();

        Assert.True((await posting.PostAsync(IvTrxTypes.GoodsReceive, [batchNo])).Succeeded);
        var rollback = await posting.RollbackAsync(IvTrxTypes.GoodsReceive, [batchNo]);

        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.New, await db.IvTrxBatches
            .Where(x => x.BatchNo == batchNo)
            .Select(x => x.BatchStatus)
            .SingleAsync());
        Assert.Equal(0m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0m, await db.PoOrderDetails.Select(x => x.RecvQty).SingleAsync());
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
        Assert.Null((await db.IvLots.SingleAsync()).ExpiryDate);
    }

    [Fact]
    public async Task Max_selection_rejected()
    {
        var mr = CreateMr();
        var nos = Enumerable.Range(1, 11).ToList();
        var result = await mr.PostAsync(nos);
        Assert.False(result.Succeeded);
        Assert.Contains("10", result.ErrorMessage);
    }

    [Fact]
    public async Task Wrong_trx_type_rejected()
    {
        var posting = CreatePosting();
        var result = await posting.PostAsync("XX", [1]);
        Assert.False(result.Succeeded);
        Assert.Contains("not implemented", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconcile_flags_mismatch_and_does_not_autofix()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(Request(10m));
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync();
            bal.StdQty = 99m;
            await db.SaveChangesAsync();
        }

        var recon = CreateReconcile();
        var report = await recon.ReconcileAsync();
        Assert.True(report.Succeeded);
        Assert.Contains(report.Findings, x => x.Code == "MISMATCH");
        Assert.Equal("INVENTORY DATA INTEGRITY ERROR", report.Status);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(99m, await verify.IvBalLocs.Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Reconcile_non_stock_null_fk_is_not_orphan()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            Lines = [Line("NS01", 3m)]
        });
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        var report = await CreateReconcile().ReconcileAsync();
        Assert.DoesNotContain(report.Findings, x => x.Code == "ORPHAN_HISTORY");
    }

    private static async Task AssertInvariantAsync(AppDbContext db, int balLocId)
    {
        var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balLocId);
        var inbound = await db.IvTrxHistories
            .Where(x => x.ToBalLocId == balLocId)
            .SumAsync(x => x.ToStdQty ?? 0m);
        var outbound = await db.IvTrxHistories
            .Where(x => x.FromBalLocId == balLocId)
            .SumAsync(x => x.FrStdQty ?? 0m);
        Assert.Equal(IvQty.Round(bal.StdQty), IvQty.Round(inbound - outbound));
    }

    private static IvMiscReceiptSaveRequest Request(decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            VendCode = "SUP01",
            Lines = [Line("A100", qty)]
        };

    private static IvMiscReceiptLineRequest Line(string iCode, decimal qty) =>
        new()
        {
            ICode = iCode,
            ToWarehouse = "MAIN",
            ToLocation = "BIN1",
            Quantity = qty,
            Uom = "EA",
            IClassCode = "RAW",
            IStatus = "ACTIVE",
            UnitPrice = 1m,
            PriceConfirmed = true,
            Reason = "ADJ"
        };

    private async Task SetLot1ExpiryControlAsync(string expiryControl)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "LOT1");
        item.ExpiryControl = expiryControl;
        await db.SaveChangesAsync();
    }

    private async Task<int> CreateGoodsReceiptDraftAsync(DateTime? expiry)
    {
        const int batchNo = 7001;
        const string poNo = "PO-POST-7001";

        await using var db = await _factory.CreateDbContextAsync();
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = poNo,
            PoRelNo = 0,
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            Status = PoOrderStatuses.New,
            RowVersion = Guid.NewGuid().ToByteArray(),
            Details =
            [
                new PoOrderDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    PoNo = poNo,
                    PoRelNo = 0,
                    Line = 1,
                    ICode = "LOT1",
                    IDesc = "Lot item",
                    PoUnitPrice = 1m,
                    PoQty = 3m,
                    PoPurQty = 3m,
                    WtQty = 3m,
                    Amount = 3m,
                    RecvQty = 0m,
                    ReturnQty = 0m,
                    BalanceQty = 3m,
                    OverRecvQty = 0m,
                    InvoicedQty = 0m,
                    PackSz = 1m,
                    StdUom = "EA",
                    PurchaseUom = "EA",
                    CurCode = "MYR",
                    ToWarehouse = "MAIN",
                    OneTime = false
                }
            ]
        });
        db.IvTrxBatches.Add(new IvTrxBatch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batchNo,
            TrxDtTime = FixedToday,
            TrxType = IvTrxTypes.GoodsReceive,
            BatchStatus = IvBatchStatuses.New,
            RefNo = "GR-POST-7001",
            VendCode = "SUP01",
            LocationCode = "SITE",
            Details =
            [
                new IvTrxBatchDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    BatchNo = batchNo,
                    TrxLineNo = 1,
                    TrxType = IvTrxTypes.GoodsReceive,
                    ICode = "LOT1",
                    IDesc = "Lot item",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = "POST-LOT-1",
                    ToStdQty = 3m,
                    ToStdUom = "EA",
                    ToPurQty = 3m,
                    ToPurUom = "EA",
                    IStatus = "ACTIVE",
                    IClassCode = "RAW",
                    ExpiryDate = expiry,
                    PoNo = poNo,
                    PoRelNo = 0,
                    PoLineNo = 1,
                    UnitPrice = 1m,
                    CostPrice = 1m,
                    BaseUnitPrices = 1m,
                    PriceEvidence = "PO_PROVISIONAL|TEST",
                    Currency = "MYR"
                }
            ]
        });
        await db.SaveChangesAsync();
        return batchNo;
    }

    private IvMiscReceiptService CreateMr(bool canPost = true, bool canRollback = true)
    {
        var access = Access(canPost, canRollback);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvMiscReceiptService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            new PoSupplierRepository(_factory),
            new IvUomConversionService(_factory),
            NullLogger<IvMiscReceiptService>.Instance);
    }

    private IIvInventoryPostingService CreatePosting() =>
        new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);

    private IIvInventoryReconciliationService CreateReconcile() =>
        new IvInventoryReconciliationService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object);

    private static Mock<IAccessRightService> Access(bool canPost = true, bool canRollback = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Post, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canPost);
        access.Setup(x => x.CanAsync(MenuCodes.InventoryMiscReceipt, PermissionCodes.Rollback, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canRollback);
        return access;
    }
}
