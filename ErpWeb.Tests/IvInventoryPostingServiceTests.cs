using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Business-logic posting/rollback tests (SQLite). Does not prove SQL Server UPDLOCK/HOLDLOCK.
/// </summary>
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
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
        await AssertInvariantAsync(db, bal.Id);
    }

    [Fact]
    public async Task Post_same_slice_lines_aggregate_to_one_bal_three_history()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
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
                    ExpiryDate = FixedToday.AddDays(30)
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
        Assert.Equal(14m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
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
        var result = await posting.PostAsync("TR", [1]);
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
            UnitPrice = 1m
        };

    private IvMiscReceiptService CreateMr(bool canPost = true, bool canRollback = true)
    {
        var access = Access(canPost, canRollback);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvMiscReceiptService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvMiscReceiptService>.Instance);
    }

    private IIvInventoryPostingService CreatePosting() =>
        new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

    private IIvInventoryReconciliationService CreateReconcile() =>
        new IvInventoryReconciliationService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext());

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
