using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionStockAdjustmentServiceTests : IAsyncDisposable
{
    private static readonly DateTime EffectiveAt = new(2026, 10, 2, 8, 0, 0);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IStockPostingCoordinator _coordinator;

    public ProductionStockAdjustmentServiceTests()
    {
        _connection.Open();
        _factory = ProductionLedgerTestFixture.CreateSqliteFactory(_connection);
        _coordinator = ProductionLedgerTestFixture.CreateCoordinator(_factory);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        ProductionLedgerTestFixture.SeedActiveEpochAsync(db, effectiveFrom: new DateTime(2026, 10, 1))
            .GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Approved_manual_adjust_in_posts_replays_and_rolls_back_without_inventory_effects()
    {
        var (workOrderId, poolId) = await SeedWorkOrderAndPoolAsync();
        var service = CreateService();
        var saved = await service.SaveAsync(new ProductionStockAdjustmentSaveRequest
        {
            WorkOrderId = workOrderId,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            EffectiveDate = EffectiveAt,
            Lines = [new ProductionStockAdjustmentLineInput
            {
                ProductionBalLotId = poolId,
                AdjustmentQty = 3m,
                Reason = ProductionStockAdjustmentReasons.Count,
                Remarks = "Approved manual WIP receipt",
                CostEvidenceType = InventoryCostEvidenceTypes.ManualApproved,
                ProposedUnitCost = 0.333334m,
                CostOverrideReason = "Approved opening valuation",
            }],
        });

        Assert.True(saved.Succeeded, saved.Message);
        Assert.NotNull(saved.Data);
        await SetSqliteRowVersionAsync(saved.Data.Id);
        var draft = await service.GetAsync(saved.Data.Id);
        Assert.True(draft.Succeeded, draft.Message);

        var postRequest = new ProductionStockAdjustmentCommand(saved.Data.Id, draft.Data!.RowVersion, Guid.NewGuid());
        var posted = await service.PostAsync(postRequest);
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal("POSTED", posted.Data!.Status);

        var replay = await service.PostAsync(postRequest);
        Assert.True(replay.Succeeded, replay.Message);
        Assert.Equal(posted.Data.PostingId, replay.Data!.PostingId);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == poolId);
            Assert.Equal(3m, pool.BaseQty);
            Assert.Equal(1.000002m, pool.TotalCost);
            var movement = await db.ProductionBalLotMovements.SingleAsync(x => x.StockPostingId == posted.Data.PostingId);
            Assert.Equal(ProductionBalLotMovementTypes.AdjustIn, movement.MovementType);
            Assert.Equal(3m, movement.BaseQty);
            Assert.Equal(1.000002m, movement.TotalCost);
            Assert.Equal(ProductionPoolValuationService.Verified, movement.ValuationStatus);
            var fact = await db.ProductionStockAdjustmentFacts.SingleAsync(x => x.StockPostingId == posted.Data.PostingId);
            Assert.Equal(InventoryCostEvidenceTypes.ManualApproved, fact.CostEvidenceType);
            Assert.Equal("Approved opening valuation", fact.CostOverrideReason);
            var evidence = await db.ProductionValuationEvidenceRows.SingleAsync(x => x.MovementId == movement.Uid);
            Assert.Equal(InventoryCostEvidenceTypes.ManualApproved, evidence.Basis);
            Assert.Empty(await db.IvTrxHistories.Where(x => x.StockPostingId == posted.Data.PostingId).ToListAsync());
            Assert.Empty(await db.StockValuationFacts.Where(x => x.StockPostingId == posted.Data.PostingId).ToListAsync());
            Assert.Empty(await db.IvBalLocs.ToListAsync());
        }

        var rollback = await service.RollbackAsync(new ProductionStockAdjustmentCommand(
            saved.Data.Id, posted.Data.RowVersion, Guid.NewGuid(), "Reverse the full depletion"));
        Assert.True(rollback.Succeeded, rollback.Message);
        Assert.Equal("REVERSED", rollback.Data!.Status);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == poolId);
            Assert.Equal(0m, pool.BaseQty);
            Assert.Equal(0m, pool.TotalCost);
            Assert.Equal(0m, pool.AverageUnitCost);
            var facts = await db.ProductionStockAdjustmentFacts.OrderBy(x => x.StockPostingId).ToListAsync();
            Assert.Equal(2, facts.Count);
            Assert.NotNull(facts[1].ReversesFactId);
            Assert.Equal(facts[0].TotalValue, facts[1].TotalValue);
            Assert.Empty(await db.IvTrxHistories.ToListAsync());
            Assert.Empty(await db.StockValuationFacts.ToListAsync());
            Assert.Empty(await db.IvBalLocs.ToListAsync());
        }
    }

    [Fact]
    public async Task Failure_after_production_writer_rolls_back_the_complete_adjustment_transaction()
    {
        var (workOrderId, poolId) = await SeedWorkOrderAndPoolAsync();
        var service = CreateService(new ThrowAfterApplyWriter(new ProductionStockWriter(new StockMovementRegistry())));
        var saved = await service.SaveAsync(new ProductionStockAdjustmentSaveRequest
        {
            WorkOrderId = workOrderId,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            EffectiveDate = EffectiveAt,
            Lines = [new ProductionStockAdjustmentLineInput
            {
                ProductionBalLotId = poolId,
                AdjustmentQty = 3m,
                Reason = ProductionStockAdjustmentReasons.Count,
                CostEvidenceType = InventoryCostEvidenceTypes.ManualApproved,
                ProposedUnitCost = 0.333334m,
                CostOverrideReason = "Atomicity injection",
            }],
        });
        Assert.True(saved.Succeeded, saved.Message);
        await SetSqliteRowVersionAsync(saved.Data!.Id);
        var draft = await service.GetAsync(saved.Data.Id);
        Assert.True(draft.Succeeded, draft.Message);
        var request = new ProductionStockAdjustmentCommand(saved.Data.Id, draft.Data!.RowVersion, Guid.NewGuid());

        var failed = await service.PostAsync(request);

        Assert.False(failed.Succeeded);
        Assert.Contains("Injected PADJ failure", failed.Message, StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync();
        var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == poolId);
        Assert.Equal(0m, pool.BaseQty);
        Assert.Equal(0m, pool.TotalCost);
        Assert.Empty(await db.ProductionBalLotMovements.ToListAsync());
        Assert.Empty(await db.ProductionStockAdjustmentFacts.ToListAsync());
        Assert.Empty(await db.ProductionPostingLinks.Where(x => x.WorkOrderId == workOrderId).ToListAsync());
        Assert.Empty(await db.StockPostings.Where(x => x.RequestId == request.RequestId).ToListAsync());
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
        Assert.Empty(await db.StockValuationFacts.ToListAsync());
    }

    private async Task<(long WorkOrderId, long PoolId)> SeedWorkOrderAndPoolAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var workOrder = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = $"WO-PADJ-{Guid.NewGuid():N}",
            ProductCode = "FG-PADJ",
            OutputUom = "EA",
            Status = ProductionWorkOrderStatuses.Released,
            PlannedQty = 10m,
            RemainingQty = 10m,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
            IsLegacySnapshot = false,
            DefinitionEffectiveDate = EffectiveAt.Date,
            PlannedStartDateTime = EffectiveAt.Date,
            PlannedCompletionDateTime = EffectiveAt.Date.AddDays(1),
            RowVersion = [1],
        };
        db.ProductionWorkOrders.Add(workOrder);
        await db.SaveChangesAsync();

        var pool = new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.Wip,
            ItemCode = "WIP-PADJ",
            Description = "PADJ service test pool",
            Qty = 0m,
            Uom = "EA",
            BaseQty = 0m,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            TotalCost = 0m,
            AverageUnitCost = 0m,
            WorkOrderId = workOrder.Uid,
            WorkOrderNo = workOrder.WorkOrderNo,
            WarehouseCode = "MAIN",
            LocationCode = "SITE",
            LotNo = "PADJ-LOT",
            LastMovementDate = EffectiveAt.AddDays(-1),
        };
        db.ProductionBalLots.Add(pool);
        await db.SaveChangesAsync();
        await ProductionLedgerTestFixture.SeedVerifiedPoolAsync(db, pool);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return (workOrder.Uid, pool.Uid);
    }

    private ProductionStockAdjustmentService CreateService(IProductionStockWriter? productionWriter = null) => new(
        _factory,
        InventoryTenantTestHelper.CreateTenantContext(),
        ProductionLedgerTestFixture.Allow(MenuCodes.PlanningStockAdjustment),
        new FixedCurrentDateService(EffectiveAt.AddDays(1)),
        new TestRunningNumberService(),
        new BranchStockTransactionLock(),
        _coordinator,
        productionWriter ?? new ProductionStockWriter(new StockMovementRegistry()),
        new ItemStandardCostResolver(),
        Options.Create(new ProductionStockTransactionOptions { AdjustmentPostingEnabled = true }));

    private async Task SetSqliteRowVersionAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PrProductionStockAdjustment SET RowVersion = X'01' WHERE BatchId = {id};");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class TestRunningNumberService : IRunningNumberService
    {
        public Task<int> PeekNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) => Task.FromResult(100);
        public Task<int> GetNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) => Task.FromResult(100);
    }

    private sealed class ThrowAfterApplyWriter(IProductionStockWriter inner) : IProductionStockWriter
    {
        public async Task<IReadOnlyList<ProductionBalLotMovement>> ApplyAsync(
            StockPostingContext context,
            IReadOnlyCollection<ProductionStockLeg> legs,
            CancellationToken cancellationToken = default)
        {
            await inner.ApplyAsync(context, legs, cancellationToken);
            throw new InvalidOperationException("Injected PADJ failure after Production writer.");
        }
    }
}
