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
/// <summary>
/// The period-close guard: posting and rollback refuse a batch whose <c>TrxDtTime</c> falls in a CLOSED
/// period for the tenant — keyed on the batch's OWN date, never on the execution date (Critical finding 2).
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPeriodClose)]
public class IvPeriodCloseGuardTests : IAsyncLifetime
{
    private static readonly DateTime AugustFrom = new(2026, 8, 1);
    private static readonly DateTime AugustTo = new(2026, 8, 31);
    private static readonly DateTime InAugust = new(2026, 8, 20, 10, 0, 0);
    private static readonly DateTime AfterAugust = new(2026, 9, 5, 10, 0, 0);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private int _nextBatchNo = 1;

    public IvPeriodCloseGuardTests()
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

    /// <summary>
    /// The four types whose post/rollback cores are reached directly by the dispatcher (the NG core is
    /// reached only after the goods-receipt wrapper's PO-link checks, so it is covered by the direct
    /// guard test below instead — the guard helper itself is type-agnostic).
    /// </summary>
    public static TheoryData<string> StockTypes => new()
    {
        IvTrxTypes.MiscellaneousReceipt,
        IvTrxTypes.StockAdjustment,
        IvTrxTypes.MiscellaneousIssue,
        IvTrxTypes.StockTransfer
    };

    // ── Posting guard ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(StockTypes))]
    public async Task Post_refuses_a_batch_dated_in_a_closed_period(string trxType)
    {
        await SeedClosedPeriodAsync("DEMO", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Closed);
        var batchNo = await SeedBatchAsync(trxType, InAugust, IvBatchStatuses.New);

        var result = await CreatePosting().PostAsync(trxType, [batchNo]);

        Assert.False(result.Succeeded);
        Assert.Contains("2026-08-20", result.ErrorMessage);
        Assert.Contains("2026-08-31", result.ErrorMessage);
        Assert.Contains("closed inventory period", result.ErrorMessage);
    }

    [Fact]
    public async Task Post_allows_a_batch_dated_after_the_closed_period()
    {
        await SeedClosedPeriodAsync("DEMO", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Closed);
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = AfterAugust,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = "A100",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    Quantity = 10m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    Reason = "ADJ"
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await mr.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
    }

    // ── Rollback guard ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(StockTypes))]
    public async Task Rollback_refuses_a_batch_dated_in_a_closed_period(string trxType)
    {
        await SeedClosedPeriodAsync("DEMO", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Closed);
        var batchNo = await SeedBatchAsync(trxType, InAugust, IvBatchStatuses.Posted);

        var result = await CreatePosting().RollbackAsync(trxType, [batchNo]);

        Assert.False(result.Succeeded);
        Assert.Contains("2026-08-20", result.ErrorMessage);
        Assert.Contains("2026-08-31", result.ErrorMessage);
    }

    // ── Boundary / no-row / reopened / tenant isolation ───────────────────────────────────────────

    [Theory]
    [InlineData("2026-08-31T23:59:59", false)] // last instant of the closed period — refused
    [InlineData("2026-09-01T00:00:00", true)]  // first instant after the closed period — allowed
    public async Task Boundary_dates_are_decided_on_the_date_component(string isoDate, bool shouldPost)
    {
        await SeedClosedPeriodAsync("DEMO", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Closed);
        var date = DateTime.Parse(isoDate, System.Globalization.CultureInfo.InvariantCulture);
        var batchNo = await SeedBatchAsync(IvTrxTypes.StockAdjustment, date, IvBatchStatuses.New);

        var result = await CreatePosting().PostAsync(IvTrxTypes.StockAdjustment, [batchNo]);

        Assert.Equal(shouldPost, !(result.ErrorMessage?.Contains("closed inventory period") ?? false));
    }

    [Fact]
    public async Task Guard_helper_is_type_agnostic_and_covers_the_ng_path()
    {
        await SeedClosedPeriodAsync("DEMO", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Closed);
        await using var db = await _factory.CreateDbContextAsync();

        // The same helper serves every trx type — including NG, whose core is reached only after the
        // goods-receipt wrapper's PO-link checks. Keyed on the DATE component, never on today.
        Assert.NotNull(await IvPeriodCloseGuard.EnsureOpenAsync(db, "DEMO", "HQ", InAugust));
        Assert.Null(await IvPeriodCloseGuard.EnsureOpenAsync(db, "DEMO", "HQ", AfterAugust));
        Assert.Equal(
            AugustTo,
            await IvPeriodCloseGuard.ClosedThroughAsync(db, "DEMO", "HQ"));
    }

    [Fact]
    public async Task No_closed_header_allows_posting()
    {
        var batchNo = await SeedBatchAsync(IvTrxTypes.StockAdjustment, InAugust, IvBatchStatuses.New);

        var result = await CreatePosting().PostAsync(IvTrxTypes.StockAdjustment, [batchNo]);

        Assert.False(result.ErrorMessage?.Contains("closed inventory period") ?? false);
    }

