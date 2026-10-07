using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockCount)]
public sealed class IvStockCountLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal PurchasePrice = 777m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockCountLedgerAuthorityTests()
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
        await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
        await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
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
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = 100m,
            StdUom = "EA",
            UnitPrice = null,
            TransDate = FixedToday
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Positive_count_variance_uses_current_cost_not_purchase_price()
    {
        var service = InventoryLedgerTestFixture.CreateStockCount(_factory, FixedToday);
        var save = await service.SaveAsync(new IvStockCountSaveRequest
        {
            CountDate = FixedToday,
            WHCode = "MAIN",
            IncludeZeroQty = true
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.GenerateAsync(save.Id, discardCounts: false)).Succeeded);

        var sheet = await service.GetAsync(save.CountNo!);
        Assert.True(sheet.Succeeded, sheet.ErrorMessage);
        var line = Assert.Single(sheet.Document!.Lines);
        Assert.Equal(PurchasePrice, line.SnapshotUnitPrice);

        var counted = await service.SaveCountsAsync(
            sheet.Document.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 110m }],
            null);
        Assert.True(counted.Succeeded, counted.ErrorMessage);
        var post = await service.PostAsync(sheet.Document.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(110m, state.OnHandBaseQty);
        Assert.Equal(220m, state.InventoryValue);
    }
}
