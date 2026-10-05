using System.Data.Common;
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
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionMaterialIssueSequenceOverlapTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionMaterialIssueSequenceOverlapTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        ProductionLedgerTestFixture.SeedActiveEpochAsync(db).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Next_process_material_issue_is_blocked_until_predecessor_posts_good()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-SEQ");
        await SeedBalanceAsync(50, 20m);
        var issues = CreateIssueService();

        var search = await issues.SearchEligibleOperationsAsync(new ProductionMaterialIssueOperationQuery
        {
            WorkOrderNo = "WO-IP-SEQ",
            ExactMatch = true,
            Take = 20,
        });
        Assert.True(search.Succeeded, search.Message);
        var p2Row = Assert.Single(search.Data!.Rows, x => x.WorkOrderOperationId == graph.Process2Id);
        Assert.False(p2Row.IsSequenceEligible);

        var preview = await issues.GetBomPreviewAsync(graph.Process2Id, 1m, new DateTime(2026, 10, 1));
        Assert.False(preview.Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 50, 1m));
        Assert.False(draft.Succeeded);
        Assert.Contains("must post Good qty first", draft.Message, StringComparison.OrdinalIgnoreCase);

        var outputs = CreateOutputService();
        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var opened = await issues.SearchEligibleOperationsAsync(new ProductionMaterialIssueOperationQuery
        {
            WorkOrderNo = "WO-IP-SEQ",
            ExactMatch = true,
            Take = 20,
        });
        Assert.True(Assert.Single(opened.Data!.Rows, x => x.WorkOrderOperationId == graph.Process2Id).IsSequenceEligible);

        var allowedPreview = await issues.GetBomPreviewAsync(graph.Process2Id, 1m, new DateTime(2026, 10, 1));
        Assert.True(allowedPreview.Succeeded, allowedPreview.Message);

        var saved = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 50, 1m));
        Assert.True(saved.Succeeded, saved.Message);
        var posted = await issues.PostAsync([saved.Data!.BatchNo]);
        Assert.True(posted.Succeeded && posted.Data!.SucceededCount == 1,
            posted.Data?.Batches.FirstOrDefault()?.Message ?? posted.Message);
    }

    [Fact]
    public async Task Stale_next_process_issue_draft_is_rejected_after_predecessor_good_returns_to_zero()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-STALE");
        await SeedBalanceAsync(51, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 51, 1m));
        Assert.True(draft.Succeeded, draft.Message);

        var rollback = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "clear predecessor Good",
        });
        Assert.True(rollback.Succeeded, rollback.Message);

        var post = await issues.PostAsync([draft.Data!.BatchNo]);
        Assert.True(post.Succeeded, post.Message);
        Assert.Equal(0, post.Data!.SucceededCount);
        Assert.Equal(1, post.Data.FailedCount);
        Assert.Contains("must post Good qty first",
            post.Data.Batches[0].Message, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        Assert.Empty(await db.ProductionMaterialMovements
            .Where(x => x.MovementType == ProductionMaterialMovementTypes.Issue)
            .ToListAsync());
    }

    [Fact]
    public async Task Active_next_process_issue_blocks_predecessor_rollback_to_zero_and_clears_after_issue_rollback()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-RB");
        await SeedBalanceAsync(52, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 52, 1m));
        Assert.True(draft.Succeeded, draft.Message);
        var posted = await issues.PostAsync([draft.Data!.BatchNo]);
        Assert.True(posted.Succeeded && posted.Data!.SucceededCount == 1, posted.Message);

        var blocked = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "active downstream issue",
        });
        Assert.False(blocked.Succeeded);
        Assert.Contains("downstream Issue-to-Production remains active", blocked.Message, StringComparison.OrdinalIgnoreCase);

        var issueRollback = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
        {
            PostingRequestId = Guid.NewGuid().ToString("N"),
            InventoryBatchNo = draft.Data.BatchNo,
            Reason = "clear issued material",
        });
        Assert.True(issueRollback.Succeeded, issueRollback.Message);

        var allowed = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "issue already reversed",
        });
        Assert.True(allowed.Succeeded, allowed.Message);
    }

    [Fact]
    public async Task Fully_returned_downstream_issue_does_not_block_predecessor_rollback()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-RET");
        await SeedBalanceAsync(53, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 53, 1m));
        Assert.True(draft.Succeeded, draft.Message);
        Assert.True((await issues.PostAsync([draft.Data!.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            material.ReturnedQty = material.IssuedQty;
            await db.SaveChangesAsync();
        }

        var rollback = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "returned issue has no net qty",
        });
        Assert.True(rollback.Succeeded, rollback.Message);
    }

    [Fact]
    public async Task Later_stage_material_issue_stays_blocked_on_partial_previous_stage()
    {
        var graph = await SeedTwoStageWithLaterMaterialAsync("WO-IP-STAGE");
        await SeedBalanceAsync(54, 20m);
        var issues = CreateIssueService();
        var outputs = CreateOutputService();

        var before = await issues.GetBomPreviewAsync(graph.LaterOperationId, 1m, new DateTime(2026, 10, 1));
        Assert.False(before.Succeeded);
        Assert.Contains("Previous Stage", before.Message, StringComparison.OrdinalIgnoreCase);

        var partial = await outputs.CreateAsync(OutputRequest(graph.FirstOperationId, 4m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(partial.Succeeded, partial.Message);
        Assert.True((await outputs.PostAsync(partial.Data!.Uid)).Succeeded);

        var stillBlocked = await issues.CreateAsync(await CreateIssueSaveAsync(graph.WorkOrderNo, graph.LaterOperationId, graph.MaterialId, 54, 1m));
        Assert.False(stillBlocked.Succeeded);
        Assert.Contains("Previous Stage", stillBlocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Later_stage_active_issue_blocks_predecessor_stage_rollback()
    {
        var graph = await SeedTwoStageWithLaterMaterialAsync("WO-IP-STAGE-RB");
        await SeedBalanceAsync(55, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var first = await outputs.CreateAsync(OutputRequest(graph.FirstOperationId, 10m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(first.Succeeded, first.Message);
        Assert.True((await outputs.PostAsync(first.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph.WorkOrderNo, graph.LaterOperationId, graph.MaterialId, 55, 1m));
        Assert.True(draft.Succeeded, draft.Message);
        Assert.True((await issues.PostAsync([draft.Data!.BatchNo])).Succeeded);

        var rollback = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = first.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "later stage issue is active",
        });
        Assert.False(rollback.Succeeded);
        Assert.Contains("downstream Issue-to-Production remains active", rollback.Message, StringComparison.OrdinalIgnoreCase);
    }

    private ProductionOutputService CreateOutputService() =>
        ProductionLedgerTestFixture.CreateProductionOutputService(_factory);

    private ProductionMaterialIssueService CreateIssueService()
    {
        var access = Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var allocation = new ProductionMaterialAllocationService(
            _factory, tenant, access.Object, new FixedCurrentDateService(new DateTime(2026, 10, 1)));
        return new ProductionMaterialIssueService(
            _factory, tenant, access.Object, new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            allocation, new RunningNumberService(), new IvStockPostingRepository(),
            ProductionLedgerTestFixture.CreateInventoryPosting(_factory));
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static ProductionOutputCreateRequest OutputRequest(long operationId, decimal good, DateTime date) => new()
    {
        WorkOrderOperationId = operationId,
        PostingRequestId = Guid.NewGuid().ToString("N"),
        ProductionDate = date,
        GoodQty = good,
        OutputLotNo = "LOT-1",
    };

    private async Task<ProductionMaterialIssueSaveRequest> CreateIssueSaveAsync(GraphIds graph, int balanceId, decimal issueQty) =>
        await CreateIssueSaveAsync(graph.WorkOrderNo, graph.Process2Id, graph.MaterialId, balanceId, issueQty);

    private async Task<ProductionMaterialIssueSaveRequest> CreateIssueSaveAsync(
        string workOrderNo, long operationId, long materialId, int balanceId, decimal issueQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.WorkOrderNo == workOrderNo);
        return new ProductionMaterialIssueSaveRequest
        {
            WorkOrderNo = workOrderNo,
            WorkOrderOperationId = operationId,
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            ProductionQtyThisIssue = 1m,
            TrxDateTime = new DateTime(2026, 10, 1),
            Lines =
            [
                new ProductionMaterialIssueLineRequest
                {
                    WorkOrderMaterialId = materialId,
                    IssueQty = issueQty,
                    Allocations =
                    [
                        new ProductionMaterialIssueAllocationRequest
                        {
                            FromBalLocId = balanceId,
                            BaseQty = issueQty,
                        }
                    ]
                }
            ]
        };
    }

    private async Task SeedBalanceAsync(int id, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvBalLocs.Add(new IvBalLoc
        {
            Id = id,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "RM001",
            WhCode = "WH01",
            LocCode = "BIN-A",
            LotNo = "",
            IStatus = IvItemStatuses.Active,
            StdQty = qty,
            StdUom = "EA",
            TransDate = new DateTime(2026, 9, 1),
            UnitPrice = 2m,
            PriceEvidence = ProductionLedgerTestFixture.TestPriceEvidence,
            RowVersion = [1],
        });
        await db.SaveChangesAsync();
    }

    private async Task<GraphIds> SeedTwoProcessWithMaterialAsync(string workOrderNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        EnsureStock(db);
        var order = Order(workOrderNo, plannedQty: 100m);
        var route = Route(order, 10, "WC10", PrRouteOutputTypes.WipStocked);
        var first = Operation(order, route, "P1", 10, planned: 100m, isFinal: false);
        var later = Operation(order, route, "P2", 20, planned: 100m, isFinal: true);
        var material = Material(order, later, requiredQty: 100m);
        route.Operations.Add(first);
        route.Operations.Add(later);
        order.RouteSteps.Add(route);
        order.Operations.Add(first);
        order.Operations.Add(later);
        db.ProductionWorkOrderMaterials.Add(material);
        var p1Material = new ProductionWorkOrderMaterial
        {
            WorkOrder = order,
            WorkOrderOperation = first,
            LineNo = 1,
            ComponentCode = "RM-P1",
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased,
            RequiredQty = 100m,
            RequiredUom = "EA",
            RequiredBaseQty = 100m,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            WarehouseCode = "WH01",
            LocationCode = "BIN-A",
            RowVersion = [1],
        };
        db.ProductionWorkOrderMaterials.Add(p1Material);
        await db.SaveChangesAsync();
        db.ProductionBalLots.Add(new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.MaterialIn,
            ItemCode = "RM-P1",
            Qty = 500m,
            Uom = "EA",
            BaseQty = 500m,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            TotalCost = 500m,
            AverageUnitCost = 1m,
            WorkOrderId = order.Uid,
            WorkOrderNo = workOrderNo,
            WorkOrderMaterialId = p1Material.Uid,
            WarehouseCode = "WH01",
            LocationCode = "BIN-A",
            LotNo = "P1-LOT",
            LastMovementDate = new DateTime(2026, 9, 30),
            RowVersion = [1],
        });
        await db.SaveChangesAsync();
        var p1Lot = await db.ProductionBalLots.SingleAsync(x => x.WorkOrderMaterialId == p1Material.Uid);
        db.ProductionPoolValuationRows.Add(new ProductionPoolValuation
        {
            ProductionBalLotId = p1Lot.Uid,
            Status = ProductionPoolValuationService.Verified,
            TrackedBaseQty = p1Lot.BaseQty,
            TrackedValue = p1Lot.TotalCost,
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new GraphIds(workOrderNo, order.Uid, first.Uid, later.Uid, material.Uid);
    }

    private async Task<StageGraphIds> SeedTwoStageWithLaterMaterialAsync(string workOrderNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        EnsureStock(db);
        var order = Order(workOrderNo, plannedQty: 10m);
        var firstRoute = Route(order, 10, "WC-A", PrRouteOutputTypes.WipNonstock);
        var laterRoute = Route(order, 20, "WC-B", PrRouteOutputTypes.WipNonstock);
        var first = Operation(order, firstRoute, "P1", 10, planned: 10m, isFinal: true);
        var later = Operation(order, laterRoute, "P2", 10, planned: 10m, isFinal: true);
        var material = Material(order, later, requiredQty: 10m);
        firstRoute.Operations.Add(first);
        laterRoute.Operations.Add(later);
        order.RouteSteps.Add(firstRoute);
        order.RouteSteps.Add(laterRoute);
        order.Operations.Add(first);
        order.Operations.Add(later);
        db.ProductionWorkOrderMaterials.Add(material);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new StageGraphIds(workOrderNo, first.Uid, later.Uid, material.Uid);
    }

    private static void EnsureStock(AppDbContext db)
    {
        if (db.IvStockMasters.Any())
            return;
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "RM001",
            IDesc = "Raw material",
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            RowVersion = [1],
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "WH01",
            IsActive = true,
            RowVersion = [1],
        });
    }

    private static ProductionWorkOrder Order(string workOrderNo, decimal plannedQty) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        WorkOrderNo = workOrderNo,
        ProductCode = "FG001",
        OutputUom = "EA",
        Status = ProductionWorkOrderStatuses.Released,
        PlannedQty = plannedQty,
        RemainingQty = plannedQty,
        SnapshotRevision = 1,
        SnapshotHash = new string('A', 64),
        SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
        SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
        IsLegacySnapshot = false,
        DefinitionEffectiveDate = new DateTime(2026, 10, 1),
        PlannedStartDateTime = new DateTime(2026, 10, 1),
        PlannedCompletionDateTime = new DateTime(2026, 10, 2),
        RowVersion = [1],
    };

    private static ProductionWorkOrderRouteStep Route(
        ProductionWorkOrder order, int stage, string workCentre, string outputType) => new()
    {
        WorkOrder = order,
        StageSequence = stage,
        WorkCentreCode = workCentre,
        OutputItemCode = "WIP-OUT",
        OutputType = outputType,
        YieldPercent = 100m,
        OutputUom = "EA",
        OutputBaseUom = "EA",
        OutputConversionFactorToBase = 1m,
        RowVersion = [1],
    };

    private static ProductionWorkOrderOperation Operation(
        ProductionWorkOrder order,
        ProductionWorkOrderRouteStep route,
        string code,
        int processSequence,
        decimal planned,
        bool isFinal) => new()
    {
        WorkOrder = order,
        RouteStep = route,
        OperationCode = code,
        WorkCentreCode = route.WorkCentreCode,
        ProcessSequence = processSequence,
        ProcessType = "MANUAL",
        PlannedOutputQty = planned,
        PlannedInputUom = "EA",
        PlannedOutputUom = "EA",
        IsFinalOperation = isFinal,
        RemainingQty = planned,
        RowVersion = [1],
    };

    private static ProductionWorkOrderMaterial Material(
        ProductionWorkOrder order, ProductionWorkOrderOperation operation, decimal requiredQty) => new()
    {
        WorkOrder = order,
        WorkOrderOperation = operation,
        LineNo = 2,
        ComponentCode = "RM001",
        IssueMethod = PrMaterialIssueMethods.Manual,
        SupplySource = PrMaterialSupplySources.Purchased,
        RequiredQty = requiredQty,
        RequiredUom = "EA",
        RequiredBaseQty = requiredQty,
        BaseUom = "EA",
        ConversionFactorToBase = 1m,
        WarehouseCode = "WH01",
        LocationCode = "BIN-A",
        RowVersion = [1],
    };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct GraphIds(
        string WorkOrderNo, long OrderId, long Process1Id, long Process2Id, long MaterialId);

    private readonly record struct StageGraphIds(
        string WorkOrderNo, long FirstOperationId, long LaterOperationId, long MaterialId);

    private sealed class TestRunningNumberService : IRunningNumberService
    {
        private int _next;
        public Task<int> PeekNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_next + 1);
        public Task<int> GetNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Increment(ref _next));
    }

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
