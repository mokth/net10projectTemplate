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

namespace ErpWeb.Tests.Inventory.Transaction;
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMiscIssue)]
public class IvMiscIssuePostingServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvMiscIssuePostingServiceTests()
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
            Barcode = "BC-A100"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "B200",
            IDesc = "Other item same barcode",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            Barcode = "DUP-BC"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "C300",
            IDesc = "Dup barcode peer",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            Barcode = "DUP-BC"
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task<(int Histories, int Postings, int Facts, int Layers)> ReadPostingCountsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return (
            await db.IvTrxHistories.CountAsync(),
            await db.StockPostings.CountAsync(),
            await db.StockValuationFacts.CountAsync(),
            await db.StockFifoLayers.CountAsync());
    }

    [Fact]
    public async Task Post_decreases_stock_writes_history_and_posts()
    {
        var balId = await SeedBalLocAsync(100m);
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(IssueRequest(balId, 30m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
        Assert.Equal(70m, bal.StdQty);
        Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo)).BatchStatus);
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
        var detail = await db.IvTrxBatchDetails.SingleAsync();
        Assert.Equal(balId, detail.FromBalLocId);
        Assert.Null(detail.FromLotId);
    }

    [Fact]
    public async Task Save_future_dated_new_issue_keeps_balance_and_ledger_unchanged()
    {
        var balId = await SeedBalLocAsync(100m);
        var before = await ReadPostingCountsAsync();
        var mi = CreateMi();
        var request = IssueRequest(balId, 30m);
        request.TrxDate = FixedToday.AddDays(1);

        var saved = await mi.SaveNewAsync(request);
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        request.TrxDate = FixedToday.AddDays(2);
        var updated = await mi.UpdateAsync(saved.BatchNo, request);
        Assert.True(updated.Succeeded, updated.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Equal(FixedToday.AddDays(2), batch.TrxDtTime);
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(before, await ReadPostingCountsAsync());
    }

    [Fact]
    public async Task Future_issue_revalidates_stock_that_changed_before_its_post_date()
    {
        var balId = await SeedBalLocAsync(100m);
        var effectiveDate = FixedToday.AddDays(1);
        var futureIssue = CreateMi(postingBusinessDate: effectiveDate);
        var savedFuture = await futureIssue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = effectiveDate,
            Lines = [IssueLine(balId, 80m)]
        });
        Assert.True(savedFuture.Succeeded, savedFuture.ErrorMessage);

        var currentIssue = CreateMi(postingBusinessDate: FixedToday);
        var savedCurrent = await currentIssue.SaveNewAsync(IssueRequest(balId, 50m));
        Assert.True(savedCurrent.Succeeded, savedCurrent.ErrorMessage);
        var currentPost = await currentIssue.PostAsync([savedCurrent.BatchNo]);
        Assert.True(currentPost.Succeeded, currentPost.ErrorMessage);

        var futurePost = await CreateMi(postingBusinessDate: effectiveDate).PostAsync([savedFuture.BatchNo]);
        Assert.False(futurePost.Succeeded);
        Assert.Contains("Insufficient quantity", futurePost.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        var futureBatch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == savedFuture.BatchNo);
        Assert.Equal(IvBatchStatuses.New, futureBatch.BatchStatus);
        Assert.Null(futureBatch.PostedDate);
        Assert.Null(futureBatch.PostedBy);
        Assert.Equal(0, futureBatch.PostedCount);
        Assert.Null(futureBatch.PostingOperationId);
        Assert.Equal(50m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var history = Assert.Single(await db.IvTrxHistories.ToListAsync());
        Assert.Equal(savedCurrent.BatchNo, history.BatchNo);
        Assert.Equal((1, 0, 0, 0), await ReadPostingCountsAsync());
    }

    [Fact]
    public async Task Future_issue_cannot_post_ahead_of_an_existing_later_movement()
    {
        var balId = await SeedBalLocAsync(100m);
        var futureDate = FixedToday.AddDays(1);
        var laterDate = futureDate.AddDays(1);
        var futureIssue = CreateMi(postingBusinessDate: laterDate);
        var savedFuture = await futureIssue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = futureDate,
            Lines = [IssueLine(balId, 10m)]
        });
        Assert.True(savedFuture.Succeeded, savedFuture.ErrorMessage);

        var laterIssue = CreateMi(postingBusinessDate: laterDate);
        var savedLater = await laterIssue.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = laterDate,
            Lines = [IssueLine(balId, 10m)]
        });
        Assert.True(savedLater.Succeeded, savedLater.ErrorMessage);
        var laterPost = await laterIssue.PostAsync([savedLater.BatchNo]);
        Assert.True(laterPost.Succeeded, laterPost.ErrorMessage);

        var earlyPost = await CreateMi(postingBusinessDate: laterDate).PostAsync([savedFuture.BatchNo]);
        Assert.False(earlyPost.Succeeded);
        Assert.Contains("Stock balance is dated", earlyPost.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(laterDate.ToString("dd/MM/yyyy"), earlyPost.ErrorMessage);
        Assert.Contains(futureDate.ToString("dd/MM/yyyy"), earlyPost.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var futureBatch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == savedFuture.BatchNo);
        Assert.Equal(IvBatchStatuses.New, futureBatch.BatchStatus);
        Assert.Equal(0, futureBatch.PostedCount);
        Assert.Equal(90m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var history = Assert.Single(await db.IvTrxHistories.ToListAsync());
        Assert.Equal(savedLater.BatchNo, history.BatchNo);
    }

    [Fact]
    public async Task TestA_multi_line_same_balance_over_issue_fails_atomically()
    {
        var balId = await SeedBalLocAsync(100m);
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                IssueLine(balId, 60m),
                IssueLine(balId, 50m)
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await mi.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task TestG_rollback_then_repost()
    {
        var balId = await SeedBalLocAsync(100m);
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(IssueRequest(balId, 30m));
        var posted = await mi.PostAsync([save.BatchNo]);
        Assert.True(posted.Succeeded, posted.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        }

        Assert.True((await mi.RollbackAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(0, await db.IvTrxHistories.CountAsync());
            var detail = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(balId, detail.FromBalLocId);
            Assert.Null(detail.FromLotId);
            Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        }

        Assert.True((await mi.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(1, await db.IvTrxHistories.CountAsync());
            Assert.Equal(balId, await db.IvTrxHistories.Select(x => x.FromBalLocId).SingleAsync());
        }
    }

    [Fact]
    public async Task Multi_line_same_balance_rollback_restores_net_once()
    {
        var balId = await SeedBalLocAsync(100m);
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(new IvMiscIssueSaveRequest
        {
            TrxDate = FixedToday,
            Lines =
            [
                IssueLine(balId, 30m),
                IssueLine(balId, 20m)
            ]
        });
        Assert.True((await mi.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await mi.RollbackAsync([save.BatchNo])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task TestD_failure_after_stock_update_rolls_back()
    {
        var balId = await SeedBalLocAsync(100m);
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(IssueRequest(balId, 30m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePostingService();
        posting.TestHookAfterMiStockUpdate = () => throw new InvalidOperationException("forced after stock");

        var post = await posting.PostAsync(IvTrxTypes.MiscellaneousIssue, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task TestE_failure_after_history_rolls_back()
    {
        var balId = await SeedBalLocAsync(100m);
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(IssueRequest(balId, 30m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var posting = CreatePostingService();
        posting.TestHookAfterMiHistory = () => throw new InvalidOperationException("forced after history");

        var post = await posting.PostAsync(IvTrxTypes.MiscellaneousIssue, [save.BatchNo]);
        Assert.False(post.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
    }

    [Fact]
    public async Task Double_post_rejected()
    {
        var balId = await SeedBalLocAsync(100m);
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(IssueRequest(balId, 30m));
        Assert.True((await mi.PostAsync([save.BatchNo])).Succeeded);
        var second = await mi.PostAsync([save.BatchNo]);
        Assert.False(second.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Never_find_or_create_missing_balance_fails()
    {
        var mi = CreateMi();
        var save = await mi.SaveNewAsync(IssueRequest(fromBalLocId: 99999, qty: 10m));
        // Save may fail advisory validation if balance missing — either path is OK.
        if (save.Succeeded)
        {
            var post = await mi.PostAsync([save.BatchNo]);
            Assert.False(post.Succeeded);
        }

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
    }

    [Fact]
    public async Task Lookup_resolve_item_icode_before_barcode()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveItemAsync("A100");
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("A100", result.Item!.ICode);
    }

    [Fact]
    public async Task Lookup_resolve_duplicate_barcode_rejects()
    {
        var lookups = CreateLookups();
        var result = await lookups.ResolveItemAsync("DUP-BC");
        Assert.False(result.Succeeded);
        Assert.Contains("multiple", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lookup_on_hand_only_positive_qty()
    {
        await SeedBalLocAsync(50m);
        await SeedBalLocAsync(0m, iCode: "A100", wh: "MAIN", loc: "BIN1", status: "ACTIVE", forceZero: true);
        var lookups = CreateLookups();
        var result = await lookups.SearchOnHandAsync(new IvOnHandSearchRequest { Take = 50 });
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.All(result.Rows, r => Assert.True(r.StdQty > 0m));
    }

    private async Task<int> SeedBalLocAsync(
        decimal qty,
        string iCode = "A100",
        string wh = "MAIN",
        string loc = "BIN1",
        string status = "ACTIVE",
        bool forceZero = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (!forceZero && qty <= 0m)
        {
            qty = 0.0001m;
        }

        // Unique slice — for zero-qty row use a distinct loc so we can still seed for filter tests.
        var locCode = forceZero && qty == 0m ? "ZERO" : loc;
        if (forceZero && qty == 0m)
        {
            if (!await db.IvLocations.AnyAsync(x => x.LocCode == "ZERO"))
            {
                db.IvLocations.Add(new IvLocation
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    WarehouseCode = wh,
                    LocCode = "ZERO",
                    IsActive = true
                });
            }
        }

        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = iCode,
            WhCode = wh,
            LocCode = locCode,
            LotNo = string.Empty,
            IStatus = status,
            StdQty = forceZero ? 0m : qty,
            StdUom = "EA",
            TransDate = FixedToday
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static IvMiscIssueSaveRequest IssueRequest(int fromBalLocId, decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines = [IssueLine(fromBalLocId, qty)]
        };

    private static IvMiscIssueLineRequest IssueLine(int fromBalLocId, decimal qty) =>
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
            UnitPrice = 1m
        };

    private IvMiscIssueService CreateMi(DateTime? postingBusinessDate = null, DateTime? draftBusinessDate = null)
    {
        var access = Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance,
            currentDate: new FixedCurrentDateService(postingBusinessDate ?? DateTime.MaxValue.Date));

        return new IvMiscIssueService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(draftBusinessDate ?? FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            NullLogger<IvMiscIssueService>.Instance);
    }

    private IvInventoryPostingService CreatePostingService() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance, currentDate: new FixedCurrentDateService(DateTime.MaxValue.Date));

    private IIvInventoryLookupService CreateLookups()
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.CompanyCode).Returns("DEMO");
        current.SetupGet(x => x.BranchCode).Returns("HQ");
        return new IvInventoryLookupService(
            current.Object,
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            _factory);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
