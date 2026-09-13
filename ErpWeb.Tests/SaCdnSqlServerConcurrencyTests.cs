using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using CdnStatuses = ErpWeb.Core.Sales.SaCdnStatuses;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

// ─────────────────────────── Decorator for test #4 ───────────────────────────

/// <summary>
/// Wraps a real <see cref="IvInventoryPostingService"/> but always returns a
/// failure from <see cref="RollBackStockInInTransactionAsync"/>. Used to verify
/// that when the stock rollback fails the CN and CR batch both stay POSTED.
/// </summary>
internal sealed class FailRollbackInventoryPostingService : IIvInventoryPostingService
{
    private readonly IIvInventoryPostingService _inner;

    public FailRollbackInventoryPostingService(IIvInventoryPostingService inner) =>
        _inner = inner;

    public Task<IvInventoryPostingResult> PostAsync(
        string trxType,
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default) =>
        _inner.PostAsync(trxType, batchNos, cancellationToken);

    public Task<IvInventoryPostingResult> RollbackAsync(
        string trxType,
        IReadOnlyList<int> batchNos,
        CancellationToken cancellationToken = default) =>
        _inner.RollbackAsync(trxType, batchNos, cancellationToken);

    public Task<IvInventoryPostingBatchResult> PostStockOutInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default) =>
        _inner.PostStockOutInTransactionAsync(db, companyCode, branchCode, userId, batchNo, expectedTrxType, cancellationToken);

    public Task<IvInventoryPostingBatchResult> RollBackStockOutInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default) =>
        _inner.RollBackStockOutInTransactionAsync(db, companyCode, branchCode, userId, batchNo, expectedTrxType, cancellationToken);

    public Task<IvInventoryPostingBatchResult> PostStockInInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default) =>
        _inner.PostStockInInTransactionAsync(db, companyCode, branchCode, userId, batchNo, expectedTrxType, cancellationToken);

    /// <summary>Always fails — simulates a crash/hardware failure during CR rollback.</summary>
    public Task<IvInventoryPostingBatchResult> RollBackStockInInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string userId,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(IvInventoryPostingBatchResult.Fail(batchNo, "Simulated stock rollback failure."));

    public Task DeleteNewStockInBatchInTransactionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        string expectedTrxType,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteNewStockInBatchInTransactionAsync(db, companyCode, branchCode, batchNo, expectedTrxType, cancellationToken);
}

// ─────────────────────────── Test class ───────────────────────────

