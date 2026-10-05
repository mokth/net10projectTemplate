using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPosting)]
public sealed class InventoryValuationServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IDbContextFactory<AppDbContext> _factory = null!;
    private int _batchNo;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new LocalFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "ITEM-1", IDesc = "Valued item",
            IClassCode = "RAW", StdUom = "EA", StockControl = true, IsActive = true
        });
        db.StockLedgerEpochs.Add(new StockLedgerEpoch
        {
            CompanyCode = "DEMO", BranchCode = "HQ", EffectiveFrom = new DateTime(2026, 10, 1),
            Version = 2, Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(), ReconciliationManifestHash = new string('A', 64)
        });
        await db.SaveChangesAsync();
        db.IvBalLocs.AddRange(
            Balance("MAIN", "A"),
            Balance("MAIN", "B"));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Moving_average_issues_and_final_depletion_use_authoritative_state()
    {
        await PostReceiptAsync(10m, 2m, new DateTime(2026, 10, 1, 8, 0, 0));
        await PostReceiptAsync(10m, 4m, new DateTime(2026, 10, 2, 8, 0, 0));
        await PostIssueAsync(5m, new DateTime(2026, 10, 3, 8, 0, 0));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync();
            Assert.Equal(15m, state.OnHandBaseQty);
            Assert.Equal(45m, state.InventoryValue);
            Assert.Equal(3m, state.AverageUnitCost);
            var issue = await db.StockValuationFacts.SingleAsync(x => x.Direction == -1);
            Assert.Equal(15m, issue.CostAmount);
            Assert.Equal(StockValuationSources.MovingAverage, issue.ValuationSource);
        }

        await PostIssueAsync(15m, new DateTime(2026, 10, 4, 8, 0, 0));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync();
            Assert.Equal(0m, state.OnHandBaseQty);
            Assert.Equal(0m, state.InventoryValue);
            Assert.Equal(0m, state.AverageUnitCost);
            var lastIssue = await db.StockValuationFacts.Where(x => x.Direction == -1)
                .OrderByDescending(x => x.Id).FirstAsync();
            Assert.Equal(45m, lastIssue.CostAmount);
        }
    }

    [Fact]
    public async Task Transfer_creates_equal_out_and_in_facts_without_changing_pool_value()
    {
        await PostReceiptAsync(10m, 7.25m, new DateTime(2026, 10, 1, 8, 0, 0));
        var balances = await BalanceIdsAsync();
        await ExecuteAsync(new DateTime(2026, 10, 2, 8, 0, 0), "TR", (context, writer) =>
        {
            var history = History(IvTrxTypes.StockTransfer);
            history.FromBalLocId = balances[0];
            history.ToBalLocId = balances[1];
            history.FrWarehouse = "MAIN";
            history.FrLocation = "A";
            history.ToWarehouse = "MAIN";
            history.ToLocation = "B";
            history.FrStdQty = 4m;
            history.ToStdQty = 4m;
            history.FrStdUom = "EA";
            history.ToStdUom = "EA";
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });

        await using var db = await _factory.CreateDbContextAsync();
        var transferPosting = await db.StockPostings.SingleAsync(x => x.SourceDocumentType == "TR");
        var facts = await db.StockValuationFacts.Where(x => x.StockPostingId == transferPosting.Id)
            .OrderBy(x => x.SplitOrdinal).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(-1, facts[0].Direction);
        Assert.Equal(1, facts[1].Direction);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        var state = await db.StockCostStates.SingleAsync();
        Assert.Equal(10m, state.OnHandBaseQty);
        Assert.Equal(72.5m, state.InventoryValue);
    }

    [Fact]
    public async Task Reversal_copies_original_value_and_links_the_original_fact()
    {
        var postingId = await PostReceiptAsync(8m, 2.5m, new DateTime(2026, 10, 1, 8, 0, 0));
        int historyId;
        await using (var db = await _factory.CreateDbContextAsync())
            historyId = await db.IvTrxHistories.Select(x => x.Id).SingleAsync();

        await ExecuteAsync(
            new DateTime(2026, 10, 2, 8, 0, 0), "MR_REVERSAL",
            async (context, writer) =>
            {
                var original = await context.Db.IvTrxHistories.SingleAsync(x => x.Id == historyId);
                writer.AppendReversal(context, [original], 1);
            },
            postingId);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
        Assert.Equal(StockValuationSources.OriginalReversal, facts[1].ValuationSource);
        Assert.Equal(StockValuationStatuses.Reversed, facts[1].ValuationStatus);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(0m, state.OnHandBaseQty);
        Assert.Equal(0m, state.InventoryValue);
    }

    [Fact]
    public async Task History_saved_by_route_before_completion_is_still_valued()
    {
        var balanceId = (await BalanceIdsAsync())[0];
        await ExecuteAsync(new DateTime(2026, 10, 1, 8, 0, 0), "MR_SAVED", async (context, writer) =>
        {
            var history = History(IvTrxTypes.MiscellaneousReceipt);
            history.ToBalLocId = balanceId;
            history.ToWarehouse = "MAIN";
            history.ToLocation = "A";
            history.ToStdQty = 6m;
            history.ToStdUom = "EA";
            history.UnitPrice = 4.5m;
            history.PriceEvidence = "TEST_APPROVED";
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);

            // Production and finished-good routes persist their history rows before the
            // coordinator completes the posting. Valuation must discover those rows too.
            await context.Db.SaveChangesAsync();
        });

        await using var db = await _factory.CreateDbContextAsync();
        var historyId = await db.IvTrxHistories.Select(x => x.Id).SingleAsync();
        var fact = await db.StockValuationFacts.SingleAsync();
        Assert.Equal(historyId, fact.InventoryHistoryId);
        Assert.Equal(27m, fact.CostAmount);
        Assert.Equal(6m, (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
    }

    private async Task<long> PostReceiptAsync(decimal qty, decimal price, DateTime effectiveAt)
    {
        var balanceId = (await BalanceIdsAsync())[0];
        return await ExecuteAsync(effectiveAt, "MR", (context, writer) =>
        {
            var history = History(IvTrxTypes.MiscellaneousReceipt);
            history.ToBalLocId = balanceId;
            history.ToWarehouse = "MAIN";
            history.ToLocation = "A";
            history.ToStdQty = qty;
            history.ToStdUom = "EA";
            history.UnitPrice = price;
            history.PriceEvidence = "TEST_APPROVED";
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });
    }

    private async Task<long> PostIssueAsync(decimal qty, DateTime effectiveAt)
    {
        var balanceId = (await BalanceIdsAsync())[0];
        return await ExecuteAsync(effectiveAt, "MI", (context, writer) =>
        {
            var history = History(IvTrxTypes.MiscellaneousIssue);
            history.FromBalLocId = balanceId;
            history.FrWarehouse = "MAIN";
            history.FrLocation = "A";
            history.FrStdQty = qty;
            history.FrStdUom = "EA";
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });
    }

    private async Task<long> ExecuteAsync(
        DateTime effectiveAt,
        string documentType,
        Func<StockPostingContext, IvInventoryHistoryWriter, Task> handler,
        long? reversesPostingId = null)
    {
        var command = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(),
            CommandType = documentType + "_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = documentType,
            SourceDocumentId = (++_batchNo).ToString(),
            SourceDocumentNo = _batchNo.ToString(),
            DocumentRevision = 1,
            PostingRole = reversesPostingId is null ? "PRIMARY" : "REVERSAL",
            EffectiveAt = effectiveAt,
            Evidence = StockPostingFingerprint.Create(new { documentType, _batchNo }, new { effectiveAt }),
            ReversesPostingId = reversesPostingId
        };
        var coordinator = new StockPostingCoordinator(
            _factory, new Tenant(), new BranchStockTransactionLock(),
            new StockPeriodGuard(), new NoActiveStockFreezeGuard(),
            new InventoryValuationService());
        var result = await coordinator.ExecuteAsync(command, async (context, _) =>
        {
            await handler(context, new IvInventoryHistoryWriter());
            return 0;
        });
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.StockPostingId!.Value;
    }

    private async Task<int[]> BalanceIdsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.IvBalLocs.OrderBy(x => x.LocCode).Select(x => x.Id).ToArrayAsync();
    }

    private IvTrxHistory History(string type) => new()
    {
        CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = ++_batchNo,
        TrxLineNo = 1, TrxDtTime = new DateTime(2026, 10, 1), TrxType = type,
        BatchStatus = IvBatchStatuses.Posted, ICode = "ITEM-1", IStatus = "ACTIVE",
        CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
    };

    private static IvBalLoc Balance(string warehouse, string location) => new()
    {
        CompanyCode = "DEMO", BranchCode = "HQ", ICode = "ITEM-1",
        WhCode = warehouse, LocCode = location, LotNo = string.Empty, IStatus = "ACTIVE",
        StdQty = 0m, StdUom = "EA"
    };

    private sealed class Tenant : IInventoryTenantContext
    {
        private static readonly InventoryTenantScope Scope = new()
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LocationCode = "SITE", UserId = "tester"
        };
        public InventoryTenantScope? TryCompanyScope() => Scope;
        public InventoryTenantScope? TryBranchScope() => Scope;
        public InventoryTenantScope? TryWriteScope() => Scope;
    }

    private sealed class LocalFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
