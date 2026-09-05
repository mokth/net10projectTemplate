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

public class IvStockReturnPostingServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockReturnPostingServiceTests()
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
                IDesc = "Widget",
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
                ICode = "INACT",
                IDesc = "Will deactivate",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = true,
                LotControl = false,
                IsActive = true
            });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveNew_succeeds_with_CR_trx_type()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(ReturnRequest(10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Equal(IvTrxTypes.CustomerReturn, batch.TrxType);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
    }

    [Fact]
    public async Task Save_rejects_missing_and_unknown_reason()
    {
        var cr = CreateCr();
        var line = ValidLine(10m);
        line.Reason = null;
        Assert.False((await cr.SaveNewAsync(Wrap(line))).Succeeded);

        line = ValidLine(10m);
        line.Reason = "NOT_A_REASON";
        var bad = await cr.SaveNewAsync(Wrap(line));
        Assert.False(bad.Succeeded);
        Assert.Contains("not valid", bad.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reason_round_trip_and_update()
    {
        var cr = CreateCr();
        var line = ValidLine(10m);
        line.Reason = IvReturnReasons.Return;
        line.Remarks = "User remark";
        var save = await cr.SaveNewAsync(Wrap(line));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var get1 = await cr.GetAsync(save.BatchNo);
        Assert.True(get1.Succeeded, get1.ErrorMessage);
        var loaded = get1.Document!.Lines.Single();
        Assert.Equal(IvReturnReasons.Return, loaded.Reason);
        Assert.Equal("User remark", loaded.Remarks);

        var updateLine = ValidLine(10m);
        updateLine.Reason = IvReturnReasons.Excess;
        updateLine.Remarks = "Updated";
        Assert.True((await cr.UpdateAsync(save.BatchNo, Wrap(updateLine))).Succeeded);

        var reloaded = (await cr.GetAsync(save.BatchNo)).Document!.Lines.Single();
        Assert.Equal(IvReturnReasons.Excess, reloaded.Reason);
        Assert.Equal("Updated", reloaded.Remarks);
    }

    [Fact]
    public async Task IClassCode_always_from_item_master()
    {
        var cr = CreateCr();
        var line = ValidLine(10m);
        line.IClassCode = "FG";
        var save = await cr.SaveNewAsync(Wrap(line));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("RAW", (await db.IvTrxBatchDetails.SingleAsync()).IClassCode);
    }

    [Fact]
    public async Task Post_increases_stock_writes_CR_history()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(ReturnRequest(30m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await cr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(30m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        var history = await db.IvTrxHistories.SingleAsync();
        Assert.Equal(IvTrxTypes.CustomerReturn, history.TrxType);
        var batch = await db.IvTrxBatches.SingleAsync();
        Assert.Equal(IvBatchStatuses.Posted, batch.BatchStatus);
        Assert.NotNull(batch.PostedDate);
        Assert.Equal(1, batch.PostedCount);
        Assert.NotNull(batch.PostingOperationId);
    }

    [Fact]
    public async Task Post_twice_fails_qty_unchanged()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(ReturnRequest(50m));
        Assert.True((await cr.PostAsync([save.BatchNo])).Succeeded);

        var second = await cr.PostAsync([save.BatchNo]);
        Assert.False(second.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(50m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task CR_batch_cannot_be_posted_as_MR()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(ReturnRequest(10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePosting();
        var post = await posting.PostAsync(IvTrxTypes.MiscellaneousReceipt, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task MR_batch_cannot_be_posted_as_CR()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(MrRequest(10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePosting();
        var post = await posting.PostAsync(IvTrxTypes.CustomerReturn, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
    }

    [Fact]
    public async Task Rollback_restores_qty_and_returns_NEW()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(ReturnRequest(25m));
        Assert.True((await cr.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await cr.RollbackAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        var detail = await db.IvTrxBatchDetails.SingleAsync();
        Assert.Null(detail.ToBalLocId);
        Assert.Null(detail.ToLotId);
    }

    [Fact]
    public async Task Rollback_negative_fails_atomically()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(ReturnRequest(100m));
        Assert.True((await cr.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync();
            bal.StdQty = 20m;
            await db.SaveChangesAsync();
        }

        var rb = await cr.RollbackAsync([save.BatchNo]);
        Assert.False(rb.Succeeded);
        Assert.Contains("negative", rb.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.Posted, await verify.IvTrxBatches.Select(x => x.BatchStatus).SingleAsync());
        Assert.Equal(20m, await verify.IvBalLocs.Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Lot_controlled_new_lot_gets_CR_source_type()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvStockReturnLineRequest
                {
                    ICode = "LOT1",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = "L-CR-001",
                    Quantity = 7m,
                    UnitPrice = 1m,
                    IStatus = "ACTIVE",
                    Reason = IvReturnReasons.Return,
                    ExpiryDate = FixedToday.AddDays(30)
                }
            ]
        });
        Assert.True((await cr.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var lot = Assert.Single(await db.IvLots.ToListAsync());
        Assert.Equal("L-CR-001", lot.LotNo);
        Assert.Equal(IvTrxTypes.CustomerReturn, lot.SourceType);
    }

    [Fact]
    public async Task Lot_reuses_existing_MR_lot_without_changing_source_type()
    {
        var mr = CreateMr();
        var mrReq = new IvMiscReceiptSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = "LOT1",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = "L-SHARED",
                    Quantity = 5m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    ExpiryDate = FixedToday.AddDays(30)
                }
            ]
        };
        var mrSave = await mr.SaveNewAsync(mrReq);
        Assert.True((await mr.PostAsync([mrSave.BatchNo])).Succeeded);

        var cr = CreateCr();
        var crSave = await cr.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvStockReturnLineRequest
                {
                    ICode = "LOT1",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = "L-SHARED",
                    Quantity = 3m,
                    UnitPrice = 1m,
                    IStatus = "ACTIVE",
                    Reason = IvReturnReasons.Return,
                    ExpiryDate = FixedToday.AddDays(30)
                }
            ]
        });
        Assert.True((await cr.PostAsync([crSave.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var lot = Assert.Single(await db.IvLots.ToListAsync());
        Assert.Equal(IvTrxTypes.MiscellaneousReceipt, lot.SourceType);
        Assert.Equal(8m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Multi_line_post_fails_atomically_when_item_deactivated()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                ValidLine(10m),
                ValidLine(5m),
                new IvStockReturnLineRequest
                {
                    ICode = "INACT",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    Quantity = 2m,
                    UnitPrice = 1m,
                    IStatus = "ACTIVE",
                    Reason = IvReturnReasons.Return
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "INACT");
            item.IsActive = false;
            await db.SaveChangesAsync();
        }

        var post = await cr.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.IvBalLocs.CountAsync());
        Assert.Equal(0, await verify.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Multi_line_save_fails_when_one_line_invalid()
    {
        var cr = CreateCr();
        var bad = ValidLine(10m);
        bad.Quantity = 0;
        var save = await cr.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ValidLine(10m), ValidLine(5m), bad]
        });
        Assert.False(save.Succeeded);
        Assert.Equal(0, await _factory.CreateDbContext().IvTrxBatches.CountAsync());
    }

    [Fact]
    public async Task Update_atomic_when_one_line_invalid()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ValidLine(10m), ValidLine(5m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var bad = ValidLine(10m);
        bad.Quantity = 0;
        var update = await cr.UpdateAsync(save.BatchNo, new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ValidLine(10m), ValidLine(5m), bad]
        });
        Assert.False(update.Succeeded);

        var loaded = await cr.GetAsync(save.BatchNo);
        Assert.Equal(2, loaded.Document!.Lines.Count);
        Assert.Equal(10m, loaded.Document.Lines[0].Quantity);
    }

    [Fact]
    public async Task Post_is_denied_without_POST_permission()
    {
        var cr = CreateCr(canPost: false);
        var save = await cr.SaveNewAsync(ReturnRequest(10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await cr.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Equal("Not authorized.", post.ErrorMessage);
    }

    [Fact]
    public async Task Rollback_is_denied_without_ROLLBACK_permission()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(ReturnRequest(10m));
        Assert.True((await cr.PostAsync([save.BatchNo])).Succeeded);

        var denied = CreateCr(canRollback: false);
        var rb = await denied.RollbackAsync([save.BatchNo]);
        Assert.False(rb.Succeeded);
        Assert.Equal("Not authorized.", rb.ErrorMessage);
    }

    [Fact]
    public async Task Multi_line_rollback_reverses_all_lines_atomically()
    {
        var cr = CreateCr();
        var save = await cr.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ValidLine(10m), ValidLine(5m)]
        });
        Assert.True((await cr.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await cr.RollbackAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    private static IvStockReturnSaveRequest ReturnRequest(decimal qty) =>
        new() { TrxDate = FixedToday, Lines = [ValidLine(qty)] };

    private static IvStockReturnSaveRequest Wrap(IvStockReturnLineRequest line) =>
        new() { TrxDate = FixedToday, Lines = [line] };

    private static IvStockReturnLineRequest ValidLine(decimal qty) =>
        new()
        {
            ICode = "A100",
            ToWarehouse = "MAIN",
            ToLocation = "BIN1",
            Quantity = qty,
            UnitPrice = 1m,
            IStatus = "ACTIVE",
            Reason = IvReturnReasons.Return
        };

    private static IvMiscReceiptSaveRequest MrRequest(decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = "A100",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    Quantity = qty,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = 1m
                }
            ]
        };

    private IvStockReturnService CreateCr(bool canPost = true, bool canRollback = true)
    {
        var access = Access(canPost, canRollback, MenuCodes.InventoryStockReturn);
        return BuildCr(access);
    }

    private IvMiscReceiptService CreateMr()
    {
        var access = Access(true, true, MenuCodes.InventoryMiscReceipt);
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

    private IvStockReturnService BuildCr(Mock<IAccessRightService> access)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvStockReturnService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvStockReturnService>.Instance);
    }

    private IIvInventoryPostingService CreatePosting() =>
        new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access(true, true, MenuCodes.InventoryStockReturn).Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

    private static Mock<IAccessRightService> Access(
        bool canPost,
        bool canRollback,
        string menuCode)
    {
        var access = new Mock<IAccessRightService>();
        foreach (var perm in new[]
                 {
                     PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Edit,
                     PermissionCodes.Delete, PermissionCodes.Post, PermissionCodes.Rollback
                 })
        {
            var allowed = perm switch
            {
                PermissionCodes.Post => canPost,
                PermissionCodes.Rollback => canRollback,
                _ => true
            };
            access.Setup(x => x.CanAsync(menuCode, perm, It.IsAny<CancellationToken>()))
                .ReturnsAsync(allowed);
        }

        return access;
    }
}