    [Fact]
    public async Task A_reopened_header_guards_nothing()
    {
        await SeedClosedPeriodAsync("DEMO", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Reopened);
        var batchNo = await SeedBatchAsync(IvTrxTypes.StockAdjustment, InAugust, IvBatchStatuses.New);

        var result = await CreatePosting().PostAsync(IvTrxTypes.StockAdjustment, [batchNo]);

        Assert.False(result.ErrorMessage?.Contains("closed inventory period") ?? false);
    }

    [Fact]
    public async Task Tenant_isolation_another_company_closed_period_does_not_block()
    {
        await SeedClosedPeriodAsync("OTHER", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Closed);
        var batchNo = await SeedBatchAsync(IvTrxTypes.StockAdjustment, InAugust, IvBatchStatuses.New);

        var result = await CreatePosting().PostAsync(IvTrxTypes.StockAdjustment, [batchNo]);

        Assert.False(result.ErrorMessage?.Contains("closed inventory period") ?? false);
    }

    // ── S3 acceptance test: rollback after close must refuse and mutate NOTHING ───────────────────

    [Fact]
    public async Task Rollback_after_close_refuses_and_mutates_nothing()
    {
        // Post an MR dated 2026-08-20, then close August.
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = InAugust,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = "A100",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    Quantity = 100m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    Reason = "ADJ"
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var historyId = await db.IvTrxHistories.Select(x => x.Id).SingleAsync();
            var balId = await db.IvBalLocs.Select(x => x.Id).SingleAsync();
            await db.IvPeriodCloseHdrs.AddAsync(new IvPeriodCloseHdr
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                PeriodFrom = AugustFrom,
                PeriodTo = AugustTo,
                Status = IvPeriodCloseStatuses.Closed,
                ClosedBy = "admin",
                ClosedOn = new DateTime(2026, 8, 31, 23, 59, 59)
            });
            await db.SaveChangesAsync();
            var stdQtyBefore = await db.IvBalLocs.Select(x => x.StdQty).SingleAsync();
            Assert.Equal(100m, stdQtyBefore);
        }

        // The rollback is attempted on a simulated 2026-09-05 — but the guard keys on batch.TrxDtTime
        // (August), never on the execution date, so it must refuse and leave every record untouched.
        var rb = await CreatePosting().RollbackAsync(IvTrxTypes.MiscellaneousReceipt, [save.BatchNo]);

        Assert.False(rb.Succeeded);
        Assert.Contains("2026-08-20", rb.ErrorMessage);
        Assert.Contains("2026-08-31", rb.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo);
            Assert.Equal(IvBatchStatuses.Posted, batch.BatchStatus);
            Assert.Equal(0, batch.RollbackCount);
            Assert.Null(batch.RollbackOperationId);

            Assert.Equal(1, await db.IvTrxHistories.CountAsync());
            Assert.Equal(100m, await db.IvBalLocs.Select(x => x.StdQty).SingleAsync());
        }
    }

    [Fact]
    public async Task Rollback_succeeds_once_the_period_is_reopened()
    {
        var mr = CreateMr();
        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = InAugust,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = "A100",
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    Quantity = 100m,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    Reason = "ADJ"
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await mr.PostAsync([save.BatchNo])).Succeeded);

        // Close then reopen August — after the reopen the same rollback must succeed.
        await SeedClosedPeriodAsync("DEMO", "HQ", AugustFrom, AugustTo, IvPeriodCloseStatuses.Reopened);

        var rb = await CreatePosting().RollbackAsync(IvTrxTypes.MiscellaneousReceipt, [save.BatchNo]);
        Assert.True(rb.Succeeded, rb.ErrorMessage);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────

    private IIvInventoryPostingService CreatePosting() =>
        new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);

    private IvMiscReceiptService CreateMr()
    {
        var access = Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory, tenant, access.Object, postingRepo,
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvMiscReceiptService(
            _factory, tenant, access.Object, new RunningNumberService(),
            new FixedCurrentDateService(AfterAugust.Date),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo, posting,
            new PoSupplierRepository(_factory),
            new IvUomConversionService(_factory),
            NullLogger<IvMiscReceiptService>.Instance);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private async Task SeedClosedPeriodAsync(
        string company,
        string branch,
        DateTime from,
        DateTime to,
        string status)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvPeriodCloseHdrs.Add(new IvPeriodCloseHdr
        {
            CompanyCode = company,
            BranchCode = branch,
            PeriodFrom = from.Date,
            PeriodTo = to.Date,
            Status = status,
            ClosedBy = "admin",
            ClosedOn = to.Date.AddHours(23)
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedBatchAsync(string trxType, DateTime trxDtTime, string batchStatus)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var batchNo = _nextBatchNo++;
        db.IvTrxBatches.Add(new IvTrxBatch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batchNo,
            TrxDtTime = trxDtTime,
            TrxType = trxType,
            BatchStatus = batchStatus,
            RefNo = $"G{batchNo}"
        });
        await db.SaveChangesAsync();
        return batchNo;
    }
}
