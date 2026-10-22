using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
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
    private readonly TestRunningNumberService _numbers = new();

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
        Assert.Equal("NEW", rollback.Data!.Status);
        Assert.Equal(2, rollback.Data.Revision);
        Assert.NotNull(rollback.Data.PostingId);
        Assert.NotNull(rollback.Data.ReversalPostingId);

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
    public async Task Restated_adjust_out_displays_and_rolls_back_its_effective_cost_while_manual_in_stays_frozen()
    {
        var (workOrderId, poolId) = await SeedWorkOrderAndPoolAsync();
        var service = CreateService();
        var initial = await SaveAndPostAsync(service, workOrderId, poolId, 3m);
        var posted = await SaveAndPostAsync(service, workOrderId, poolId, -1m);
        var batchId = posted.Id;

        long originalMovementId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var movement = await db.ProductionBalLotMovements.SingleAsync(x => x.StockPostingId == posted.PostingId);
            originalMovementId = movement.Uid;
            Assert.Equal(1m, movement.TotalCost);
            var owner = await db.StockPostings.SingleAsync(x => x.Id == movement.StockPostingId);
            var epoch = await db.StockLedgerEpochs.SingleAsync();
            var counter = await db.StockPostingBranchSequences.SingleAsync();
            var rootAt = EffectiveAt.AddDays(-1);
            var root = new StockPosting
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epoch.Id,
                PostingSequence = ++counter.LastSequence,
                RequestId = Guid.NewGuid(), CommandType = "PADJ_RESTATEMENT_TEST",
                RequestFingerprint = new string('C', 64),
                SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
                SourceDocumentId = "PADJ-COST", SourceDocumentNo = "PADJ-COST",
                DocumentRevision = 1, PostingRole = "PRIMARY",
                SourceSnapshotJson = "{}", SourceSnapshotHash = new string('D', 64),
                EffectiveAt = rootAt, BusinessDate = rootAt.Date, PeriodKey = "2026-10",
                PostedAtUtc = DateTime.UtcNow, PostedBy = "test"
            };
            db.StockPostings.Add(root);
            await db.SaveChangesAsync();
            db.StockReplayRuns.Add(new StockReplayRun
            {
                CompanyCode = "DEMO", BranchCode = "HQ", RootStockPostingId = root.Id,
                RootRequestId = root.RequestId, RootDocumentType = root.SourceDocumentType,
                RootDocumentId = root.SourceDocumentId, EarliestEffectiveAt = rootAt,
                LatestEffectiveAt = owner.EffectiveAt, PlanHash = new string('E', 64),
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "test"
            });
            await db.SaveChangesAsync();
            var context = new StockPostingContext(db, epoch, root, "test");
            var receipt = await db.ProductionBalLotMovements.SingleAsync(x => x.StockPostingId == initial.PostingId);
            var receiptCost = (await ProductionEffectiveCostResolver.ResolveAsync(db, "DEMO", "HQ", [receipt.Uid]))[receipt.Uid];
            var frozen = await Assert.ThrowsAsync<StockLedgerException>(() =>
                ProductionCostRestatementWriter.AppendWithinRootAsync(context, receiptCost, 4.5m, 1.5m));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, frozen.Error.Code);
            var original = (await ProductionEffectiveCostResolver.ResolveAsync(db, "DEMO", "HQ", [movement.Uid]))[movement.Uid];
            var restatement = await ProductionCostRestatementWriter.AppendWithinRootAsync(context, original, 1.5m, 1.5m);
            Assert.NotNull(restatement.Replacement);
            var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == poolId);
            var valuation = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == poolId);
            pool.TotalCost = valuation.TrackedValue = 1.5m;
            pool.AverageUnitCost = 0.75m;
            root.SealedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var current = await service.GetAsync(batchId);
        Assert.True(current.Succeeded, current.Message);
        Assert.Equal(1.5m, Assert.Single(current.Data!.Lines).TotalValue);
        var rollback = await service.RollbackAsync(new(batchId, current.Data.RowVersion,
            Guid.NewGuid(), "Reverse restated PADJ"));
        Assert.True(rollback.Succeeded, rollback.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == poolId);
            var valuation = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == poolId);
            Assert.Equal(3m, pool.BaseQty);
            Assert.Equal(3m, pool.TotalCost);
            Assert.Equal(3m, valuation.TrackedValue);
            var inverse = await db.ProductionBalLotMovements.SingleAsync(x => x.OriginalMovementId == originalMovementId);
            Assert.Equal(1.5m, inverse.TotalCost);
            var facts = await db.ProductionStockAdjustmentFacts.Where(x => x.BatchId == batchId)
                .OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(1m, facts[0].TotalValue);
            Assert.Equal(1.5m, facts[1].TotalValue);
            Assert.Equal(facts[0].Id, facts[1].ReversesFactId);
        }
    }

    private async Task<ProductionStockAdjustmentDocument> SaveAndPostAsync(
        ProductionStockAdjustmentService service, long workOrderId, long poolId, decimal quantity)
    {
        var saved = await service.SaveAsync(new ProductionStockAdjustmentSaveRequest
        {
            WorkOrderId = workOrderId,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            EffectiveDate = EffectiveAt,
            Lines = [new ProductionStockAdjustmentLineInput
            {
                ProductionBalLotId = poolId,
                AdjustmentQty = quantity,
                Reason = ProductionStockAdjustmentReasons.Count,
                CostEvidenceType = quantity > 0m ? InventoryCostEvidenceTypes.ManualApproved : null,
                ProposedUnitCost = quantity > 0m ? 1m : null,
                CostOverrideReason = quantity > 0m ? "Approved opening valuation" : null,
            }],
        });
        Assert.True(saved.Succeeded, saved.Message);
        await SetSqliteRowVersionAsync(saved.Data!.Id);
        var draft = await service.GetAsync(saved.Data.Id);
        var posted = await service.PostAsync(new(saved.Data.Id, draft.Data!.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);
        return posted.Data!;
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
    public async Task Future_adjustment_is_saved_as_draft_and_post_is_blocked()
    {
        var (workOrderId, poolId) = await SeedWorkOrderAndPoolAsync();
        var service = CreateService();
        var saved = await service.SaveAsync(new ProductionStockAdjustmentSaveRequest
        {
            WorkOrderId = workOrderId,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            EffectiveDate = EffectiveAt.AddDays(2),
            Lines = [new ProductionStockAdjustmentLineInput
            {
                ProductionBalLotId = poolId,
                AdjustmentQty = 3m,
                Reason = ProductionStockAdjustmentReasons.Count,
                CostEvidenceType = InventoryCostEvidenceTypes.ManualApproved,
                ProposedUnitCost = 1m,
                CostOverrideReason = "Approved future receipt",
            }],
        });
        Assert.True(saved.Succeeded, saved.Message);
        await SetSqliteRowVersionAsync(saved.Data!.Id);
        var draft = await service.GetAsync(saved.Data.Id);
        Assert.True(draft.Succeeded, draft.Message);

        var post = await service.PostAsync(new(saved.Data.Id, draft.Data!.RowVersion, Guid.NewGuid()));
        Assert.False(post.Succeeded);
        Assert.Contains("after the business date", post.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ProductionBalLotMovements.ToListAsync());
        Assert.Equal(0m, (await db.ProductionBalLots.SingleAsync(x => x.Uid == poolId)).BaseQty);
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
        var savedDraftLink = Assert.Single(await db.ProductionPostingLinks
            .Where(x => x.WorkOrderId == workOrderId).ToListAsync());
        Assert.Equal(ProductionPostingLinkStatuses.Draft, savedDraftLink.Status);
        Assert.NotEqual(request.RequestId.ToString("N"), savedDraftLink.PostingRequestId);
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
        _numbers,
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
        private int _next = 100;
        public Task<int> PeekNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) => Task.FromResult(_next);
        public Task<int> GetNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) => Task.FromResult(_next++);
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
