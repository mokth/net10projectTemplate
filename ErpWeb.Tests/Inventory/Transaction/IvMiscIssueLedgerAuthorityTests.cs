using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMiscIssue)]
public sealed class IvMiscIssueLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal BalanceReferencePrice = 11m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvMiscIssueLedgerAuthorityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = InventoryLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedCompanyAsync(db);
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            UomCode = "EA",
            IsActive = true
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IClassCode = "RAW",
            IsActive = true
        });
        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IStatus = "ACTIVE",
            IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = PurchasePrice
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Moving_average_issue_ignores_request_price_and_writes_valuation_fact()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(BalanceReferencePrice, detail.UnitPrice);
        }

        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync();
        Assert.Equal(-1, fact.Direction);
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(90m, state.OnHandBaseQty);
        Assert.Equal(180m, state.InventoryValue);
    }

    [Fact]
    public async Task Fifo_issue_consumes_layers_and_ignores_request_price()
    {
        var balId = await SeedBalanceAsync(15m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
        }

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts
            .Where(x => x.MovementCode == "ISSUE_OUT")
            .OrderBy(x => x.SplitOrdinal)
            .ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(27m, facts.Sum(x => x.CostAmount));
        Assert.All(facts, fact =>
        {
            Assert.NotEqual(TamperPrice, fact.UnitCost);
            Assert.NotEqual(PurchasePrice, fact.UnitCost);
            Assert.Equal(StockValuationSources.Fifo, fact.ValuationSource);
        });
    }

    [Fact]
    public async Task Standard_issue_uses_effective_standard_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await mi.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.Direction == -1);
        Assert.Equal(6.5m, fact.UnitCost);
        Assert.Equal(65m, fact.CostAmount);
        Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
    }

    [Fact]
    public async Task Rollback_creates_exact_reversal()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var mi = InventoryLedgerTestFixture.CreateMiscIssue(_factory, FixedToday);
        var save = await mi.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await mi.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await mi.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        Assert.Equal(facts[0].UnitCost, facts[1].UnitCost);
        Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
        Assert.Equal(StockValuationSources.OriginalReversal, facts[1].ValuationSource);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(200m, state.InventoryValue);
    }

    private async Task<int> SeedBalanceAsync(decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            UnitPrice = BalanceReferencePrice,
            TransDate = FixedToday
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }

    private static IvMiscIssueSaveRequest Request(int fromBalLocId, decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvMiscIssueLineRequest
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
                    UnitPrice = TamperPrice
                }
            ]
        };
}
