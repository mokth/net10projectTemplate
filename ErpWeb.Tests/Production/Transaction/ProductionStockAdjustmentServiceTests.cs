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
using Moq;

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
        var deniedPost = await CreateService(access: Access(canPost: false)).PostAsync(postRequest);
        Assert.False(deniedPost.Succeeded);
        Assert.Contains("Access denied", deniedPost.Message, StringComparison.OrdinalIgnoreCase);
        var newRollback = await service.RollbackAsync(postRequest with { Reason = "Cannot reverse a draft" });
        Assert.False(newRollback.Succeeded);
        Assert.Contains("Only POSTED PADJ documents", newRollback.Message, StringComparison.OrdinalIgnoreCase);

        var posted = await service.PostAsync(postRequest);
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal("POSTED", posted.Data!.Status);

        var replay = await service.PostAsync(postRequest);
        Assert.True(replay.Succeeded, replay.Message);
        Assert.Equal(posted.Data.PostingId, replay.Data!.PostingId);

        var postedAgain = await service.PostAsync(new(saved.Data.Id, posted.Data.RowVersion, Guid.NewGuid()));
        Assert.False(postedAgain.Succeeded);
        Assert.Contains("Only NEW PADJ documents", postedAgain.Message, StringComparison.OrdinalIgnoreCase);

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

        var rollbackRequest = new ProductionStockAdjustmentCommand(
            saved.Data.Id, posted.Data.RowVersion, Guid.NewGuid(), "Reverse the full depletion");
        var deniedRollback = await CreateService(access: Access(canRollback: false)).RollbackAsync(rollbackRequest);
        Assert.False(deniedRollback.Succeeded);
        Assert.Contains("Access denied", deniedRollback.Message, StringComparison.OrdinalIgnoreCase);

        var rollback = await service.RollbackAsync(rollbackRequest);
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
    public async Task Saved_adjustment_keeps_current_pool_base_separate_and_search_summary_uses_signed_source_uom()
    {
        var (workOrderId, poolId) = await SeedWorkOrderAndPoolAsync();
        var service = CreateService();
        var saved = await service.SaveAsync(new ProductionStockAdjustmentSaveRequest
        {
            WorkOrderId = workOrderId,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            EffectiveDate = EffectiveAt,
            RefNo = "20461012",
            Lines = [new ProductionStockAdjustmentLineInput
            {
                ProductionBalLotId = poolId,
                AdjustmentQty = 3m,
                Reason = ProductionStockAdjustmentReasons.Count,
                Remarks = "Explicit opening balance",
                CostEvidenceType = InventoryCostEvidenceTypes.ManualApproved,
                ProposedUnitCost = 1m,
                CostOverrideReason = "Approved opening valuation",
            }],
        });

        Assert.True(saved.Succeeded, saved.Message);
        var line = Assert.Single(saved.Data!.Lines);
        Assert.Equal(3m, line.BaseQty);
        Assert.Equal(0m, line.CurrentBaseQty);

        var byNumericReference = await service.SearchAsync(new() { SearchText = "20461012" });
        Assert.True(byNumericReference.Succeeded, byNumericReference.Message);
        Assert.Equal("+3.0000 EA", Assert.Single(byNumericReference.Data!.Rows).QtySummary);

        var byBatchNumber = await service.SearchAsync(new() { SearchText = saved.Data.BatchNo.ToString() });
        Assert.True(byBatchNumber.Succeeded, byBatchNumber.Message);
        Assert.Equal(saved.Data.Id, Assert.Single(byBatchNumber.Data!.Rows).Id);
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

    private ProductionStockAdjustmentService CreateService(
        IProductionStockWriter? productionWriter = null,
        IAccessRightService? access = null) => new(
        _factory,
        InventoryTenantTestHelper.CreateTenantContext(),
        access ?? ProductionLedgerTestFixture.Allow(MenuCodes.PlanningStockAdjustment),
        new FixedCurrentDateService(EffectiveAt.AddDays(1)),
        new TestRunningNumberService(),
        new BranchStockTransactionLock(),
        _coordinator,
        productionWriter ?? new ProductionStockWriter(new StockMovementRegistry()),
        new ItemStandardCostResolver());

    private static IAccessRightService Access(bool canPost = true, bool canRollback = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string permission, CancellationToken _) => Task.FromResult(
                (permission != PermissionCodes.Post || canPost)
                && (permission != PermissionCodes.Rollback || canRollback)));
        return access.Object;
    }

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
