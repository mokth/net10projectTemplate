using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionCostReadinessTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionCostReadinessTests()
    {
        _connection.Open();
        _factory = ProductionLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(null, "EVIDENCE", false)]
    [InlineData(-1.0, "EVIDENCE", false)]
    [InlineData(0.0, null, false)]
    [InlineData(0.0, "", false)]
    [InlineData(0.0, "   ", false)]
    [InlineData(0.0, "ZERO", true)]
    [InlineData(1.5, "   ", false)]
    [InlineData(1.5, "TEST_EXPLICIT_COMPANY_BASE_PRICE", true)]
    public void Inventory_cost_predicate_matches_non_negative_price_and_nonblank_evidence(
        double? price, string? evidence, bool expected)
    {
        decimal? unitPrice = price is null ? null : (decimal)price.Value;
        Assert.Equal(expected, ProductionCostReadiness.HasVerifiedInventoryCost(unitPrice, evidence));
    }

    [Fact]
    public void Inventory_balance_error_identifies_locked_stock_identity()
    {
        var error = ProductionCostReadiness.InventoryBalanceError(new IvBalLocLockResult
        {
            Id = 123,
            ICode = "RM001",
            WhCode = "WH01",
            LocCode = "BIN-A",
            LotNo = "LOT01",
            UnitPrice = null,
            PriceEvidence = "X",
        });
        Assert.Contains("RM001", error);
        Assert.Contains("WH01/BIN-A", error);
        Assert.Contains("LOT01", error);
        Assert.Contains("IvBalLoc 123", error);
        Assert.Null(ProductionCostReadiness.InventoryBalanceError(new IvBalLocLockResult
        {
            Id = 1,
            ICode = "RM001",
            WhCode = "WH01",
            LocCode = "BIN-A",
            LotNo = "LOT01",
            UnitPrice = 0m,
            PriceEvidence = "ZERO",
        }));
    }

    [Fact]
    public async Task Missing_or_unvalued_or_mismatched_pools_are_invalid()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var missing = Lot("RM001", "IP-1");
        db.ProductionBalLots.Add(missing);
        await db.SaveChangesAsync();
        Assert.Contains("UNVALUED", await ProductionCostReadiness.PoolValuationError(db, missing, CancellationToken.None));

        var unverified = Lot("RM001", "IP-2");
        db.ProductionBalLots.Add(unverified);
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.Add(new ProductionPoolValuation
        {
            ProductionBalLotId = unverified.Uid,
            Status = ProductionPoolValuationService.Unvalued,
            TrackedBaseQty = 1,
            TrackedValue = 1,
        });
        await db.SaveChangesAsync();
        Assert.Contains("UNVALUED", await ProductionCostReadiness.PoolValuationError(db, unverified, CancellationToken.None));

        var qtyMismatch = Lot("RM001", "IP-3", baseQty: 2, cost: 2);
        db.ProductionBalLots.Add(qtyMismatch);
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.Add(Projection(qtyMismatch, trackedQty: 1, trackedValue: 2));
        await db.SaveChangesAsync();
        Assert.Contains("reconciliation", await ProductionCostReadiness.PoolValuationError(db, qtyMismatch, CancellationToken.None));

        var valueMismatch = Lot("RM001", "IP-4", baseQty: 2, cost: 2);
        db.ProductionBalLots.Add(valueMismatch);
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.Add(Projection(valueMismatch, trackedQty: 2, trackedValue: 9));
        await db.SaveChangesAsync();
        Assert.Contains("reconciliation", await ProductionCostReadiness.PoolValuationError(db, valueMismatch, CancellationToken.None));

        var stranded = Lot("RM001", "IP-5", baseQty: 0, cost: 4);
        db.ProductionBalLots.Add(stranded);
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.Add(Projection(stranded, trackedQty: 0, trackedValue: 4));
        await db.SaveChangesAsync();
        Assert.Contains("reconciliation", await ProductionCostReadiness.PoolValuationError(db, stranded, CancellationToken.None));

        var negative = Lot("RM001", "IP-6", baseQty: 1, cost: 1);
        negative.Qty = -1;
        Assert.Contains("negative", await ProductionCostReadiness.PoolValuationError(db, negative, CancellationToken.None));
    }

    [Fact]
    public async Task Generic_pool_readiness_does_not_require_production_location()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var material = Lot("RM001", "IP-1001", kind: ProductionBalLotKinds.MaterialIn);
        var handoff = Lot("WIP-OUT", "HO-1", kind: ProductionBalLotKinds.Wip);
        db.ProductionBalLots.AddRange(material, handoff);
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.AddRange(Projection(material), Projection(handoff));
        await db.SaveChangesAsync();

        Assert.Null(await ProductionCostReadiness.PoolValuationError(db, material, CancellationToken.None));
        Assert.Null(await ProductionCostReadiness.PoolValuationError(db, handoff, CancellationToken.None));
        Assert.Contains("production location",
            await ProductionCostReadiness.FinishedGoodSourceError(db, material, CancellationToken.None));
        Assert.Contains("production location",
            await ProductionCostReadiness.FinishedGoodSourceError(db, handoff, CancellationToken.None));
    }

    [Fact]
    public async Task Fg_readiness_requires_verified_pool_and_location()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var lot = Lot("FG", "PROD-1", kind: ProductionBalLotKinds.Wip);
        lot.ProductionLocationId = 9;
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync();
        db.ProductionPoolValuationRows.Add(Projection(lot));
        await db.SaveChangesAsync();
        Assert.Null(await ProductionCostReadiness.FinishedGoodSourceError(db, lot, CancellationToken.None));
    }

    private static ProductionBalLot Lot(
        string item, string lotNo, decimal baseQty = 1, decimal cost = 1,
        string kind = ProductionBalLotKinds.MaterialIn) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        Kind = kind,
        ItemCode = item,
        Qty = baseQty,
        Uom = "EA",
        BaseQty = baseQty,
        BaseUom = "EA",
        ConversionFactorToBase = 1m,
        TotalCost = cost,
        AverageUnitCost = baseQty == 0m ? 0m : cost / baseQty,
        WorkOrderId = 1,
        WorkOrderNo = "WO-1",
        LotNo = lotNo,
        RowVersion = [1],
    };

    private static ProductionPoolValuation Projection(
        ProductionBalLot lot, decimal? trackedQty = null, decimal? trackedValue = null) => new()
    {
        ProductionBalLotId = lot.Uid,
        Status = ProductionPoolValuationService.Verified,
        TrackedBaseQty = trackedQty ?? lot.BaseQty,
        TrackedValue = trackedValue ?? lot.TotalCost,
    };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
