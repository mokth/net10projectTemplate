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

public class IvStockAdjustmentPostingServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockAdjustmentPostingServiceTests()
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
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = 5m
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveNew_succeeds_with_ADJ_trx_type()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, -10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Equal(IvTrxTypes.StockAdjustment, batch.TrxType);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
    }

    [Fact]
    public async Task Save_missing_reason_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var line = AdjLine(balId, -10m);
        line.Reason = null;
        var save = await adj.SaveNewAsync(new IvStockAdjustmentSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [line]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("reason", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_zero_adjust_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, 0m));
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task Post_decrease_updates_stock_and_writes_history()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, -30m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await adj.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var history = await db.IvTrxHistories.SingleAsync();
        Assert.Equal(IvTrxTypes.StockAdjustment, history.TrxType);
        Assert.Equal(30m, history.FrStdQty);
    }

    [Fact]
    public async Task Post_increase_updates_stock()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, 15m));
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(115m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var history = await db.IvTrxHistories.SingleAsync();
        Assert.Equal(15m, history.ToStdQty);
    }

    [Fact]
    public async Task Post_increase_on_zero_qty_bin_succeeds()
    {
        var balId = await SeedBalLocAsync(0m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, 5m));
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(5m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Mixed_batch_nets_on_same_bin()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(new IvStockAdjustmentSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [AdjLine(balId, 10m), AdjLine(balId, -3m)]
        });
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(107m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(2, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Zero_net_batch_posts_without_stock_change()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(new IvStockAdjustmentSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [AdjLine(balId, 10m), AdjLine(balId, -10m)]
        });
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(2, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Multi_line_over_qty_fails_at_save()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(new IvStockAdjustmentSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [AdjLine(balId, -60m), AdjLine(balId, -50m)]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("Insufficient", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_ui_post_fails_when_on_hand_drops()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, -80m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
            bal.StdQty = 60m;
            await db.SaveChangesAsync();
        }

        var post = await adj.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(60m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Rollback_after_later_movement_applies_inverse_to_current_stock()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, 30m));
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
            bal.StdQty -= 50m;
            await db.SaveChangesAsync();
        }

        Assert.True((await adj.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(50m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(IvBatchStatuses.New, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Post_without_permission_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, -5m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePostingService(denyPost: true);
        var post = await posting.PostAsync(IvTrxTypes.StockAdjustment, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Save_other_reason_without_remark_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var line = AdjLine(balId, -10m);
        line.Reason = IvAdjustmentReasons.Other;
        line.Remarks = null;
        var save = await adj.SaveNewAsync(new IvStockAdjustmentSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [line]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("remark", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_missing_bal_loc_fails_atomically()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, -10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM IvBalLoc WHERE Id = {0}", balId);
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }

        var post = await adj.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.IvBalLocs.CountAsync());
        Assert.Equal(0, await verify.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Duplicate_bal_loc_four_lines_nets_once_with_four_history_rows()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(new IvStockAdjustmentSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                AdjLine(balId, 10m),
                AdjLine(balId, -3m),
                AdjLine(balId, 5m),
                AdjLine(balId, -2m)
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(110m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(4, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Rollback_history_mismatch_fails_atomically()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, -20m));
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var history = await db.IvTrxHistories.SingleAsync();
            history.FrStdQty = 99m;
            await db.SaveChangesAsync();
        }

        var rollback = await adj.RollbackAsync([save.BatchNo]);
        Assert.False(rollback.Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(80m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(IvBatchStatuses.Posted, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Post_dispatch_accepts_ADJ_trx_type()
    {
        var balId = await SeedBalLocAsync(100m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, -5m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePostingService();
        var post = await posting.PostAsync(IvTrxTypes.StockAdjustment, [save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
    }

    [Fact]
    public async Task BalLoc_cost_unchanged_after_post()
    {
        var balId = await SeedBalLocAsync(100m, cost: 12.5m, unitPrice: 8m);
        var adj = CreateAdjustment();
        var save = await adj.SaveNewAsync(AdjRequest(balId, 20m));
        Assert.True((await adj.PostAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
        Assert.Equal(12.5m, bal.Cost);
        Assert.Equal(8m, bal.UnitPrice);
    }

    private async Task<int> SeedBalLocAsync(decimal qty, decimal? cost = null, decimal? unitPrice = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            Cost = cost,
            UnitPrice = unitPrice
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static IvStockAdjustmentSaveRequest AdjRequest(int balLocId, decimal adjustQty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines = [AdjLine(balLocId, adjustQty)]
        };

    private static IvStockAdjustmentLineRequest AdjLine(int balLocId, decimal adjustQty) =>
        new()
        {
            BalLocId = balLocId,
            ICode = "A100",
            Warehouse = "MAIN",
            Location = "BIN1",
            LotNo = string.Empty,
            AdjustQty = adjustQty,
            Uom = "EA",
            IClassCode = "RAW",
            IStatus = "ACTIVE",
            UnitPrice = 1m,
            Reason = IvAdjustmentReasons.Count
        };

    private IvStockAdjustmentService CreateAdjustment()
    {
        var access = Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvStockAdjustmentService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvStockAdjustmentService>.Instance);
    }

    private IvInventoryPostingService CreatePostingService(bool denyPost = false)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Post, It.IsAny<CancellationToken>()))
            .ReturnsAsync(!denyPost);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsNotIn(PermissionCodes.Post), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
