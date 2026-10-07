using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryTransfer)]
public sealed class IvStockTransferLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal BalanceReferencePrice = 11m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockTransferLedgerAuthorityTests()
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
        foreach (var loc in new[] { "BIN1", "BIN2" })
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                WarehouseCode = "MAIN",
                LocCode = loc,
                IsActive = true
            });
        }

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
    public async Task Moving_average_transfer_is_value_neutral_and_ignores_request_price()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(BalanceReferencePrice, (await db.IvTrxBatchDetails.SingleAsync()).UnitPrice);

        var post = await transfer.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Direction).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(20m, facts.Single(x => x.MovementCode == "TRANSFER_OUT").CostAmount);
        Assert.Equal(20m, facts.Single(x => x.MovementCode == "TRANSFER_IN").CostAmount);
        Assert.All(facts, fact =>
        {
            Assert.NotEqual(TamperPrice, fact.UnitCost);
            Assert.NotEqual(PurchasePrice, fact.UnitCost);
        });
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(200m, state.InventoryValue);
    }

    [Fact]
    public async Task Fifo_transfer_moves_exact_layer_value()
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

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await transfer.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var outbound = await verify.StockValuationFacts.Where(x => x.MovementCode == "TRANSFER_OUT").ToListAsync();
        var inbound = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "TRANSFER_IN");
        Assert.Equal(27m, outbound.Sum(x => x.CostAmount));
        Assert.Equal(27m, inbound.CostAmount);
        Assert.NotEqual(TamperPrice, inbound.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(15m, state.OnHandBaseQty);
        Assert.Equal(55m, state.InventoryValue);
    }

    [Fact]
    public async Task Standard_transfer_uses_effective_standard_cost_on_both_legs()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await transfer.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var outbound = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "TRANSFER_OUT");
        var inbound = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "TRANSFER_IN");
        Assert.Equal(65m, outbound.CostAmount);
        Assert.Equal(65m, inbound.CostAmount);
        Assert.Equal(StockValuationSources.Standard, outbound.ValuationSource);
        Assert.Equal(StockValuationSources.Standard, inbound.ValuationSource);
        Assert.NotEqual(TamperPrice, outbound.UnitCost);
        Assert.NotEqual(PurchasePrice, outbound.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(650m, state.InventoryValue);
    }

    [Fact]
    public async Task Rollback_reverses_both_transfer_facts()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var transfer = InventoryLedgerTestFixture.CreateStockTransfer(_factory, FixedToday);
        var save = await transfer.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await transfer.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await transfer.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var facts = await verify.StockValuationFacts.ToListAsync();
        var originals = facts.Where(x => x.ReversesValuationFactId == null).ToList();
        var reversals = facts.Where(x => x.ReversesValuationFactId != null).ToList();
        Assert.Equal(2, originals.Count);
        Assert.Equal(2, reversals.Count);
        Assert.All(reversals, reversal =>
        {
            var original = originals.Single(x => x.Id == reversal.ReversesValuationFactId);
            Assert.Equal(original.CostAmount, reversal.CostAmount);
            Assert.Equal(StockValuationSources.OriginalReversal, reversal.ValuationSource);
        });
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

    private static IvStockTransferSaveRequest Request(int fromBalLocId, decimal qty) =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvStockTransferLineRequest
                {
                    FromBalLocId = fromBalLocId,
                    ICode = "A100",
                    FrWarehouse = "MAIN",
                    FrLocation = "BIN1",
                    FrLotNo = string.Empty,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN2",
                    Quantity = qty,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = TamperPrice
                }
            ]
        };
}
