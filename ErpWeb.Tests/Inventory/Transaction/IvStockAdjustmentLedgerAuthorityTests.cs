using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryAdjustment)]
public sealed class IvStockAdjustmentLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal BalanceReferencePrice = 11m;
    private const decimal ApprovedCost = 4.25m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockAdjustmentLedgerAuthorityTests()
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
        db.MsUoms.Add(new MsUom { CompanyCode = InventoryLedgerTestFixture.CompanyCode, UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = InventoryLedgerTestFixture.CompanyCode, IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = InventoryLedgerTestFixture.CompanyCode, IStatus = "ACTIVE", IsActive = true });
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
    public async Task Negative_moving_average_ignores_request_price()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, -10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(BalanceReferencePrice, detail.UnitPrice);
            Assert.Null(detail.CostEvidenceType);
        }

        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);
        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_OUT");
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
    }

    [Fact]
    public async Task Negative_fifo_consumes_layers()
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

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, -8m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts.Where(x => x.MovementCode == "ADJUST_OUT").ToListAsync();
        Assert.Equal(27m, facts.Sum(x => x.CostAmount));
        Assert.All(facts, fact => Assert.NotEqual(TamperPrice, fact.UnitCost));
    }

    [Fact]
    public async Task Negative_standard_uses_effective_standard_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, -10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_OUT");
        Assert.Equal(6.5m, fact.UnitCost);
        Assert.Equal(65m, fact.CostAmount);
        Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
    }

    [Fact]
    public async Task Positive_moving_average_without_evidence_uses_current_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(110m, state.OnHandBaseQty);
        Assert.Equal(220m, state.InventoryValue);
    }

    [Fact]
    public async Task Positive_moving_average_without_pool_is_blocked()
    {
        var balId = await SeedBalanceAsync(10m);
        await using (var db = await _factory.CreateDbContextAsync())
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 5m, unitPrice: 0m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("no approved current inventory cost for item A100", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await verify.StockValuationFacts.CountAsync(x => x.MovementCode == "ADJUST_IN"));
    }

    [Fact]
    public async Task Positive_moving_average_approved_evidence_uses_evidence_cost()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(
            balId, 10m, ApprovedCost, InventoryCostEvidenceTypes.ManualApproved, "supervisor count"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(ApprovedCost, fact.UnitCost);
        Assert.Equal(42.5m, fact.CostAmount);
        Assert.NotEqual(2m, fact.UnitCost);
    }

    [Fact]
    public async Task Positive_fifo_without_evidence_uses_current_pool_cost_and_creates_a_layer()
    {
        var balId = await SeedBalanceAsync(15m);
        decimal current;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
            current = await db.StockCostStates.Select(x => x.CurrentUnitCost).SingleAsync();
        }

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 2m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(current, fact.UnitCost);
        Assert.Equal(StockValuationSources.Fifo, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        var layers = await verify.StockFifoLayers.Where(x => x.Status == StockFifoLayerStatuses.Open).ToListAsync();
        Assert.Contains(layers, x => x.RemainingQty == 2m && x.CurrentUnitCost == current);
    }

    [Fact]
    public async Task Positive_fifo_without_pool_is_blocked()
    {
        var balId = await SeedBalanceAsync(10m);
        await using (var db = await _factory.CreateDbContextAsync())
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, 4m, unitPrice: 0m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.False(post.Succeeded);
        Assert.Contains("no approved current inventory cost for item A100", post.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Positive_fifo_approved_evidence_creates_layer_at_approved_cost()
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

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(
            balId, 2m, ApprovedCost, InventoryCostEvidenceTypes.ManualApproved, "layer correction"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(ApprovedCost, fact.UnitCost);
        Assert.Contains(
            await verify.StockFifoLayers.ToListAsync(),
            x => x.RemainingQty == 2m && x.CurrentUnitCost == ApprovedCost);
    }

    [Fact]
    public async Task Positive_standard_ignores_manual_unit_price()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(
            balId, 10m, TamperPrice, InventoryCostEvidenceTypes.ManualApproved, "must not override standard"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(6.5m, fact.UnitCost);
        Assert.Equal(65m, fact.CostAmount);
        Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
    }

    [Fact]
    public async Task Zero_cost_and_opening_evidence_keep_current_rules()
    {
        var zeroBal = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var missingReason = await service.SaveNewAsync(Request(
            zeroBal, 1m, 0m, InventoryCostEvidenceTypes.OpeningApproved, reason: null));
        Assert.False(missingReason.Succeeded);
        Assert.Contains("reason", missingReason.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var opening = await service.SaveNewAsync(Request(
            zeroBal, 1m, 3m, InventoryCostEvidenceTypes.OpeningApproved, "opening balance"));
        Assert.True(opening.Succeeded, opening.ErrorMessage);
        Assert.True((await service.PostAsync([opening.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(3m, fact.UnitCost);
    }

    [Fact]
    public async Task Unauthorized_price_override_is_blocked()
    {
        var balId = await SeedBalanceAsync(100m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryStockAdjustment,
                PermissionCodes.PriceOverride,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday, access: access.Object);
        var save = await service.SaveNewAsync(Request(
            balId, 4m, ApprovedCost, InventoryCostEvidenceTypes.ManualApproved, "not allowed"));
        Assert.False(save.Succeeded);
        Assert.Contains("not authorized to override inventory cost", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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

        var service = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(balId, -10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await service.RollbackAsync([save.BatchNo])).Succeeded);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
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

    private static IvStockAdjustmentSaveRequest Request(
        int balLocId,
        decimal adjustQty,
        decimal? unitPrice = null,
        string? evidence = null,
        string? reason = null) =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvStockAdjustmentLineRequest
                {
                    BalLocId = balLocId,
                    ICode = "A100",
                    Warehouse = "MAIN",
                    Location = "BIN1",
                    LotNo = string.Empty,
                    AdjustQty = adjustQty,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = "ACTIVE",
                    UnitPrice = unitPrice ?? TamperPrice,
                    CostEvidenceType = evidence,
                    CostOverrideReason = reason,
                    Reason = IvAdjustmentReasons.Found
                }
            ]
        };
}
