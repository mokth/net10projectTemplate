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

public class IvScrapPostingServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvScrapPostingServiceTests()
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
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveNew_succeeds_with_SC_trx_type()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo);
        Assert.Equal(IvTrxTypes.Scrap, batch.TrxType);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
    }

    [Fact]
    public async Task Save_with_zero_lines_fails()
    {
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest
        {
            TrxDate = FixedToday,
            Lines = []
        });
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task Save_missing_reason_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var line = ScrapLine(balId, 10m);
        line.Reason = null;
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [line]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("reason", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_blank_reason_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var line = ScrapLine(balId, 10m);
        line.Reason = "   ";
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest { TrxDate = FixedToday, Lines = [line] });
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task Save_invalid_reason_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var line = ScrapLine(balId, 10m);
        line.Reason = "NOT_A_REASON";
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest { TrxDate = FixedToday, Lines = [line] });
        Assert.False(save.Succeeded);
        Assert.Contains("not valid", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_zero_quantity_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var line = ScrapLine(balId, 0m);
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest { TrxDate = FixedToday, Lines = [line] });
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task Save_negative_quantity_fails()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var line = ScrapLine(balId, -5m);
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest { TrxDate = FixedToday, Lines = [line] });
        Assert.False(save.Succeeded);
    }

    [Fact]
    public async Task Post_decreases_stock_writes_SC_history()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 30m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await scrap.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var history = await db.IvTrxHistories.SingleAsync();
        Assert.Equal(IvTrxTypes.Scrap, history.TrxType);
    }

    [Fact]
    public async Task Multi_line_over_qty_fails_atomically()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ScrapLine(balId, 60m), ScrapLine(balId, 50m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await scrap.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Rollback_restores_qty_and_returns_NEW()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 25m));
        Assert.True((await scrap.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await scrap.RollbackAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Posted_document_cannot_be_edited()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 10m));
        Assert.True((await scrap.PostAsync([save.BatchNo])).Succeeded);

        var update = await scrap.UpdateAsync(save.BatchNo, ScrapRequest(balId, 5m));
        Assert.False(update.Succeeded);
    }

    [Fact]
    public async Task Cancelled_document_cannot_be_edited_or_posted()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 10m));
        Assert.True((await scrap.CancelAsync([save.BatchNo])).Succeeded);

        var update = await scrap.UpdateAsync(save.BatchNo, ScrapRequest(balId, 5m));
        Assert.False(update.Succeeded);

        var post = await scrap.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
    }

    [Fact]
    public async Task SC_batch_cannot_be_posted_as_MI()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePostingService();
        var post = await posting.PostAsync(IvTrxTypes.MiscellaneousIssue, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task SC_posted_batch_cannot_be_rolled_back_as_MI()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 10m));
        Assert.True((await scrap.PostAsync([save.BatchNo])).Succeeded);

        var posting = CreatePostingService();
        var rollback = await posting.RollbackAsync(IvTrxTypes.MiscellaneousIssue, [save.BatchNo]);
        Assert.False(rollback.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(90m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Reason_round_trip_and_update()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var line = ScrapLine(balId, 10m);
        line.Reason = IvScrapReasons.Damaged;
        line.Remarks = "User remark";
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest { TrxDate = FixedToday, Lines = [line] });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var get1 = await scrap.GetAsync(save.BatchNo);
        Assert.True(get1.Succeeded, get1.ErrorMessage);
        var loaded = get1.Document!.Lines.Single();
        Assert.Equal(IvScrapReasons.Damaged, loaded.Reason);
        Assert.Equal("User remark", loaded.Remarks);

        var updateLine = ScrapLine(balId, 10m);
        updateLine.Reason = IvScrapReasons.Expired;
        updateLine.Remarks = "User remark";
        Assert.True((await scrap.UpdateAsync(save.BatchNo, new IvScrapSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [updateLine]
        })).Succeeded);

        var get2 = await scrap.GetAsync(save.BatchNo);
        var reloaded = get2.Document!.Lines.Single();
        Assert.Equal(IvScrapReasons.Expired, reloaded.Reason);
        Assert.Equal("User remark", reloaded.Remarks);
        Assert.DoesNotContain("DAMAGED", reloaded.Remarks ?? string.Empty);
    }

    [Fact]
    public async Task Remarks_250_char_boundary_preserves_reason()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var longRemark = new string('x', 240);
        var line = ScrapLine(balId, 5m);
        line.Reason = IvScrapReasons.Damaged;
        line.Remarks = longRemark;
        var save = await scrap.SaveNewAsync(new IvScrapSaveRequest { TrxDate = FixedToday, Lines = [line] });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var get = await scrap.GetAsync(save.BatchNo);
        var loaded = get.Document!.Lines.Single();
        Assert.Equal(IvScrapReasons.Damaged, loaded.Reason);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.IvTrxBatchDetails.Select(x => x.Remarks).SingleAsync();
        Assert.NotNull(stored);
        Assert.True(stored!.Length <= 250);
    }

    [Fact]
    public async Task Stale_source_pile_identity_rejected_on_post()
    {
        var balId = await SeedBalLocAsync(100m);
        var scrap = CreateScrap();
        var save = await scrap.SaveNewAsync(ScrapRequest(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
            bal.IStatus = "QCHOLD";
            await db.SaveChangesAsync();
        }

        var post = await scrap.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await verify.IvTrxHistories.CountAsync());
    }

    private async Task<int> SeedBalLocAsync(decimal qty)
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
            StdUom = "EA"
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static IvScrapSaveRequest ScrapRequest(int fromBalLocId, decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines = [ScrapLine(fromBalLocId, qty)]
        };

    private static IvScrapLineRequest ScrapLine(int fromBalLocId, decimal qty) =>
        new()
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
            UnitPrice = 1m,
            Reason = IvScrapReasons.Damaged
        };

    private IvScrapService CreateScrap()
    {
        var access = Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvScrapService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvScrapService>.Instance);
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
