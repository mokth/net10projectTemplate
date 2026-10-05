using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Data.Common;

namespace ErpWeb.Tests.Production.Transaction;

internal static class ProductionLedgerTestFixture
{
    public const string TestPriceEvidence = "TEST_EXPLICIT_COMPANY_BASE_PRICE";

    public static async Task SeedActiveEpochAsync(
        AppDbContext db,
        string company = "DEMO",
        string branch = "HQ",
        DateTime? effectiveFrom = null)
    {
        if (await db.StockLedgerEpochs.AnyAsync(x =>
            x.CompanyCode == company
            && x.BranchCode == branch
            && x.Status == StockLedgerEpochStatuses.Active))
            return;

        db.StockLedgerEpochs.Add(new StockLedgerEpoch
        {
            CompanyCode = company,
            BranchCode = branch,
            EffectiveFrom = effectiveFrom ?? new DateTime(2026, 9, 1),
            Version = 2,
            Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64),
            ActivatedAtUtc = DateTime.UtcNow,
            ActivatedBy = "TEST",
        });
        await db.SaveChangesAsync();
    }

    public static StockPostingCoordinator CreateCoordinator(
        IDbContextFactory<AppDbContext> factory,
        string company = "DEMO",
        string branch = "HQ",
        IInventoryValuationService? valuation = null) =>
        new(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            new BranchStockTransactionLock(),
            new StockPeriodGuard(),
            new NoActiveStockFreezeGuard(),
            valuation);

    public static IvInventoryPostingService CreateInventoryPosting(
        IDbContextFactory<AppDbContext> factory,
        string company = "DEMO",
        string branch = "HQ",
        IInventoryValuationService? valuation = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var access = Allow(MenuCodes.PlanningMaterialIssue);
        return new IvInventoryPostingService(
            factory,
            tenant,
            access,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance,
            new IvInventoryHistoryWriter(),
            CreateCoordinator(factory, company, branch, valuation));
    }

    public static ProductionOutputService CreateProductionOutputService(
        IDbContextFactory<AppDbContext> factory,
        string company = "DEMO",
        string branch = "HQ",
        IAccessRightService? access = null,
        IInventoryValuationService? valuation = null) =>
        new(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            access ?? Allow(MenuCodes.PlanningDailyProduction),
            new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            new TestRunningNumberService(),
            CreateCoordinator(factory, company, branch, valuation));

    public static async Task SeedVerifiedPoolAsync(AppDbContext db, ProductionBalLot lot)
    {
        var existing = await db.ProductionPoolValuationRows
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == lot.Uid);
        if (existing is null)
        {
            existing = new ProductionPoolValuation { ProductionBalLotId = lot.Uid };
            db.ProductionPoolValuationRows.Add(existing);
        }

        existing.Status = ProductionPoolValuationService.Verified;
        existing.TrackedBaseQty = lot.BaseQty;
        existing.TrackedValue = lot.TotalCost;
        await db.SaveChangesAsync();
    }

    public static IAccessRightService Allow(string menu)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(menu, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access.Object;
    }

    public static TestDbContextFactory CreateSqliteFactory(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);

    private sealed class TestRunningNumberService : IRunningNumberService
    {
        private int _next = 1;

        public Task<int> PeekNextAsync(
            AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_next);

        public Task<int> GetNextAsync(
            AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Increment(ref _next));
    }

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
