using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryScrap)]
public sealed class IvScrapLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal BalanceReferencePrice = 11m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvScrapLedgerAuthorityTests()
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
    public async Task Moving_average_scrap_ignores_request_price()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var scrap = InventoryLedgerTestFixture.CreateScrap(_factory, FixedToday);
        var save = await scrap.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(BalanceReferencePrice, (await db.IvTrxBatchDetails.SingleAsync()).UnitPrice);

        var post = await scrap.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync();
        Assert.Equal("SCRAP_OUT", fact.MovementCode);
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
    }

    [Fact]
    public async Task Fifo_scrap_consumes_layers_and_ignores_request_price()
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

        var scrap = InventoryLedgerTestFixture.CreateScrap(_factory, FixedToday);
        var save = await scrap.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await scrap.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts
            .Where(x => x.MovementCode == "SCRAP_OUT")
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
    public async Task Standard_scrap_uses_effective_standard_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var scrap = InventoryLedgerTestFixture.CreateScrap(_factory, FixedToday);
        var save = await scrap.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await scrap.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "SCRAP_OUT");
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

        var scrap = InventoryLedgerTestFixture.CreateScrap(_factory, FixedToday);
        var save = await scrap.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await scrap.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await scrap.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
        Assert.Equal(StockValuationSources.OriginalReversal, facts[1].ValuationSource);
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

    private static IvScrapSaveRequest Request(int fromBalLocId, decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvScrapLineRequest
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
                    UnitPrice = TamperPrice,
                    Reason = IvScrapReasons.Damaged
                }
            ]
        };
}
