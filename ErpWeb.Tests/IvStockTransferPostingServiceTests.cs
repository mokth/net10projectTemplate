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

public class IvStockTransferPostingServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockTransferPostingServiceTests()
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
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN2",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN3",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock item",
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
    public async Task Post_moves_stock_from_one_source_to_three_destinations()
    {
        var srcId = await SeedBalLocAsync(24m, loc: "BIN1", unitPrice: 5m, cost: 120m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                TransferLine(srcId, 10m, "BIN2"),
                TransferLine(srcId, 8m, "BIN3"),
                TransferLine(srcId, 6m, "BIN2")
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await tr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(16m, await db.IvBalLocs.Where(x => x.LocCode == "BIN2").Select(x => x.StdQty).SumAsync());
        Assert.Equal(8m, await db.IvBalLocs.Where(x => x.LocCode == "BIN3").Select(x => x.StdQty).SingleAsync());
        Assert.Equal(3, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync()).BatchStatus);

        var destBin2 = await db.IvBalLocs.SingleAsync(x => x.LocCode == "BIN2");
        Assert.Equal(5m, destBin2.UnitPrice);
        Assert.Equal(120m, destBin2.Cost);
    }

    [Fact]
    public async Task Multi_line_same_source_over_transfer_fails_atomically()
    {
        var srcId = await SeedBalLocAsync(24m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                TransferLine(srcId, 15m, "BIN2"),
                TransferLine(srcId, 12m, "BIN3")
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await tr.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(24m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        Assert.False(await db.IvBalLocs.AnyAsync(x => x.LocCode == "BIN2" || x.LocCode == "BIN3"));
    }

    [Fact]
    public async Task Rollback_then_repost_restores_and_reapplies_movement()
    {
        var srcId = await SeedBalLocAsync(100m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(TransferRequest(srcId, 30m, "BIN2"));
        Assert.True((await tr.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(30m, await db.IvBalLocs.Where(x => x.LocCode == "BIN2").Select(x => x.StdQty).SingleAsync());
        }

        Assert.True((await tr.RollbackAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(0, await db.IvTrxHistories.CountAsync());
            Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        }

        Assert.True((await tr.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(30m, await db.IvBalLocs.Where(x => x.LocCode == "BIN2").Select(x => x.StdQty).SingleAsync());
            Assert.Equal(1, await db.IvTrxHistories.CountAsync());
        }
    }

    [Fact]
    public async Task Multi_line_same_source_rollback_restores_net_once()
    {
        var srcId = await SeedBalLocAsync(100m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                TransferLine(srcId, 30m, "BIN2"),
                TransferLine(srcId, 20m, "BIN3")
            ]
        });
        Assert.True((await tr.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await tr.RollbackAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Failure_after_stock_update_rolls_back()
    {
        var srcId = await SeedBalLocAsync(100m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(TransferRequest(srcId, 30m, "BIN2"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePostingService();
        posting.TestHookAfterTrStockUpdate = () => throw new InvalidOperationException("forced after stock");

        var post = await posting.PostAsync(IvTrxTypes.StockTransfer, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Failure_after_history_rolls_back()
    {
        var srcId = await SeedBalLocAsync(100m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(TransferRequest(srcId, 30m, "BIN2"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePostingService();
        posting.TestHookAfterTrHistory = () => throw new InvalidOperationException("forced after history");

        var post = await posting.PostAsync(IvTrxTypes.StockTransfer, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Double_post_rejected()
    {
        var srcId = await SeedBalLocAsync(100m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(TransferRequest(srcId, 30m, "BIN2"));
        Assert.True((await tr.PostAsync([save.BatchNo])).Succeeded);
        var second = await tr.PostAsync([save.BatchNo]);
        Assert.False(second.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Same_slice_save_rejected()
    {
        var srcId = await SeedBalLocAsync(50m, loc: "BIN1");
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(TransferRequest(srcId, 10m, "BIN1"));
        Assert.False(save.Succeeded);
        Assert.Contains("different", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lot_split_post_creates_three_destination_lots()
    {
        await SeedLotItemAsync();
        var (srcId, srcLotId) = await SeedLotBalLocAsync("SRCLOT", 30m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "DEST01"),
                LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "DEST02"),
                LotTransferLine(srcId, 10m, "BIN3", "SRCLOT", "DEST03")
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await tr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(4, await db.IvLots.CountAsync(x => x.ICode == "LOT1"));
        Assert.Equal(3, await db.IvTrxHistories.CountAsync());
        Assert.Contains(await db.IvLots.Select(x => x.LotNo).ToListAsync(), x => x == "DEST01");
        Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync()).BatchStatus);

        var srcBal = await db.IvBalLocs.SingleAsync(x => x.Id == srcId);
        Assert.Equal(srcLotId, srcBal.LotId);
    }

    [Fact]
    public async Task Lot_split_rollback_restores_source_qty()
    {
        await SeedLotItemAsync();
        var (srcId, _) = await SeedLotBalLocAsync("SRCLOT", 30m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "DEST01"),
                LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "DEST02"),
                LotTransferLine(srcId, 10m, "BIN3", "SRCLOT", "DEST03")
            ]
        });
        Assert.True((await tr.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await tr.RollbackAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(30m, await db.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(4, await db.IvLots.CountAsync(x => x.ICode == "LOT1"));
    }

    [Fact]
    public async Task Save_rejects_duplicate_destination_lot_in_batch()
    {
        await SeedLotItemAsync();
        var (srcId, _) = await SeedLotBalLocAsync("SRCLOT", 30m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "ABC001"),
                LotTransferLine(srcId, 10m, "BIN3", "SRCLOT", "abc001")
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("Duplicate destination lot", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_destination_lot_equal_to_source_lot()
    {
        await SeedLotItemAsync();
        var (srcId, _) = await SeedLotBalLocAsync("SRCLOT", 30m);
        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "SRCLOT")]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("differ from source lot", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_existing_destination_lot()
    {
        await SeedLotItemAsync();
        var (srcId, _) = await SeedLotBalLocAsync("SRCLOT", 30m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLots.Add(new IvLot
            {
                CompanyCode = "DEMO",
                ICode = "LOT1",
                LotNo = "TAKEN",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "TAKEN")]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("already exists", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_post_fails_when_third_destination_lot_exists()
    {
        await SeedLotItemAsync();
        var (srcId, _) = await SeedLotBalLocAsync("SRCLOT", 30m);

        var tr = CreateTr();
        var save = await tr.SaveNewAsync(new IvStockTransferSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "DEST01"),
                LotTransferLine(srcId, 10m, "BIN2", "SRCLOT", "DEST02"),
                LotTransferLine(srcId, 10m, "BIN3", "SRCLOT", "DEST03")
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLots.Add(new IvLot
            {
                CompanyCode = "DEMO",
                ICode = "LOT1",
                LotNo = "DEST03",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var post = await tr.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("already exists", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UNIQUE", post.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", post.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(30m, await verify.IvBalLocs.Where(x => x.Id == srcId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await verify.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
        Assert.False(await verify.IvLots.AnyAsync(x => x.LotNo == "DEST01" || x.LotNo == "DEST02"));
    }

    private async Task SeedLotItemAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (!await db.IvStockMasters.AnyAsync(x => x.ICode == "LOT1"))
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "LOT1",
                IDesc = "Lot item",
                IClassCode = "RAW",
                StdUom = "EA",
                StockControl = true,
                LotControl = true,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<(int BalLocId, int LotId)> SeedLotBalLocAsync(string lotNo, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lot = new IvLot
        {
            CompanyCode = "DEMO",
            ICode = "LOT1",
            LotNo = lotNo,
            IsActive = true
        };
        db.IvLots.Add(lot);
        await db.SaveChangesAsync();

        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "LOT1",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = lotNo,
            LotId = lot.Id,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA"
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return (bal.Id, lot.Id);
    }

    private static IvStockTransferLineRequest LotTransferLine(
        int fromBalLocId,
        decimal qty,
        string toLoc,
        string frLotNo,
        string toLotNo) =>
        new()
        {
            FromBalLocId = fromBalLocId,
            ICode = "LOT1",
            FrWarehouse = "MAIN",
            FrLocation = "BIN1",
            FrLotNo = frLotNo,
            ToWarehouse = "MAIN",
            ToLocation = toLoc,
            ToLotNo = toLotNo,
            Quantity = qty,
            Uom = "EA",
            IClassCode = "RAW",
            IStatus = "ACTIVE",
            UnitPrice = 1m
        };

    private async Task<int> SeedBalLocAsync(
        decimal qty,
        string iCode = "A100",
        string wh = "MAIN",
        string loc = "BIN1",
        string status = "ACTIVE",
        decimal? unitPrice = null,
        decimal? cost = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = iCode,
            WhCode = wh,
            LocCode = loc,
            LotNo = string.Empty,
            IStatus = status,
            StdQty = qty,
            StdUom = "EA",
            UnitPrice = unitPrice,
            Cost = cost
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static IvStockTransferSaveRequest TransferRequest(int fromBalLocId, decimal qty, string toLoc) =>
        new()
        {
            TrxDate = FixedToday,
            Lines = [TransferLine(fromBalLocId, qty, toLoc)]
        };

    private static IvStockTransferLineRequest TransferLine(int fromBalLocId, decimal qty, string toLoc) =>
        new()
        {
            FromBalLocId = fromBalLocId,
            ICode = "A100",
            FrWarehouse = "MAIN",
            FrLocation = "BIN1",
            FrLotNo = string.Empty,
            ToWarehouse = "MAIN",
            ToLocation = toLoc,
            Quantity = qty,
            Uom = "EA",
            IClassCode = "RAW",
            IStatus = "ACTIVE",
            UnitPrice = 1m
        };

    private IvStockTransferService CreateTr()
    {
        var access = Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvStockTransferService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvStockTransferService>.Instance);
    }

    private IvInventoryPostingService CreatePostingService() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
