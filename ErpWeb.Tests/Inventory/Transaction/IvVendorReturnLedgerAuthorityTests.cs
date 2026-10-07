using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryVendorReturn)]
public sealed class IvVendorReturnLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal PoCommercialPrice = 9.5m;
    private const decimal BalanceReferencePrice = 11m;
    private const string PoNo = "PO-VR-1";
    private const short PoRelNo = 1;
    private const short PoLineNo = 1;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvVendorReturnLedgerAuthorityTests()
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
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            PoNo = PoNo,
            PoRelNo = PoRelNo,
            PoDate = FixedToday,
            Status = PoOrderStatuses.Received,
            VendCode = "SUP01",
            VendName = "Supplier 1",
            CurCode = InventoryLedgerTestFixture.BaseCurrency,
            OneTime = false,
            FinClosed = false
        });
        db.PoOrderDetails.Add(new PoOrderDetail
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            PoNo = PoNo,
            PoRelNo = PoRelNo,
            Line = PoLineNo,
            ICode = "A100",
            IDesc = "Stock item",
            OneTime = false,
            PoQty = 1000m,
            PoPurQty = 1000m,
            RecvQty = 1000m,
            ReturnQty = 0m,
            BalanceQty = 0m,
            OverRecvQty = 0m,
            InvoicedQty = 0m,
            PoUnitPrice = PoCommercialPrice,
            PackSz = 1m,
            StdUom = "EA",
            PurchaseUom = "EA"
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Moving_average_return_ignores_request_and_po_price()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(BalanceReferencePrice, detail.UnitPrice);
        }

        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "VENDOR_RETURN_OUT");
        Assert.Equal(-1, fact.Direction);
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        Assert.NotEqual(PoCommercialPrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(90m, state.OnHandBaseQty);
        Assert.Equal(180m, state.InventoryValue);
        var poLine = await verify.PoOrderDetails.SingleAsync();
        Assert.Equal(10m, poLine.ReturnQty);
        Assert.Equal(PoCommercialPrice, poLine.PoUnitPrice);
    }

    [Fact]
    public async Task Fifo_return_consumes_layers_and_ignores_request_price()
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

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts
            .Where(x => x.MovementCode == "VENDOR_RETURN_OUT")
            .OrderBy(x => x.SplitOrdinal)
            .ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(27m, facts.Sum(x => x.CostAmount));
        Assert.All(facts, fact =>
        {
            Assert.NotEqual(TamperPrice, fact.UnitCost);
            Assert.NotEqual(PurchasePrice, fact.UnitCost);
            Assert.NotEqual(PoCommercialPrice, fact.UnitCost);
            Assert.Equal(StockValuationSources.Fifo, fact.ValuationSource);
        });
        Assert.Equal(8m, await verify.PoOrderDetails.Select(x => x.ReturnQty).SingleAsync());
    }

    [Fact]
    public async Task Standard_return_uses_effective_standard_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "VENDOR_RETURN_OUT");
        Assert.Equal(6.5m, fact.UnitCost);
        Assert.Equal(65m, fact.CostAmount);
        Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PoCommercialPrice, fact.UnitCost);
    }

    [Fact]
    public async Task Rollback_restores_stock_and_po_return_qty()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await service.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0m, await verify.PoOrderDetails.Select(x => x.ReturnQty).SingleAsync());
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
        Assert.Equal(StockValuationSources.OriginalReversal, facts[1].ValuationSource);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(100m, state.OnHandBaseQty);
        Assert.Equal(200m, state.InventoryValue);
    }

    [Fact]
    public async Task Purchase_credit_note_owned_return_cannot_be_posted()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateVendorReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m, "PCN/PCN2609-0001"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await service.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("purchase credit note", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await verify.StockValuationFacts.CountAsync(x => x.MovementCode == "VENDOR_RETURN_OUT"));
        Assert.Equal(0m, await verify.PoOrderDetails.Select(x => x.ReturnQty).SingleAsync());
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

    private static IvVendorReturnSaveRequest Request(int fromBalLocId, decimal qty, string? refNo = null) =>
        new()
        {
            TrxDate = FixedToday,
            RefNo = refNo,
            Lines =
            [
                new IvVendorReturnLineRequest
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
                    PoNo = PoNo,
                    PoRelNo = PoRelNo,
                    PoLineNo = PoLineNo
                }
            ]
        };
}
