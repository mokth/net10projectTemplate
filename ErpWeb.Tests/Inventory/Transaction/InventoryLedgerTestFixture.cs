using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Data.Common;

namespace ErpWeb.Tests.Inventory.Transaction;

/// <summary>
/// Ledger-enabled inventory posting harness. Physical posting tests that omit the coordinator
/// cannot prove <see cref="StockValuationFact"/> behavior; use this fixture when a test must
/// exercise Moving Average, FIFO, or Standard valuation.
/// </summary>
internal static class InventoryLedgerTestFixture
{
    public const string CompanyCode = "DEMO";
    public const string BranchCode = "HQ";
    public const string BaseCurrency = "MYR";
    public static readonly DateTime PolicyEffectiveFrom = new(2026, 1, 1);

    public static TestDbContextFactory CreateSqliteFactory(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);

    public static async Task SeedCompanyAsync(
        AppDbContext db,
        string company = CompanyCode,
        string currency = BaseCurrency)
    {
        if (await db.Companies.AnyAsync(x => x.CompanyCode == company))
            return;

        db.Companies.Add(new Company
        {
            CompanyCode = company,
            CompanyName = "Demo Company",
            CurrencyCode = currency,
            IsActive = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "TEST"
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedActiveEpochAsync(
        AppDbContext db,
        string company = CompanyCode,
        string branch = BranchCode,
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
            EffectiveFrom = effectiveFrom ?? PolicyEffectiveFrom,
            Version = 2,
            Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64),
            ActivatedAtUtc = DateTime.UtcNow,
            ActivatedBy = "TEST"
        });
        await db.SaveChangesAsync();
    }

    public static Task SeedMovingAveragePolicyAsync(
        AppDbContext db,
        string company = CompanyCode,
        string branch = BranchCode) =>
        SeedCostPolicyAsync(db, StockCostMethods.MovingAverage, company, branch);

    public static Task SeedFifoPolicyAsync(
        AppDbContext db,
        string company = CompanyCode,
        string branch = BranchCode) =>
        SeedCostPolicyAsync(db, StockCostMethods.Fifo, company, branch);

    public static Task SeedStandardPolicyAsync(
        AppDbContext db,
        string company = CompanyCode,
        string branch = BranchCode) =>
        SeedCostPolicyAsync(db, StockCostMethods.Standard, company, branch);

    public static async Task SeedCostPolicyAsync(
        AppDbContext db,
        string costMethod,
        string company = CompanyCode,
        string branch = BranchCode,
        DateTime? effectiveFrom = null)
    {
        if (!StockCostMethods.All.Contains(costMethod))
            throw new ArgumentException($"Unsupported cost method '{costMethod}'.", nameof(costMethod));

        var active = await db.StockCostPolicyRevisions
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.Status == StockCostPolicyStatuses.Active)
            .ToListAsync();
        foreach (var row in active)
            row.Status = StockCostPolicyStatuses.Superseded;

