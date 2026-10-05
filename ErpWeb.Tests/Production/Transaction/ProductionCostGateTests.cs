using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;

[CollectionDefinition("ProductionValuationHooks", DisableParallelization = true)]
public sealed class ProductionValuationHooksCollection;

[Trait(TestCategories.Name, TestCategories.Production)]
[Collection("ProductionValuationHooks")]
public sealed class ProductionCostGateTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionCostGateTests()
    {
        _connection.Open();
        _factory = ProductionLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Ip_without_active_epoch_fails_before_stock_mutation()
    {
        var materialId = await SeedIssueGraphAsync();
        await SeedBalanceAsync(1, unitPrice: 2m, evidence: ProductionLedgerTestFixture.TestPriceEvidence);
        var sut = CreateIssueService();
        var draft = await sut.CreateAsync(await IssueRequestAsync(materialId, 1));
        Assert.True(draft.Succeeded, draft.Message);

        var posted = await sut.PostAsync([draft.Data!.BatchNo]);
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(1, posted.Data!.FailedCount);
        Assert.Contains("active V2 stock ledger", posted.Data.Batches[0].Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await db.IvBalLocs.SingleAsync(x => x.Id == 1)).StdQty);
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == draft.Data.BatchNo)).BatchStatus);
        Assert.Empty(await db.ProductionPoolValuationRows.ToListAsync());
    }

    [Fact]
    public async Task Ip_rejects_missing_negative_and_blank_cost_evidence_and_allows_evidenced_zero()
    {
        await SeedActiveEpochAsync();
        var materialId = await SeedIssueGraphAsync();
        await SeedBalanceAsync(10, unitPrice: null, evidence: "X");
        await SeedBalanceAsync(11, unitPrice: -1m, evidence: "X");
        await SeedBalanceAsync(12, unitPrice: 2m, evidence: "  ");
        await SeedBalanceAsync(13, unitPrice: 0m, evidence: ProductionLedgerTestFixture.TestPriceEvidence);
        var sut = CreateIssueService();

        Assert.Contains("Cost evidence is missing", await PostIssueAsync(sut, materialId, 10));
        Assert.Contains("Cost evidence is missing", await PostIssueAsync(sut, materialId, 11));
        Assert.Contains("Cost evidence is missing", await PostIssueAsync(sut, materialId, 12));

        var zero = await sut.CreateAsync(await IssueRequestAsync(materialId, 13, issueQty: 1m, basisQty: 1m));
        Assert.True(zero.Succeeded, zero.Message);
        var posted = await sut.PostAsync([zero.Data!.BatchNo]);
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(1, posted.Data!.SucceededCount);
        Assert.Equal(0, posted.Data.FailedCount);
        await AssertIssueLineageAsync(zero.Data.BatchNo);
    }

    [Fact]
    public async Task Ip_success_creates_sealed_verified_issue_lineage()
    {
        await SeedActiveEpochAsync();
        var materialId = await SeedIssueGraphAsync();
        await SeedBalanceAsync(20, unitPrice: 5m, evidence: ProductionLedgerTestFixture.TestPriceEvidence);
        var sut = CreateIssueService();
        var draft = await sut.CreateAsync(await IssueRequestAsync(materialId, 20));
        Assert.True(draft.Succeeded, draft.Message);
        var posted = await sut.PostAsync([draft.Data!.BatchNo]);
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(1, posted.Data!.SucceededCount);
        await AssertIssueLineageAsync(draft.Data.BatchNo);
    }

    [Fact]
    public async Task Ip_sealed_replay_from_new_draft_is_reconciliation_failure()
    {
        await SeedActiveEpochAsync();
        var materialId = await SeedIssueGraphAsync();
        await SeedBalanceAsync(21, unitPrice: 5m, evidence: ProductionLedgerTestFixture.TestPriceEvidence, qty: 20m);
        var sut = CreateIssueService();
        var draft = await sut.CreateAsync(await IssueRequestAsync(materialId, 21));
        var first = await sut.PostAsync([draft.Data!.BatchNo]);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(1, first.Data!.SucceededCount);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == draft.Data.BatchNo);
            batch.BatchStatus = IvBatchStatuses.New;
            var link = await db.ProductionPostingLinks.SingleAsync(x => x.InventoryBatchNo == draft.Data.BatchNo
                && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost);
            link.Status = ProductionPostingLinkStatuses.Draft;
            await db.SaveChangesAsync();
        }

        var replay = await sut.PostAsync([draft.Data.BatchNo]);
        Assert.True(replay.Succeeded, replay.Message);
        Assert.Equal(1, replay.Data!.FailedCount);
        Assert.Contains("not marked posted", replay.Data.Batches[0].Message);
        Assert.Contains("was not repeated", replay.Data.Batches[0].Message);
    }

    [Fact]
    public async Task Ip_invariant_failure_rolls_back_the_transaction()
    {
        await SeedActiveEpochAsync();
        var materialId = await SeedIssueGraphAsync();
        await SeedBalanceAsync(22, unitPrice: 5m, evidence: ProductionLedgerTestFixture.TestPriceEvidence);
        var sut = CreateIssueService();
        var draft = await sut.CreateAsync(await IssueRequestAsync(materialId, 22));
        ProductionMaterialIssueService.TestHookAfterIssueValuation = () =>
            throw new ProductionPostingInvariantException("forced");
        try
        {
            var posted = await sut.PostAsync([draft.Data!.BatchNo]);
            Assert.True(posted.Succeeded, posted.Message);
            Assert.Equal(1, posted.Data!.FailedCount);
            Assert.Contains("valuation evidence is incomplete", posted.Data.Batches[0].Message);
        }
        finally
        {
            ProductionMaterialIssueService.TestHookAfterIssueValuation = null;
        }

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await db.IvBalLocs.SingleAsync(x => x.Id == 22)).StdQty);
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == draft.Data.BatchNo)).BatchStatus);
        Assert.Empty(await db.ProductionBalLots.ToListAsync());
        Assert.Empty(await db.StockPostings.ToListAsync());
    }

    [Fact]
    public async Task Ip_stock_ledger_exception_is_a_controlled_rollback()
    {
        await SeedActiveEpochAsync();
        var materialId = await SeedIssueGraphAsync();
        await SeedBalanceAsync(23, unitPrice: 5m, evidence: ProductionLedgerTestFixture.TestPriceEvidence);
        var valuation = new Mock<IInventoryValuationService>();
        valuation.Setup(x => x.ValuePendingAsync(It.IsAny<StockPostingContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StockLedgerException(new(StockLedgerErrorCodes.ValuationRequired, "Valuation required.")));
        var sut = CreateIssueService(valuation.Object);
        var draft = await sut.CreateAsync(await IssueRequestAsync(materialId, 23));
        var posted = await sut.PostAsync([draft.Data!.BatchNo]);
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(1, posted.Data!.FailedCount);
        Assert.Equal("Valuation required.", posted.Data.Batches[0].Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await db.IvBalLocs.SingleAsync(x => x.Id == 23)).StdQty);
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == draft.Data.BatchNo)).BatchStatus);
    }

    [Fact]
    public async Task Daily_without_active_epoch_fails_before_production_mutation()
    {
        var graph = await SeedDailyGraphAsync();
        await SeedMaterialLotAsync(graph, verified: true);
        var sut = ProductionLedgerTestFixture.CreateProductionOutputService(_factory);
        var created = await sut.CreateAsync(DailyRequest(graph.OperationId));
        Assert.True(created.Succeeded, created.Message);
        var posted = await sut.PostAsync(created.Data!.Uid);
        Assert.False(posted.Succeeded);
        Assert.Contains("active V2 stock ledger", posted.Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(ProductionOutputStatuses.New, (await db.ProductionOutputs.SingleAsync(x => x.Uid == created.Data.Uid)).Status);
        Assert.Equal(10m, (await db.ProductionBalLots.SingleAsync()).BaseQty);
    }

    [Fact]
    public async Task Daily_rejects_missing_unvalued_and_mismatched_pools_and_zero_cost_basis()
    {
        await SeedActiveEpochAsync();
        var missing = await SeedDailyGraphAsync("WO-MISS");
        await SeedMaterialLotAsync(missing, verified: false);
        var sut = ProductionLedgerTestFixture.CreateProductionOutputService(_factory);
        var missingPost = await PostDailyAsync(sut, missing.OperationId);
        Assert.Contains("UNVALUED", missingPost);

        var unvalued = await SeedDailyGraphAsync("WO-UNVAL");
        await SeedMaterialLotAsync(unvalued, verified: true, status: ProductionPoolValuationService.Unvalued);
        Assert.Contains("UNVALUED", await PostDailyAsync(sut, unvalued.OperationId));

        var mismatch = await SeedDailyGraphAsync("WO-MISMATCH");
        await SeedMaterialLotAsync(mismatch, verified: true, trackedQty: 99m);
        Assert.Contains("reconciliation", await PostDailyAsync(sut, mismatch.OperationId));

        var none = await SeedDailyGraphAsync("WO-NONE", withMaterial: false);
        Assert.Contains("no verified consumed cost basis", await PostDailyAsync(sut, none.OperationId));
    }

    [Fact]
    public async Task Daily_success_creates_sealed_verified_consume_and_produce_lineage()
    {
        await SeedActiveEpochAsync();
        var graph = await SeedDailyGraphAsync();
        await SeedMaterialLotAsync(graph, verified: true);
        var sut = ProductionLedgerTestFixture.CreateProductionOutputService(_factory);
        var created = await sut.CreateAsync(DailyRequest(graph.OperationId));
        Assert.True(created.Succeeded, created.Message);
        var posted = await sut.PostAsync(created.Data!.Uid);
        Assert.True(posted.Succeeded, posted.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var posting = Assert.Single(await db.StockPostings.ToListAsync());
        Assert.NotNull(posting.SealedAtUtc);
        var movements = await db.ProductionBalLotMovements
            .Where(x => x.ProductionOutputId == created.Data.Uid)
            .ToListAsync();
        Assert.Contains(movements, x => x.MovementType == ProductionBalLotMovementTypes.Consume);
        Assert.Contains(movements, x => x.MovementType == ProductionBalLotMovementTypes.Produce);
        Assert.All(movements, x =>
        {
            Assert.Equal((byte)2, x.LedgerVersion);
            Assert.Equal(posting.Id, x.StockPostingId);
            Assert.Equal(ProductionPoolValuationService.Verified, x.ValuationStatus);
        });
        var evidence = await db.ProductionValuationEvidenceRows.ToListAsync();
        Assert.Equal(movements.Count, evidence.Count);
        Assert.All(evidence, x => Assert.Equal(ProductionPoolValuationService.Verified, x.Status));
        var produced = await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.Wip);
        var projection = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == produced.Uid);
        Assert.Equal(ProductionPoolValuationService.Verified, projection.Status);
        Assert.Equal(produced.BaseQty, projection.TrackedBaseQty);
        Assert.Equal(produced.TotalCost, projection.TrackedValue);
        var source = await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.MaterialIn);
        Assert.Null(source.ProductionLocationId);
    }

    [Fact]
    public async Task Daily_sealed_replay_from_new_draft_is_reconciliation_failure()
    {
        await SeedActiveEpochAsync();
        var graph = await SeedDailyGraphAsync("WO-REPLAY-D");
        await SeedMaterialLotAsync(graph, verified: true, qty: 20m, cost: 40m);
        var sut = ProductionLedgerTestFixture.CreateProductionOutputService(_factory);
        var created = await sut.CreateAsync(DailyRequest(graph.OperationId));
        Assert.True((await sut.PostAsync(created.Data!.Uid)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var output = await db.ProductionOutputs.SingleAsync(x => x.Uid == created.Data.Uid);
            output.Status = ProductionOutputStatuses.New;
            var link = await db.ProductionPostingLinks.SingleAsync(x => x.PostingRequestId == output.PostingRequestId);
            link.Status = ProductionPostingLinkStatuses.Draft;
            await db.SaveChangesAsync();
        }

        var replay = await sut.PostAsync(created.Data.Uid);
        Assert.False(replay.Succeeded);
        Assert.Contains("not marked posted", replay.Message);
    }

    [Fact]
    public async Task Daily_invariant_and_ledger_failures_roll_back()
    {
        await SeedActiveEpochAsync();
        var graph = await SeedDailyGraphAsync("WO-INV");
        await SeedMaterialLotAsync(graph, verified: true);
        var sut = ProductionLedgerTestFixture.CreateProductionOutputService(_factory);
        var created = await sut.CreateAsync(DailyRequest(graph.OperationId));
        ProductionOutputService.TestHookAfterOutputValuation = () =>
            throw new ProductionPostingInvariantException("forced");
        try
        {
            var posted = await sut.PostAsync(created.Data!.Uid);
            Assert.False(posted.Succeeded);
            Assert.Contains("valuation evidence is incomplete", posted.Message);
        }
        finally
        {
            ProductionOutputService.TestHookAfterOutputValuation = null;
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(ProductionOutputStatuses.New, (await db.ProductionOutputs.SingleAsync(x => x.Uid == created.Data.Uid)).Status);
            Assert.Equal(10m, (await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.MaterialIn)).BaseQty);
            Assert.Empty(await db.StockPostings.ToListAsync());
        }

        var valuation = new Mock<IInventoryValuationService>();
        valuation.Setup(x => x.ValuePendingAsync(It.IsAny<StockPostingContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StockLedgerException(new(StockLedgerErrorCodes.ValuationRequired, "Valuation required.")));
        var ledgerSut = ProductionLedgerTestFixture.CreateProductionOutputService(_factory, valuation: valuation.Object);
        var secondGraph = await SeedDailyGraphAsync("WO-LEDGER");
        await SeedMaterialLotAsync(secondGraph, verified: true);
        var second = await ledgerSut.CreateAsync(DailyRequest(secondGraph.OperationId));
        var ledgerPost = await ledgerSut.PostAsync(second.Data!.Uid);
        Assert.False(ledgerPost.Succeeded);
        Assert.Equal("Valuation required.", ledgerPost.Message);
    }

    private ProductionMaterialIssueService CreateIssueService(IInventoryValuationService? valuation = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var access = ProductionLedgerTestFixture.Allow(MenuCodes.PlanningMaterialIssue);
        var allocation = new ProductionMaterialAllocationService(
            _factory, tenant, access, new FixedCurrentDateService(new DateTime(2026, 10, 1)));
        return new ProductionMaterialIssueService(
            _factory, tenant, access, new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            allocation, new RunningNumberService(), new IvStockPostingRepository(),
            ProductionLedgerTestFixture.CreateInventoryPosting(_factory, valuation: valuation));
    }

    private async Task SeedActiveEpochAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await ProductionLedgerTestFixture.SeedActiveEpochAsync(db);
    }

    private async Task<long> SeedIssueGraphAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "RM001", IDesc = "Raw material", StdUom = "KG",
            StockControl = true, IsActive = true, RowVersion = [1]
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "WH01", IsActive = true, RowVersion = [1]
        });
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WorkOrderNo = "WO-" + Guid.NewGuid().ToString("N")[..6],
            SnapshotHash = new string('A', 64), ProductCode = "FG001", Status = ProductionWorkOrderStatuses.Released,
            PlannedStartDateTime = new DateTime(2026, 10, 1), PlannedCompletionDateTime = new DateTime(2026, 10, 2),
            DefinitionEffectiveDate = new DateTime(2026, 10, 1),
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current, IsLegacySnapshot = false, RowVersion = [1]
        };
        var route = new ProductionWorkOrderRouteStep
        {
            WorkOrder = order, WorkCentreCode = "WC", StageSequence = 1, OutputItemCode = "FG001", RowVersion = [1]
        };
        var operation = new ProductionWorkOrderOperation
        {
            WorkOrder = order, RouteStep = route, OperationCode = "OP", ProcessType = "MANUAL",
            ProcessSequence = 1, PlannedOutputQty = 10m, PlannedOutputUom = "KG", RowVersion = [1]
        };
        var material = new ProductionWorkOrderMaterial
        {
            WorkOrder = order, WorkOrderOperation = operation, ComponentCode = "RM001", MfgType = "BUY",
            BomPath = "RM001", IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased, RequiredQty = 10m, RequiredUom = "KG",
            RequiredBaseQty = 10m, BaseUom = "KG", ConversionFactorToBase = 1m,
            WarehouseCode = "WH01", LocationCode = "BIN-A", RowVersion = [1]
        };
        db.ProductionWorkOrderMaterials.Add(material);
        await db.SaveChangesAsync();
        return material.Uid;
    }

    private async Task SeedBalanceAsync(
        int id, decimal? unitPrice, string? evidence, decimal qty = 10m)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvBalLocs.Add(new IvBalLoc
        {
            Id = id, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM001", WhCode = "WH01",
            LocCode = $"BIN-{id}", LotNo = "", IStatus = IvItemStatuses.Active, StdQty = qty, StdUom = "KG",
            TransDate = new DateTime(2026, 9, 1), UnitPrice = unitPrice, PriceEvidence = evidence, RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    private async Task<ProductionMaterialIssueSaveRequest> IssueRequestAsync(
        long materialId, int balanceId, decimal issueQty = 4m, decimal basisQty = 4m)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var material = await db.ProductionWorkOrderMaterials.AsNoTracking().SingleAsync(x => x.Uid == materialId);
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == material.WorkOrderId);
        return new ProductionMaterialIssueSaveRequest
        {
            WorkOrderNo = order.WorkOrderNo,
            WorkOrderOperationId = material.WorkOrderOperationId!.Value,
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            ProductionQtyThisIssue = basisQty,
            TrxDateTime = new DateTime(2026, 10, 1),
            Lines =
            [
                new ProductionMaterialIssueLineRequest
                {
                    WorkOrderMaterialId = materialId,
                    IssueQty = issueQty,
                    Allocations =
                    [
                        new ProductionMaterialIssueAllocationRequest { FromBalLocId = balanceId, BaseQty = issueQty }
                    ]
                }
            ]
        };
    }

    private async Task<string> PostIssueAsync(ProductionMaterialIssueService sut, long materialId, int balanceId)
    {
        var draft = await sut.CreateAsync(await IssueRequestAsync(materialId, balanceId, issueQty: 1m, basisQty: 1m));
        Assert.True(draft.Succeeded, draft.Message);
        var posted = await sut.PostAsync([draft.Data!.BatchNo]);
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(1, posted.Data!.FailedCount);
        return posted.Data.Batches[0].Message ?? "";
    }

    private async Task AssertIssueLineageAsync(int batchNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var posting = Assert.Single(await db.StockPostings.ToListAsync());
        Assert.NotNull(posting.SealedAtUtc);
        var movements = await db.ProductionBalLotMovements
            .Where(x => x.MovementType == ProductionBalLotMovementTypes.Issue)
            .ToListAsync();
        Assert.NotEmpty(movements);
        Assert.All(movements, x =>
        {
            Assert.Equal((byte)2, x.LedgerVersion);
            Assert.Equal(posting.Id, x.StockPostingId);
            Assert.Equal(ProductionPoolValuationService.Verified, x.ValuationStatus);
        });
        foreach (var movement in movements)
        {
            var evidence = await db.ProductionValuationEvidenceRows.SingleAsync(x => x.MovementId == movement.Uid);
            Assert.Equal(ProductionPoolValuationService.Verified, evidence.Status);
            Assert.NotNull(evidence.InventoryHistoryId);
        }
        foreach (var lot in await db.ProductionBalLots.Where(x => x.Kind == ProductionBalLotKinds.MaterialIn).ToListAsync())
        {
            var pool = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == lot.Uid);
            Assert.Equal(ProductionPoolValuationService.Verified, pool.Status);
            Assert.Equal(lot.BaseQty, pool.TrackedBaseQty);
            Assert.Equal(lot.TotalCost, pool.TrackedValue);
        }
    }

    private async Task<DailyGraph> SeedDailyGraphAsync(string? workOrderNo = null, bool withMaterial = true)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WorkOrderNo = workOrderNo ?? "WO-D",
            ProductCode = "FG-OUT", OutputUom = "EA", Status = ProductionWorkOrderStatuses.Released,
            PlannedQty = 10m, SnapshotRevision = 1, SnapshotHash = new string('A', 64),
            SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current, IsLegacySnapshot = false,
            DefinitionEffectiveDate = new DateTime(2026, 10, 1),
            PlannedStartDateTime = new DateTime(2026, 10, 1), PlannedCompletionDateTime = new DateTime(2026, 10, 2),
            RowVersion = [1]
        };
        var route = new ProductionWorkOrderRouteStep
        {
            WorkOrder = order, StageSequence = 10, WorkCentreCode = "WC10", OutputItemCode = "FG-OUT",
            OutputType = PrRouteOutputTypes.WipStocked, YieldPercent = 100m, OutputUom = "EA",
            OutputBaseUom = "EA", OutputConversionFactorToBase = 1m, RowVersion = [1]
        };
        var operation = new ProductionWorkOrderOperation
        {
            WorkOrder = order, RouteStep = route, OperationCode = "OP10", ProcessSequence = 1,
            ProcessType = "MANUAL", PlannedOutputQty = 10m, PlannedOutputUom = "EA",
            IsFinalOperation = true, RemainingQty = 10m, RowVersion = [1]
        };
        route.Operations.Add(operation);
        order.RouteSteps.Add(route);
        order.Operations.Add(operation);
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();
        long? materialId = null;
        if (withMaterial)
        {
            var material = new ProductionWorkOrderMaterial
            {
                WorkOrder = order, WorkOrderOperation = operation, ComponentCode = "RM-1",
                IssueMethod = PrMaterialIssueMethods.Manual, SupplySource = PrMaterialSupplySources.Purchased,
                RequiredQty = 10m, RequiredBaseQty = 10m, RequiredUom = "EA", BaseUom = "EA",
                ConversionFactorToBase = 1m, RowVersion = [1]
            };
            db.ProductionWorkOrderMaterials.Add(material);
            await db.SaveChangesAsync();
            materialId = material.Uid;
        }
        return new DailyGraph(order.Uid, operation.Uid, materialId);
    }

    private async Task SeedMaterialLotAsync(
        DailyGraph graph, bool verified, string? status = null, decimal qty = 10m, decimal cost = 20m,
        decimal? trackedQty = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lot = new ProductionBalLot
        {
            CompanyCode = "DEMO", BranchCode = "HQ", Kind = ProductionBalLotKinds.MaterialIn,
            ItemCode = "RM-1", Qty = qty, Uom = "EA", BaseQty = qty, BaseUom = "EA",
            ConversionFactorToBase = 1m, TotalCost = cost, AverageUnitCost = qty == 0m ? 0m : cost / qty,
            WorkOrderId = graph.OrderId, WorkOrderNo = "WO-D", WorkOrderMaterialId = graph.MaterialId,
            WarehouseCode = "WH01", LocationCode = "BIN-A", LotNo = "RM-LOT",
            LastMovementDate = new DateTime(2026, 10, 1, 7, 0, 0), RowVersion = [1]
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync();
        if (verified)
        {
            db.ProductionPoolValuationRows.Add(new ProductionPoolValuation
            {
                ProductionBalLotId = lot.Uid,
                Status = status ?? ProductionPoolValuationService.Verified,
                TrackedBaseQty = trackedQty ?? lot.BaseQty,
                TrackedValue = lot.TotalCost
            });
            await db.SaveChangesAsync();
        }
    }

    private static ProductionOutputCreateRequest DailyRequest(long operationId) => new()
    {
        WorkOrderOperationId = operationId,
        PostingRequestId = Guid.NewGuid().ToString("N"),
        ProductionDate = new DateTime(2026, 10, 1, 8, 0, 0),
        GoodQty = 2m,
        OutputLotNo = "LOT-1"
    };

    private static async Task<string> PostDailyAsync(ProductionOutputService sut, long operationId)
    {
        var created = await sut.CreateAsync(DailyRequest(operationId));
        Assert.True(created.Succeeded, created.Message);
        var posted = await sut.PostAsync(created.Data!.Uid);
        Assert.False(posted.Succeeded);
        return posted.Message ?? "";
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct DailyGraph(long OrderId, long OperationId, long? MaterialId);
}