/// <summary>
/// SQL Server concurrency / atomicity tests for Credit and Debit Notes (SaCdn).
/// Skipped silently unless ConnectionStrings:DefaultConnection points at SQL Server
/// with DEMO masters and the SaCdns table is present.
/// </summary>
public class SaCdnSqlServerConcurrencyTests
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    // ── Connection helpers ──────────────────────────────────────────

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs)) return null;
        if (!cs.Contains("Database=", StringComparison.OrdinalIgnoreCase)
            && !cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase))
            return null;
        return cs;
    }

    public static bool IsSqlServerAvailable()
    {
        var cs = GetSqlServerConnectionString();
        if (cs is null) return false;
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch { return false; }
    }

    private static IDbContextFactory<AppDbContext> CreateFactory()
    {
        var cs = GetSqlServerConnectionString()!;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        return new TestDbContextFactory(options);
    }

    // ── Fixture ──────────────────────────────────────────────────────

    private sealed record Fixture(string CustCode, string PayCode, string ServiceICode, string StockICode);

    /// <summary>
    /// Seeds a unique customer, a service item, and a stock item. When
    /// <paramref name="withStock"/> is <see langword="true"/> also creates an
    /// <see cref="IvBalLoc"/> row so the posting service can stock-in the CR.
    /// Returns <see langword="null"/> to skip the calling test if any required
    /// master is missing (warehouse, currency, class) or if the SaCdns table
    /// hasn't been created yet.
    /// </summary>
    private static async Task<Fixture?> SeedFixtureAsync(
        IDbContextFactory<AppDbContext> factory,
        bool withStock = false)
    {
        await using var db = factory.CreateDbContext();

        // Check SaCdns table is present (skips if scripts/create-sacdn.sql not applied).
        try { _ = await db.SaCdns.AnyAsync(); }
        catch { return null; }

        // DEMO masters
        if (!await db.IvWarehouses.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
            return null;

        if (!await db.SaCurrencies.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.CurrCode == "MYR" && x.IsActive == true))
            return null;

        var classCode = await db.IvClasses
            .Where(x => x.CompanyCode == "DEMO")
            .Select(x => x.IClassCode)
            .FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(classCode)) return null;

        // Unique pay code
        var payCode = "P" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        if (!await db.IvMsCodes.AnyAsync(x => x.CodeType == IvMsCodeTypes.PayCode && x.Code == payCode))
            db.IvMsCodes.Add(new IvMsCode { Code = payCode, Name = "Test pay", CodeType = IvMsCodeTypes.PayCode });

        // Unique customer
        var custCode = "C" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = custCode,
            CustName = "CDN concurrency",
            Currency = "MYR",
            PayCode = payCode,
            IsActive = true
        });

        // Service item (StockControl=false) — credit-only CN tests
        var serviceICode = "SV" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = serviceICode,
            IDesc = "CDN service",
            IClassCode = classCode,
            StdUom = "EA",
            StockControl = false,
            IsActive = true,
            SellingPrice = 10m,
            SellingGlCode = "GLSVC"
        });

        // Stock item (StockControl=true) — ReturnStock tests
        var stockICode = "SK" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = stockICode,
            IDesc = "CDN stock item",
            IClassCode = classCode,
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            SellingPrice = 10m,
            SellingGlCode = "GLSALE",
            DefWarehouse = "MAIN"
        });

        await db.SaveChangesAsync();

        if (withStock)
        {
            var loc = await db.IvLocations
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                    && x.WarehouseCode == "MAIN" && x.IsActive)
                .Select(x => x.LocCode)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(loc)) return null;

            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                ICode = stockICode,
                WhCode = "MAIN",
                LocCode = loc,
                LotNo = string.Empty,
                IStatus = IvItemStatuses.Active,
                StdQty = 100m,
                StdUom = "EA",
                TransDate = FixedToday.AddDays(-10),
                LocationCode = "SITE"
            });
            await db.SaveChangesAsync();
        }

        // Ensure AdSmNumDate for INV, CN, DN for DEMO/HQ 2026/9
        foreach (var numCd in new[] { "INV", "CN", "DN" })
        {
            if (!await db.AdSmNumDates.AnyAsync(x =>
                    x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                    && x.NumCd == numCd && x.Year == 2026 && x.Month == 9))
            {
                db.AdSmNumDates.Add(new AdSmNumDate
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    LocationCode = "MAIN",
                    Year = 2026,
                    Month = 9,
                    NumCd = numCd,
                    NumDes = numCd,
                    Prefix = numCd,
                    TotLength = 4,
                    NumberingDelimeter = "-",
                    Seq = 1
                });
                try { await db.SaveChangesAsync(); }
                catch { /* concurrent test may have already inserted it */ }
            }
        }

        return new Fixture(custCode, payCode, serviceICode, stockICode);
    }

    // ── SUT factories ──────────────────────────────────────────────

    private static SaCdnService CreateCdnSut(
        IDbContextFactory<AppDbContext> factory,
        IIvInventoryPostingService? posting = null)
    {
        var access = AllowAll();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        var postingRepo = new IvStockPostingRepository();
        var realPosting = new IvInventoryPostingService(
            factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);
        return new SaCdnService(
            factory,
            tenant,
            access.Object,
            new DocumentNumberingService(tenant),
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new SaCdnRepository(),
            new SaInvoiceRepository(),
            postingRepo,
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            posting ?? realPosting,
            NullLogger<SaCdnService>.Instance);
    }

    private static SaInvoiceService CreateInvoiceSut(IDbContextFactory<AppDbContext> factory)
    {
        var access = AllowAll();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        var postingRepo = new IvStockPostingRepository();
        var salesOrders = new SaSoRepository();
        var docApplication = new SaDocApplicationService(salesOrders, new SaDoRepository());
        var posting = new IvInventoryPostingService(
            factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);
        return new SaInvoiceService(
            factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new SaInvoiceRepository(),
            new SaCustRepository(factory),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            salesOrders,
            docApplication,
            new SaCustLookupService(factory, tenant),
            NullLogger<SaInvoiceService>.Instance);
    }

    private static Mock<IAccessRightService> AllowAll()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    // ── Request builders ──────────────────────────────────────────

    private static SaCdnSaveRequest CnRequest(
        Fixture fixture,
        decimal qty,
        decimal price,
        string? invNo = null,
        byte[]? rowVersion = null) =>
        new()
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = fixture.CustCode,
            Currency = "MYR",
            InvNo = invNo,
            RowVersion = rowVersion,
            Lines = [new SaCdnLineRequest { ICode = fixture.ServiceICode, Qty = qty, UnitPrice = price }]
        };

    private static SaCdnSaveRequest ReturnStockCnRequest(Fixture fixture, decimal qty, string locCode) =>
        new()
        {
            Type = "CN",
            DocDate = FixedToday,
            CustCode = fixture.CustCode,
            Currency = "MYR",
            ReturnStock = true,
            Lines =
            [
                new SaCdnLineRequest
                {
                    ICode = fixture.StockICode,
                    Qty = qty,
                    UnitPrice = 10m,
                    FrWarehouse = "MAIN",
                    LocCode = locCode,
                    IStatus = IvItemStatuses.Active
                }
            ]
        };

    // ── Seed helpers ──────────────────────────────────────────────

    /// <summary>
    /// Saves and posts a simple service-only invoice. Returns (InvNo, TotAmnt).
    /// </summary>
    private static async Task<(string InvNo, decimal TotAmnt)> SeedPostedInvoiceAsync(
        IDbContextFactory<AppDbContext> factory,
        Fixture fixture,
        decimal qty,
        decimal price)
    {
        var inv = CreateInvoiceSut(factory);
        var req = new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = fixture.CustCode,
            Currency = "MYR",
            PayCode = fixture.PayCode,
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = fixture.ServiceICode,
                    Qty = qty,
                    UnitPrice = price,
                    FrWarehouse = "MAIN"
                }
            ]
        };
        var save = await inv.SaveNewAsync(req);
        if (!save.Succeeded)
            throw new InvalidOperationException("Seed invoice save failed: " + save.ErrorMessage);
        var post = await inv.PostAsync([save.InvNo!]);
        if (!post.Succeeded)
            throw new InvalidOperationException("Seed invoice post failed: " + post.ErrorMessage);

        await using var db = factory.CreateDbContext();
        var row = await db.SaInvoices.AsNoTracking().FirstAsync(x => x.InvNo == save.InvNo);
        return (save.InvNo!, row.TotAmnt);
    }

    // ── Tests ────────────────────────────────────────────────────────

    /// <summary>
    /// Two parallel SaveNewAsync CNs each for ~80 against the same invoice of ~100.
    /// Exactly one must succeed; the committed CN total must not exceed the invoice total.
    /// </summary>
    [Fact]
    public async Task SqlServer_concurrent_remaining_overcredit_one_winner()
    {
        if (!IsSqlServerAvailable()) return;

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory);
        if (fixture is null) return;

        // Seed invoice: qty=10 × price=10 → TotAmnt=100
        var (invNo, invTotAmnt) = await SeedPostedInvoiceAsync(factory, fixture, qty: 10m, price: 10m);
        Assert.True(invTotAmnt > 0m, "Seed invoice must have a positive total.");

        // Two concurrent CNs of ~80 against the same invoice
        var sutA = CreateCdnSut(factory);
        var sutB = CreateCdnSut(factory);
        var results = await Task.WhenAll(
            sutA.SaveNewAsync(CnRequest(fixture, qty: 8m, price: 10m, invNo: invNo)),
            sutB.SaveNewAsync(CnRequest(fixture, qty: 8m, price: 10m, invNo: invNo)));

        Assert.Equal(1, results.Count(x => x.Succeeded));

        // Sum of committed CN TotAmnt (NEW + POSTED) for that InvNo ≤ invoice TotAmnt
        await using var db = factory.CreateDbContext();
        var committedTotal = await db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                && x.InvNo == invNo
                && (x.Status == CdnStatuses.New || x.Status == CdnStatuses.Posted))
            .SumAsync(x => (decimal?)x.TotAmnt) ?? 0m;
        Assert.True(committedTotal <= invTotAmnt,
            $"Committed CN total {committedTotal} exceeds invoice {invTotAmnt}.");
    }

    /// <summary>
    /// Save a NEW CN, then fire two parallel PostAsync with the same RowVersion.
    /// Exactly one posting succeeds; the other returns Concurrency or StatusConflict.
    /// </summary>
    [Fact]
    public async Task SqlServer_duplicate_Post_one_success_one_status_conflict()
    {
        if (!IsSqlServerAvailable()) return;

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory);
        if (fixture is null) return;

        var sut = CreateCdnSut(factory);
        var save = await sut.SaveNewAsync(CnRequest(fixture, qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var rv = (byte[])save.Document!.RowVersion.Clone();

        var sutA = CreateCdnSut(factory);
        var sutB = CreateCdnSut(factory);
        var results = await Task.WhenAll(
            sutA.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = rv }]),
            sutB.PostAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = rv }]));

        Assert.Equal(1, results.Count(x => x.Succeeded));

        var failed = results.First(x => !x.Succeeded);
        var item = failed.Posting.Single(x => x.DocNo == save.DocNo);
        Assert.True(
            item.ReasonCode == SaCdnReasonCodes.Concurrency
            || item.ReasonCode == SaCdnReasonCodes.StatusConflict,
            $"Expected {SaCdnReasonCodes.Concurrency} or {SaCdnReasonCodes.StatusConflict}, got: {item.ReasonCode}");
    }

    /// <summary>
    /// Post a CN to POSTED, then fire two parallel RollbackAsync with the same
    /// RowVersion.  Exactly one rollback succeeds; the CN ends up NEW exactly once.
    /// </summary>
    [Fact]
    public async Task SqlServer_Post_vs_Rollback_one_winner()
    {
        if (!IsSqlServerAvailable()) return;

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory);
        if (fixture is null) return;

        var sut = CreateCdnSut(factory);

        // Save and post the CN
        var save = await sut.SaveNewAsync(CnRequest(fixture, qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await sut.PostAsync(
            [new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // Reload to get the fresh RowVersion after posting
        var fresh = await sut.GetAsync(save.DocNo!);
        Assert.True(fresh.Succeeded, fresh.ErrorMessage);
        var freshRv = (byte[])fresh.Document!.RowVersion.Clone();

        // Two concurrent RollbackAsync with the same RowVersion
        var sutA = CreateCdnSut(factory);
        var sutB = CreateCdnSut(factory);
        var results = await Task.WhenAll(
            sutA.RollbackAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = freshRv }]),
            sutB.RollbackAsync([new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = freshRv }]));

        Assert.Equal(1, results.Count(x => x.Succeeded));

        // CN status must be NEW exactly once
        await using var db = factory.CreateDbContext();
        var cdnAfter = await db.SaCdns.AsNoTracking().SingleAsync(x => x.DocNo == save.DocNo);
        Assert.Equal(CdnStatuses.New, cdnAfter.Status);
    }

    /// <summary>
    /// Post a ReturnStock CN (creates POSTED CR batch).  Wrap posting so
    /// RollBackStockInInTransactionAsync returns Succeeded=false.
    /// RollbackAsync must fail, and both the CN and the CR batch must remain POSTED.
    /// </summary>
    [Fact]
    public async Task SqlServer_rollback_inventory_fail_both_stay_POSTED()
    {
        if (!IsSqlServerAvailable()) return;

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null) return;

        // Resolve an active location
        string loc;
        await using (var db = factory.CreateDbContext())
        {
            loc = await db.IvLocations
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                    && x.WarehouseCode == "MAIN" && x.IsActive)
                .Select(x => x.LocCode)
                .FirstOrDefaultAsync() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(loc)) return;

        // Save and post a ReturnStock CN (normal service)
        var sut = CreateCdnSut(factory);
        var save = await sut.SaveNewAsync(ReturnStockCnRequest(fixture, qty: 5m, loc));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await sut.PostAsync(
            [new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // Now use the decorator that fails the stock rollback
        var failPosting = new FailRollbackInventoryPostingService(
            new IvInventoryPostingService(
                factory,
                InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
                AllowAll().Object,
                new IvStockPostingRepository(),
                new IvStockCommonRepository(factory),
                new PoOrderRepository(),
                NullLogger<IvInventoryPostingService>.Instance));

        var failSut = CreateCdnSut(factory, failPosting);
        var curr = await failSut.GetAsync(save.DocNo!);
        Assert.True(curr.Succeeded, curr.ErrorMessage);

        var rb = await failSut.RollbackAsync(
            [new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = curr.Document!.RowVersion }]);
        Assert.False(rb.Succeeded);

        // Both CN and CR batch must remain POSTED
        await using var db2 = factory.CreateDbContext();
        var cdnAfter = await db2.SaCdns.AsNoTracking().SingleAsync(x => x.DocNo == save.DocNo);
        Assert.Equal(CdnStatuses.Posted, cdnAfter.Status);

        var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
        var crBatch = await db2.IvTrxBatches.AsNoTracking()
            .SingleAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                && x.RefNo == refNo && x.TrxType == IvTrxTypes.CustomerReturn);
        Assert.Equal(IvBatchStatuses.Posted, crBatch.BatchStatus);
    }

    /// <summary>
    /// Post a ReturnStock CN with TestHookAfterStockIn set to throw.
    /// The post must fail and the whole transaction must roll back:
    /// CN stays NEW and no POSTED IvTrxBatch exists for that CN.
    /// </summary>
    [Fact]
    public async Task SqlServer_CR_stock_in_then_CN_fail_full_rollback()
    {
        if (!IsSqlServerAvailable()) return;

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null) return;

        string loc;
        await using (var db = factory.CreateDbContext())
        {
            loc = await db.IvLocations
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                    && x.WarehouseCode == "MAIN" && x.IsActive)
                .Select(x => x.LocCode)
                .FirstOrDefaultAsync() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(loc)) return;

        var sut = CreateCdnSut(factory);
        var save = await sut.SaveNewAsync(ReturnStockCnRequest(fixture, qty: 5m, loc));
        Assert.True(save.Succeeded, save.ErrorMessage);

        // Install hook: throws after stock-in succeeds but before CN is marked POSTED
        sut.TestHookAfterStockIn = () => throw new InvalidOperationException("Forced mid-post failure");

        var post = await sut.PostAsync(
            [new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.False(post.Succeeded);

        // CN must still be NEW
        await using var db2 = factory.CreateDbContext();
        var cdnAfter = await db2.SaCdns.AsNoTracking().SingleAsync(x => x.DocNo == save.DocNo);
        Assert.Equal(CdnStatuses.New, cdnAfter.Status);

        // No POSTED IvTrxBatch for this CN (the whole transaction was rolled back)
        var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
        var postedBatchCount = await db2.IvTrxBatches.AsNoTracking()
            .CountAsync(x =>
                x.RefNo == refNo
                && x.TrxType == IvTrxTypes.CustomerReturn
                && x.BatchStatus == IvBatchStatuses.Posted);
        Assert.Equal(0, postedBatchCount);
    }

    /// <summary>
    /// Classic stale RowVersion: update once to bump the version, then update again
    /// with the original token — must fail with Concurrency.
    /// </summary>
    [Fact]
    public async Task SqlServer_stale_RowVersion_Update_fails()
    {
        if (!IsSqlServerAvailable()) return;

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory);
        if (fixture is null) return;

        var sut = CreateCdnSut(factory);
        var save = await sut.SaveNewAsync(CnRequest(fixture, qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var staleRv = (byte[])save.Document!.RowVersion.Clone();

        // First update — must succeed and bump the RowVersion
        var first = await sut.UpdateAsync(
            save.DocNo!,
            CnRequest(fixture, qty: 3m, price: 10m, rowVersion: save.Document.RowVersion));
        Assert.True(first.Succeeded, first.ErrorMessage);

        // Second update with the stale token — must fail
        var second = await sut.UpdateAsync(
            save.DocNo!,
            CnRequest(fixture, qty: 4m, price: 10m, rowVersion: staleRv));
        Assert.False(second.Succeeded);
        Assert.Equal(SaCdnErrorKind.Concurrency, second.ErrorKind);
    }

    /// <summary>
    /// After a successful ReturnStock post the database must contain exactly one
    /// CR batch with <c>RefNo == CN/{docNo}</c>. Verifies no duplicates can arise.
    /// </summary>
    [Fact]
    public async Task SqlServer_unique_CR_RefNo_after_ReturnStock_post()
    {
        if (!IsSqlServerAvailable()) return;

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory, withStock: true);
        if (fixture is null) return;

        string loc;
        await using (var db = factory.CreateDbContext())
        {
            loc = await db.IvLocations
                .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                    && x.WarehouseCode == "MAIN" && x.IsActive)
                .Select(x => x.LocCode)
                .FirstOrDefaultAsync() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(loc)) return;

        var sut = CreateCdnSut(factory);
        var save = await sut.SaveNewAsync(ReturnStockCnRequest(fixture, qty: 5m, loc));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await sut.PostAsync(
            [new SaCdnKeyedRequest { DocNo = save.DocNo!, RowVersion = save.Document!.RowVersion }]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        var refNo = SaCdnSpRefs.ToRefNo(save.DocNo!);
        await using var db2 = factory.CreateDbContext();
        var crCount = await db2.IvTrxBatches.AsNoTracking()
            .CountAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                && x.RefNo == refNo
                && x.TrxType == IvTrxTypes.CustomerReturn);
        Assert.Equal(1, crCount);
    }
}
