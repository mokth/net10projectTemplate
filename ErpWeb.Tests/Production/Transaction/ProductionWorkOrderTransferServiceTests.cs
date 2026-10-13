using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionWorkOrderTransferServiceTests : IAsyncDisposable
{
    private static readonly DateTime EffectiveAt = new(2026, 10, 2, 8, 0, 0);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IStockPostingCoordinator _coordinator;

    public ProductionWorkOrderTransferServiceTests()
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
    public async Task Post_moves_the_full_verified_balance_and_records_the_transfer_genealogy_pair()
    {
        var graph = await SeedGraphAsync();
        var service = CreateService();

        var sources = await service.SearchEligibleSourcesAsync(new());
        Assert.True(sources.Succeeded, sources.Message);
        var source = Assert.Single(sources.Data!.Rows);
        Assert.Equal(graph.SourcePoolId, source.ProductionBalLotId);
        var targets = await service.SearchEligibleTargetsAsync(graph.SourcePoolId, new());
        Assert.True(targets.Succeeded, targets.Message);
        Assert.Equal(graph.TargetMaterialId, Assert.Single(targets.Data!.Rows).WorkOrderMaterialId);

        var created = await service.CreateAsync(new ProductionWorkOrderTransferCreateRequest
        {
            SourceProductionBalLotId = graph.SourcePoolId,
            TargetWorkOrderMaterialId = graph.TargetMaterialId,
            TransferDateTime = EffectiveAt,
            Remark = "Move full balance to the consuming Work Order",
        });
        Assert.True(created.Succeeded, created.Message);
        await SetSqliteRowVersionAsync(created.Data!.Id);
        var draft = await service.GetAsync(created.Data.Id);
        Assert.True(draft.Succeeded, draft.Message);
        var postCommand = new ProductionWorkOrderTransferCommand(draft.Data!.Id, draft.Data.RowVersion.ToArray(), Guid.NewGuid());
        var posted = await service.PostAsync(postCommand);

        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(ProductionWorkOrderTransferStatuses.Posted, posted.Data!.Status);
        Assert.Equal(4.25m, posted.Data.Qty);
        Assert.Equal(10.625m, posted.Data.BaseQty);
        Assert.Equal(35.123456m, posted.Data.TransferTotalCost);

        await using var db = await _factory.CreateDbContextAsync();
        var sourcePool = await db.ProductionBalLots.SingleAsync(x => x.Uid == graph.SourcePoolId);
        Assert.Equal(0m, sourcePool.Qty);
        Assert.Equal(0m, sourcePool.BaseQty);
        Assert.Equal(0m, sourcePool.TotalCost);
        Assert.Equal(0m, sourcePool.AverageUnitCost);
        var transfer = await db.ProductionWorkOrderTransfers.SingleAsync(x => x.Uid == posted.Data.Id);
        var targetPool = await db.ProductionBalLots.SingleAsync(x => x.Uid == transfer.DestinationProductionBalLotId);
        Assert.Equal(4.25m, targetPool.Qty);
        Assert.Equal(10.625m, targetPool.BaseQty);
        Assert.Equal(35.123456m, targetPool.TotalCost);
        Assert.Equal(3.305737m, targetPool.AverageUnitCost);
        Assert.Equal(graph.TargetWorkOrderId, targetPool.WorkOrderId);
        Assert.Equal(transfer.TargetProducingRouteStepId, targetPool.ProducingRouteStepId);
        Assert.Equal(transfer.TargetProducerOperationId, targetPool.WorkOrderOperationId);
        Assert.Equal(transfer.TargetProducerProductionLocationId, targetPool.ProductionLocationId);
        Assert.Equal("LOT-TRANSFER-1", targetPool.PhysicalLotNo);
        Assert.Null(targetPool.PoolCode);

        var outbound = await db.ProductionBalLotMovements.SingleAsync(x => x.Uid == transfer.TransferOutMovementId);
        var inbound = await db.ProductionBalLotMovements.SingleAsync(x => x.Uid == transfer.TransferInMovementId);
        Assert.Equal(ProductionBalLotMovementTypes.TransferOut, outbound.MovementType);
        Assert.Equal(ProductionBalLotMovementTypes.TransferIn, inbound.MovementType);
        Assert.Equal(outbound.MovementGroupId, inbound.MovementGroupId);
        Assert.Equal(transfer.MovementGroupId, inbound.MovementGroupId);
        Assert.Equal(outbound.StockPostingId, inbound.StockPostingId);
        Assert.Equal(outbound.Qty, inbound.Qty);
        Assert.Equal(outbound.BaseQty, inbound.BaseQty);
        Assert.Equal(outbound.TotalCost, inbound.TotalCost);
        Assert.Equal(graph.TargetWorkOrderId, inbound.WorkOrderId);
        Assert.Equal(graph.TargetMaterialId, inbound.WorkOrderMaterialId);
        Assert.Equal(transfer.TargetProducerOperationId, inbound.WorkOrderOperationId);
        Assert.Equal(transfer.TargetProducingRouteStepId, inbound.RouteStepId);
        Assert.Equal(ProductionPoolValuationService.Verified, outbound.ValuationStatus);
        Assert.Equal(ProductionPoolValuationService.Verified, inbound.ValuationStatus);
        var evidence = await db.ProductionValuationEvidenceRows
            .Where(x => x.MovementId == outbound.Uid || x.MovementId == inbound.Uid).ToListAsync();
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, x => Assert.Equal(ProductionPoolValuationService.Verified, x.Status));
        Assert.Contains(await db.ProductionPoolDependencyRows.ToListAsync(), x =>
            x.ContributorMovementId == outbound.Uid && x.ConsumerMovementId == inbound.Uid);
        var posting = await db.StockPostings.SingleAsync(x => x.Id == transfer.StockPostingId);
        Assert.NotNull(posting.SealedAtUtc);
        Assert.Equal(ProductionDocumentTypes.WorkOrderTransfer, posting.SourceDocumentType);
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
        Assert.Empty(await db.StockValuationFacts.ToListAsync());
        Assert.Empty(await db.ProductionOutputs.ToListAsync());
        var orders = await db.ProductionWorkOrders.Where(x => x.Uid == graph.SourceWorkOrderId
            || x.Uid == graph.TargetWorkOrderId).ToListAsync();
        Assert.Equal(2, orders.Count);
        Assert.Equal(ProductionWorkOrderStatuses.InProgress, orders.Single(x => x.Uid == graph.SourceWorkOrderId).Status);
        Assert.Equal(ProductionWorkOrderStatuses.Released, orders.Single(x => x.Uid == graph.TargetWorkOrderId).Status);
        Assert.All(orders, x => Assert.Equal(0m, x.GoodQty));

        var replay = await service.PostAsync(postCommand);
        Assert.True(replay.Succeeded, replay.Message);
        Assert.Equal(posted.Data.StockPostingId, replay.Data!.StockPostingId);
        Assert.Single(await db.StockPostings.ToListAsync());
    }

    [Fact]
    public async Task Post_blocks_a_source_balance_that_changed_after_draft_creation()
    {
        var graph = await SeedGraphAsync();
        var service = CreateService();
        var created = await CreateDraftAsync(service, graph);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = await db.ProductionBalLots.SingleAsync(x => x.Uid == graph.SourcePoolId);
            source.LastMovementDate = EffectiveAt.AddMinutes(1);
            await db.SaveChangesAsync();
        }
        await SetSqliteRowVersionAsync(created.Id);
        var refreshed = await service.GetAsync(created.Id);

        var result = await service.PostAsync(new(created.Id, refreshed.Data!.RowVersion, Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Contains("source WIP changed", result.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.ProductionBalLotMovements.ToListAsync());
        Assert.Equal(4.25m, (await verify.ProductionBalLots.SingleAsync(x => x.Uid == graph.SourcePoolId)).Qty);
        Assert.Empty(await verify.StockPostings.ToListAsync());
    }

    [Fact]
    public async Task Target_with_a_different_producer_uom_is_not_eligible()
    {
        var graph = await SeedGraphAsync(targetProducerUom: "EA");
        var service = CreateService();

        var candidates = await service.SearchEligibleTargetsAsync(graph.SourcePoolId, new());
        var created = await service.CreateAsync(new ProductionWorkOrderTransferCreateRequest
        {
            SourceProductionBalLotId = graph.SourcePoolId,
            TargetWorkOrderMaterialId = graph.TargetMaterialId,
            TransferDateTime = EffectiveAt,
        });

        Assert.True(candidates.Succeeded, candidates.Message);
        Assert.Empty(candidates.Data!.Rows);
        Assert.False(created.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ProductionWorkOrderTransfers.ToListAsync());
    }

    [Fact]
    public async Task Transferred_wip_is_shared_eligible_when_the_existing_location_rule_matches()
    {
        var graph = await SeedGraphAsync(consumerAtProducerLocation: true, completedTargetProducer: true);
        var transfers = CreateService();
        var draft = await CreateDraftAsync(transfers, graph);
        var posted = await transfers.PostAsync(new(draft.Id, draft.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var transfer = await db.ProductionWorkOrderTransfers.SingleAsync(x => x.Uid == draft.Id);
            var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == transfer.DestinationProductionBalLotId);
            Assert.Equal(ProductionPoolValuationService.Verified,
                (await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == lot.Uid)).Status);
            Assert.Null(await ProductionCostReadiness.PoolValuationError(db, lot, CancellationToken.None));
        }

        var daily = CreateSharedWipOutputService();
        var inquiry = await daily.GetSharedWipSourceInquiryAsync(new ProductionSharedWipSourceInquiryRequest
        {
            WorkOrderMaterialId = graph.TargetMaterialId,
            ProductionDate = EffectiveAt,
        });
        Assert.True(inquiry.Succeeded, inquiry.Message);
        Assert.Single(inquiry.Data!.Sources);
        var workspace = await daily.GetWorkspaceAsync(graph.TargetConsumerOperationId, EffectiveAt);
        Assert.True(workspace.Succeeded, workspace.Message);
        var material = Assert.Single(workspace.Data!.Materials);
        Assert.Equal(4.25m, material.AvailableQty);
        Assert.Null(material.BlockingReason);

        var output = await daily.CreateAsync(DailyRequest(graph.TargetConsumerOperationId));

        Assert.True(output.Succeeded, output.Message);
        Assert.Equal(ProductionWipConsumptionPolicies.SharedBomWip, output.Data.WipConsumptionPolicy);
    }

    [Fact]
    public async Task Transferred_wip_remains_ineligible_when_the_existing_location_rule_does_not_match()
    {
        var graph = await SeedGraphAsync(completedTargetProducer: true);
        var transfers = CreateService();
        var draft = await CreateDraftAsync(transfers, graph);
        var posted = await transfers.PostAsync(new(draft.Id, draft.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);

        var output = await CreateSharedWipOutputService().GetWorkspaceAsync(graph.TargetConsumerOperationId, EffectiveAt);

        Assert.True(output.Succeeded, output.Message);
        Assert.Equal(0m, Assert.Single(output.Data!.Materials).AvailableQty);
    }

    [Fact]
    public async Task Unverified_transfer_in_evidence_does_not_make_wip_shared_eligible()
    {
        var graph = await SeedGraphAsync(consumerAtProducerLocation: true, completedTargetProducer: true);
        var transfers = CreateService();
        var draft = await CreateDraftAsync(transfers, graph);
        var posted = await transfers.PostAsync(new(draft.Id, draft.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var transfer = await db.ProductionWorkOrderTransfers.SingleAsync(x => x.Uid == draft.Id);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PrValuationEvidence SET Status = 'UNVALUED' WHERE MovementId = {transfer.TransferInMovementId};");
        }

        var output = await CreateSharedWipOutputService().GetWorkspaceAsync(graph.TargetConsumerOperationId, EffectiveAt);

        Assert.True(output.Succeeded, output.Message);
        Assert.Equal(0m, Assert.Single(output.Data!.Materials).AvailableQty);
    }

    private ProductionWorkOrderTransferService CreateService() => new(
        _factory,
        InventoryTenantTestHelper.CreateTenantContext(),
        ProductionLedgerTestFixture.Allow(MenuCodes.PlanningWorkOrderTransfer),
        new FixedCurrentDateService(EffectiveAt),
        new TestRunningNumberService(),
        new BranchStockTransactionLock(),
        _coordinator,
        new ProductionStockWriter(new StockMovementRegistry()));

    private ProductionOutputService CreateSharedWipOutputService()
    {
        var settings = new Mock<IAppSettingService>();
        settings.Setup(x => x.GetValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AppSettingScope>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IvMasterOperationResult<string?>.Ok(ProductionWipConsumptionPolicies.SharedBomWip));
        return new ProductionOutputService(_factory, InventoryTenantTestHelper.CreateTenantContext(),
            ProductionLedgerTestFixture.Allow(MenuCodes.PlanningDailyProduction),
            new FixedCurrentDateService(EffectiveAt), new TestRunningNumberService(), _coordinator,
            settings: settings.Object);
    }

    private static ProductionOutputCreateRequest DailyRequest(long operationId) => new()
    {
        WorkOrderOperationId = operationId,
        CreateRequestId = Guid.NewGuid().ToString("N"),
        ProductionDate = EffectiveAt,
        GoodQty = 1m,
        OutputLotNo = "LOT-DAILY-TRANSFER",
    };

    private async Task<ProductionWorkOrderTransferDocument> CreateDraftAsync(
        ProductionWorkOrderTransferService service, TransferGraph graph)
    {
        var created = await service.CreateAsync(new ProductionWorkOrderTransferCreateRequest
        {
            SourceProductionBalLotId = graph.SourcePoolId,
            TargetWorkOrderMaterialId = graph.TargetMaterialId,
            TransferDateTime = EffectiveAt,
        });
        Assert.True(created.Succeeded, created.Message);
        await SetSqliteRowVersionAsync(created.Data!.Id);
        var draft = await service.GetAsync(created.Data.Id);
        Assert.True(draft.Succeeded, draft.Message);
        return draft.Data!;
    }

    private async Task<TransferGraph> SeedGraphAsync(string targetProducerUom = "BOX", bool consumerAtProducerLocation = false,
        bool completedTargetProducer = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var sourceOrder = CreateOrder("WO-TRANSFER-SOURCE", ProductionWorkOrderStatuses.InProgress);
        var sourceRoute = CreateRoute(sourceOrder, "WC-SOURCE", "WIP-1", "BOX");
        var sourceOperation = CreateOperation(sourceOrder, sourceRoute, "OP-SOURCE", true);

        var targetOrder = CreateOrder("WO-TRANSFER-TARGET", ProductionWorkOrderStatuses.Released);
        var producerRoute = CreateRoute(targetOrder, "WC-TARGET-PRODUCER", "WIP-1", targetProducerUom);
        var producerOperation = CreateOperation(targetOrder, producerRoute, "OP-PRODUCE", true);
        if (completedTargetProducer)
        {
            producerOperation.GoodQty = producerOperation.PlannedOutputQty;
            producerOperation.RemainingQty = 0m;
        }
        var consumerRoute = CreateRoute(targetOrder,
            consumerAtProducerLocation ? "WC-TARGET-PRODUCER" : "WC-CONSUMER", "FG-1", "EA");
        var consumerOperation = CreateOperation(targetOrder, consumerRoute, "OP-CONSUME", false);
        var material = new ProductionWorkOrderMaterial
        {
            WorkOrder = targetOrder,
            WorkOrderOperation = consumerOperation,
            ProducingRouteStep = producerRoute,
            ComponentCode = "WIP-1",
            ComponentDescription = "Intermediate WIP",
            MfgType = "INHOUSE",
            IssueMethod = "MANUAL",
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
            RequiredQty = 10m,
            RequiredUom = "BOX",
            RequiredBaseQty = 25m,
            BaseUom = "EA",
            ConversionFactorToBase = 2.5m,
            RowVersion = [1],
        };
        targetOrder.Materials.Add(material);
        db.ProductionWorkOrders.AddRange(sourceOrder, targetOrder);
        await db.SaveChangesAsync();

        var sourceLocation = new ProductionLocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", Code = "WC-SOURCE",
            Description = "Source production location", WorkCentreCode = "WC-SOURCE", IsActive = true,
        };
        db.ProductionLocations.Add(sourceLocation);
        await db.SaveChangesAsync();
        var sourcePool = new ProductionBalLot
        {
            CompanyCode = "DEMO", BranchCode = "HQ", Kind = ProductionBalLotKinds.Wip,
            ItemCode = "WIP-1", Description = "Intermediate WIP", Qty = 4.25m, Uom = "BOX",
            BaseQty = 10.625m, BaseUom = "EA", ConversionFactorToBase = 2.5m,
            TotalCost = 35.123456m, AverageUnitCost = 3.305737m,
            WorkOrderId = sourceOrder.Uid, WorkOrderNo = sourceOrder.WorkOrderNo,
            WarehouseCode = "", LocationCode = "", LotNo = "LOT-TRANSFER-1",
            BalanceStage = "PROCESS_WIP", ProductionLocationId = sourceLocation.Id,
            StockStatusCode = "AVAILABLE", PhysicalLotNo = "LOT-TRANSFER-1", PoolCode = "POOL-TRANSFER-1",
            LotIdentityKind = "PRODUCTION", OriginType = "PRODUCED",
            ProducingRouteStepId = sourceRoute.Uid, WorkOrderOperationId = sourceOperation.Uid,
            OutputType = PrRouteOutputTypes.WipStocked, WorkCentreCode = sourceRoute.WorkCentreCode,
            ProcessCode = sourceOperation.OperationCode,
            LastMovementDate = new DateTime(2026, 10, 1, 7, 0, 0),
            LastStockEventEffectiveAt = new DateTime(2026, 10, 1, 7, 0, 0), RowVersion = [1],
        };
        db.ProductionBalLots.Add(sourcePool);
        await db.SaveChangesAsync();
        await ProductionLedgerTestFixture.SeedVerifiedPoolAsync(db, sourcePool);
        await db.Entry(sourcePool).ReloadAsync();
        Assert.Equal(StockLedgerPrecision.Money(sourcePool.TotalCost / sourcePool.BaseQty), sourcePool.AverageUnitCost);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PrProductionBalLot SET RowVersion = X'01' WHERE UID = {sourcePool.Uid};");
        return new(sourceOrder.Uid, targetOrder.Uid, sourcePool.Uid, material.Uid,
            sourceOperation.Uid, producerOperation.Uid, consumerOperation.Uid);
    }

    private static ProductionWorkOrder CreateOrder(string number, string status) => new()
    {
        CompanyCode = "DEMO", BranchCode = "HQ", WorkOrderNo = number,
        ProductCode = number.EndsWith("SOURCE", StringComparison.Ordinal) ? "FG-SOURCE" : "FG-TARGET",
        ProductDescription = "Transfer test finished good", OutputUom = "EA", Status = status,
        PlannedQty = 10m, RemainingQty = 10m, SnapshotRevision = 1,
        SnapshotHash = new string('A', 64), SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
        SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current, IsLegacySnapshot = false,
        DefinitionEffectiveDate = new DateTime(2026, 10, 1),
        PlannedStartDateTime = new DateTime(2026, 10, 1), PlannedCompletionDateTime = new DateTime(2026, 10, 3),
        RowVersion = [1],
    };

    private static ProductionWorkOrderRouteStep CreateRoute(
        ProductionWorkOrder order, string workCentre, string outputItem, string outputUom)
    {
        var route = new ProductionWorkOrderRouteStep
        {
            WorkOrder = order, StageSequence = order.RouteSteps.Count + 1,
            WorkCentreCode = workCentre, OutputItemCode = outputItem,
            OutputType = PrRouteOutputTypes.WipStocked, YieldPercent = 100m,
            OutputBaseQty = 1m, OutputUom = outputUom, OutputBaseUom = "EA",
            OutputConversionFactorToBase = outputUom == "BOX" ? 2.5m : 1m,
            PlannedQty = 10m, RowVersion = [1],
        };
        order.RouteSteps.Add(route);
        return route;
    }

    private static ProductionWorkOrderOperation CreateOperation(
        ProductionWorkOrder order, ProductionWorkOrderRouteStep route, string code, bool isFinal)
    {
        var operation = new ProductionWorkOrderOperation
        {
            WorkOrder = order, RouteStep = route, OperationCode = code, ProcessType = "MANUAL",
            ProcessSequence = 1, PlannedOutputQty = 10m, PlannedOutputUom = "EA",
            IsFinalOperation = isFinal, RemainingQty = 10m, GoodQty = 0m, RowVersion = [1],
        };
        route.Operations.Add(operation);
        order.Operations.Add(operation);
        return operation;
    }

    private async Task SetSqliteRowVersionAsync(long id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PrWorkOrderTransfer SET RowVersion = X'01' WHERE UID = {id};");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed record TransferGraph(long SourceWorkOrderId, long TargetWorkOrderId,
        long SourcePoolId, long TargetMaterialId, long SourceOperationId,
        long TargetProducerOperationId, long TargetConsumerOperationId);

    private sealed class TestRunningNumberService : IRunningNumberService
    {
        private int _next = 1;
        public Task<int> PeekNextAsync(AppDbContext db, string companyCode, string docKey,
            CancellationToken cancellationToken = default) => Task.FromResult(_next);
        public Task<int> GetNextAsync(AppDbContext db, string companyCode, string docKey,
            CancellationToken cancellationToken = default) => Task.FromResult(Interlocked.Increment(ref _next));
    }
}