        db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
        {
            CompanyCode = company,
            BranchCode = branch,
            CostMethod = costMethod,
            EffectiveFrom = (effectiveFrom ?? PolicyEffectiveFrom).Date,
            Status = StockCostPolicyStatuses.Active,
            ApprovedBy = "TEST",
            ApprovedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST",
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedCostStateAsync(
        AppDbContext db,
        string itemCode,
        decimal onHandQty,
        decimal unitCost,
        string costMethod = StockCostMethods.MovingAverage,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        var value = RoundMoney(onHandQty * unitCost);
        var average = onHandQty == 0m ? 0m : RoundMoney(value / onHandQty);
        db.StockCostStates.Add(new StockCostState
        {
            CompanyCode = company,
            BranchCode = branch,
            ItemCode = itemCode,
            CostMethod = costMethod,
            OnHandBaseQty = onHandQty,
            InventoryValue = value,
            AverageUnitCost = average,
            CurrentUnitCost = average,
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedStandardCostAsync(
        AppDbContext db,
        string itemCode,
        decimal totalStandardCost,
        string company = CompanyCode,
        string branch = BranchCode,
        DateTime? effectiveFrom = null)
    {
        db.ItemStandardCostRevisions.Add(new ItemStandardCostRevision
        {
            CompanyCode = company,
            BranchCode = branch,
            ItemCode = itemCode,
            EffectiveFrom = (effectiveFrom ?? PolicyEffectiveFrom).Date,
            MaterialCost = totalStandardCost,
            TotalStandardCost = totalStandardCost,
            Status = ItemStandardCostRevisionStatuses.Approved,
            Revision = 1,
            ApprovedBy = "TEST",
            ApprovedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST",
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    public static async Task SeedFifoPoolAsync(
        AppDbContext db,
        string itemCode,
        IReadOnlyList<FifoLayerSeed> layers,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        if (layers.Count == 0)
            throw new ArgumentException("At least one FIFO layer is required.", nameof(layers));

        var epochId = await db.StockLedgerEpochs
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.Status == StockLedgerEpochStatuses.Active)
            .Select(x => x.Id)
            .SingleAsync();
        var ordered = layers.OrderBy(x => x.EffectiveAt).ToArray();
        var sequence = await ReserveSequenceAsync(db, company, branch);
        var earliest = ordered[0].EffectiveAt;
        var documentNo = $"OPEN-{itemCode}";
        var posting = new StockPosting
        {
            CompanyCode = company,
            BranchCode = branch,
            LedgerEpochId = epochId,
            PostingSequence = sequence,
            RequestId = Guid.NewGuid(),
            CommandType = "OPENING_SEED",
            RequestFingerprint = new string('B', 64),
            SourceModule = "INVENTORY",
            SourceDocumentType = "OPENING",
            SourceDocumentId = documentNo,
            SourceDocumentNo = documentNo,
            DocumentRevision = 1,
            PostingRole = "PRIMARY",
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('C', 64),
            EffectiveAt = earliest,
            BusinessDate = earliest.Date,
            PeriodKey = earliest.ToString("yyyy-MM"),
            PostedAtUtc = DateTime.UtcNow,
            PostedBy = "TEST",
            SealedAtUtc = DateTime.UtcNow
        };
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();

        var facts = new List<StockValuationFact>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var layer = ordered[i];
            var amount = RoundMoney(layer.Qty * layer.UnitCost);
            facts.Add(new StockValuationFact
            {
                CompanyCode = company,
                BranchCode = branch,
                LedgerEpochId = epochId,
                StockPostingId = posting.Id,
                PostingLineNo = i + 1,
                SplitOrdinal = 0,
                SourceLineId = (i + 1).ToString(),
                SourceDocumentType = "OPENING",
                SourceDocumentId = documentNo,
                SourceDocumentNo = documentNo,
                SourceDocumentLine = (i + 1).ToString(),
                EffectiveAt = layer.EffectiveAt,
                BusinessDate = layer.EffectiveAt.Date,
                PeriodKey = layer.EffectiveAt.ToString("yyyy-MM"),
                ItemCode = itemCode,
                BaseUom = "EA",
                MovementCode = "RECEIPT_IN",
                Direction = 1,
                BaseQty = layer.Qty,
                CostMethod = StockCostMethods.Fifo,
                UnitCost = layer.UnitCost,
                CostAmount = amount,
                BaseCostAmount = amount,
                BaseCurrency = BaseCurrency,
                ValuationSource = StockValuationSources.Fifo,
                ValuationStatus = StockValuationStatuses.Valued,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = "TEST"
            });
        }

        db.StockValuationFacts.AddRange(facts);
        await db.SaveChangesAsync();

        for (var i = 0; i < ordered.Length; i++)
        {
            var layer = ordered[i];
            var amount = facts[i].CostAmount;
            db.StockFifoLayers.Add(new StockFifoLayer
            {
                CompanyCode = company,
                BranchCode = branch,
                ItemCode = itemCode,
                BaseUom = "EA",
                OriginValuationFactId = facts[i].Id,
                OriginStockPostingId = posting.Id,
                ReceiptEffectiveAt = layer.EffectiveAt,
                OriginalQty = layer.Qty,
                RemainingQty = layer.Qty,
                OriginalValue = amount,
                RemainingValue = amount,
                CurrentUnitCost = layer.Qty == 0m ? 0m : RoundMoney(amount / layer.Qty),
                SourceDocumentType = "OPENING",
                SourceDocumentNo = documentNo,
                SourceDocumentLine = (i + 1).ToString(),
                Status = StockFifoLayerStatuses.Open,
                RowVersion = [1]
            });
        }

        var qty = ordered.Sum(x => x.Qty);
        var value = facts.Sum(x => x.CostAmount);
        db.StockCostStates.Add(new StockCostState
        {
            CompanyCode = company,
            BranchCode = branch,
            ItemCode = itemCode,
            CostMethod = StockCostMethods.Fifo,
            OnHandBaseQty = qty,
            InventoryValue = value,
            AverageUnitCost = qty == 0m ? 0m : RoundMoney(value / qty),
            CurrentUnitCost = qty == 0m ? 0m : RoundMoney(value / qty),
            LastPostingSequence = sequence,
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    public static StockPostingCoordinator CreateCoordinator(
        IDbContextFactory<AppDbContext> factory,
        string company = CompanyCode,
        string branch = BranchCode,
        IInventoryValuationService? valuation = null) =>
        new(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            new BranchStockTransactionLock(),
            new StockPeriodGuard(),
            new NoActiveStockFreezeGuard(),
            valuation ?? new InventoryValuationService());

    public static IvInventoryPostingService CreateInventoryPosting(
        IDbContextFactory<AppDbContext> factory,
        string company = CompanyCode,
        string branch = BranchCode,
        IAccessRightService? access = null,
        IInventoryValuationService? valuation = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        return new IvInventoryPostingService(
            factory,
            tenant,
            access ?? Allow(),
            new IvStockPostingRepository(),
            new IvStockCommonRepository(factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance,
            new IvInventoryHistoryWriter(),
            CreateCoordinator(factory, company, branch, valuation));
    }

    public static IvMiscIssueService CreateMiscIssue(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var access = Allow();
        var postingRepo = new IvStockPostingRepository();
        return new IvMiscIssueService(
            factory,
            tenant,
            access,
            new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreateInventoryPosting(factory, company, branch, access),
            NullLogger<IvMiscIssueService>.Instance);
    }

    public static IvScrapService CreateScrap(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var access = Allow();
        var postingRepo = new IvStockPostingRepository();
        return new IvScrapService(
            factory,
            tenant,
            access,
            new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreateInventoryPosting(factory, company, branch, access),
            NullLogger<IvScrapService>.Instance);
    }

    public static IvStockTransferService CreateStockTransfer(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var access = Allow();
        var postingRepo = new IvStockPostingRepository();
        return new IvStockTransferService(
            factory,
            tenant,
            access,
            new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreateInventoryPosting(factory, company, branch, access),
            NullLogger<IvStockTransferService>.Instance);
    }

    public static IvVendorReturnService CreateVendorReturn(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var access = Allow();
        var postingRepo = new IvStockPostingRepository();
        return new IvVendorReturnService(
            factory,
            tenant,
            access,
            new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreateInventoryPosting(factory, company, branch, access),
            NullLogger<IvVendorReturnService>.Instance);
    }

    public static IvStockAdjustmentService CreateStockAdjustment(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        string company = CompanyCode,
        string branch = BranchCode,
        IAccessRightService? access = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        access ??= Allow();
        var postingRepo = new IvStockPostingRepository();
        return new IvStockAdjustmentService(
            factory,
            tenant,
            access,
            new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreateInventoryPosting(factory, company, branch, access),
            NullLogger<IvStockAdjustmentService>.Instance);
    }

    public static IvStockReturnService CreateStockReturn(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var access = Allow();
        var postingRepo = new IvStockPostingRepository();
        return new IvStockReturnService(
            factory,
            tenant,
            access,
            new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreateInventoryPosting(factory, company, branch, access),
            NullLogger<IvStockReturnService>.Instance);
    }

    public static IvStockCountService CreateStockCount(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        string company = CompanyCode,
        string branch = BranchCode)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
        var access = Allow();
        var postingRepo = new IvStockPostingRepository();
        return new IvStockCountService(
            factory,
            tenant,
            access,
            new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreateInventoryPosting(factory, company, branch, access),
            NullLogger<IvStockCountService>.Instance);
    }

    public static IAccessRightService Allow()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access.Object;
    }

    private static async Task<long> ReserveSequenceAsync(AppDbContext db, string company, string branch)
    {
        var counter = await db.StockPostingBranchSequences
            .SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch);
        if (counter is null)
        {
            counter = new StockPostingBranchSequence
            {
                CompanyCode = company,
                BranchCode = branch,
                LastSequence = 0,
                UpdatedAtUtc = DateTime.UtcNow,
                RowVersion = [1]
            };
            db.StockPostingBranchSequences.Add(counter);
        }

        counter.LastSequence++;
        counter.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return counter.LastSequence;
    }

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

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

internal readonly record struct FifoLayerSeed(decimal Qty, decimal UnitCost, DateTime EffectiveAt);
