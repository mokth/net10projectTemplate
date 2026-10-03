using System.Data.Common;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionProcessHandoffServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionProcessHandoffServiceTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Posting_process1_stages_handoff_and_process2_is_capped_by_available()
    {
        var graph = await SeedTwoProcessAsync("WO-H1");
        await SetPlannedOutputAsync(graph.Process1Id, 30m);
        var sut = CreateService();

        var p1 = await CreateAndPostAsync(sut, graph.Process1Id, 30m, date: new DateTime(2026, 10, 2, 8, 0, 0));
        Assert.True(p1.Succeeded, p1.Message);

        var workspace = await sut.GetWorkspaceAsync(graph.Process2Id);
        Assert.True(workspace.Succeeded, workspace.Message);
        var handoff = Assert.Single(workspace.Data!.Materials,
            x => x.SupplySource == ProductionProcessHandoff.SupplySource);
        Assert.Equal(30m, handoff.AvailableQty);
        Assert.Null(handoff.BlockingReason);

        var over = await CreateAndPostAsync(sut, graph.Process2Id, 31m, date: new DateTime(2026, 10, 2, 9, 0, 0));
        Assert.False(over.Succeeded);
        Assert.Contains("Insufficient previous-process balance", over.Message, StringComparison.OrdinalIgnoreCase);
        await AssertBalancesUnchangedAsync(graph.OrderId, expectedHandoffQty: 30m, expectedRouteQty: 0m);

        var ok = await CreateAndPostAsync(sut, graph.Process2Id, 30m, date: new DateTime(2026, 10, 2, 9, 0, 0));
        Assert.True(ok.Succeeded, ok.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var handoffLot = await HandoffLotAsync(db, graph.OrderId, graph.Process1Id);
        Assert.NotNull(handoffLot);
        Assert.Equal(0m, handoffLot!.Qty);
        Assert.Equal(0m, handoffLot.BaseQty);

        var routeLot = await db.ProductionBalLots.SingleAsync(x =>
            x.WorkOrderId == graph.OrderId
            && x.Kind == ProductionBalLotKinds.Wip
            && x.ProducingRouteStepId == graph.RouteStepId);
        Assert.Equal(30m, routeLot.Qty);
    }

    [Fact]
    public async Task Upstream_rollback_is_blocked_until_consumer_is_rolled_back()
    {
        var graph = await SeedTwoProcessAsync("WO-H2");
        await SetPlannedOutputAsync(graph.Process1Id, 30m);
        var sut = CreateService();

        var p1 = await CreateAndPostAsync(sut, graph.Process1Id, 30m);
        var p2 = await CreateAndPostAsync(sut, graph.Process2Id, 30m, date: new DateTime(2026, 10, 1, 9, 0, 0));
        Assert.True(p1.Succeeded && p2.Succeeded, p1.Message + " / " + p2.Message);

        var blocked = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data!.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "blocked upstream",
        });
        Assert.False(blocked.Succeeded);
        Assert.Contains("downstream production", blocked.Message, StringComparison.OrdinalIgnoreCase);
        await AssertBalancesUnchangedAsync(graph.OrderId, expectedHandoffQty: 0m, expectedRouteQty: 30m);

        var reverseP2 = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p2.Data!.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "restore handoff",
        });
        Assert.True(reverseP2.Succeeded, reverseP2.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var handoff = await HandoffLotAsync(db, graph.OrderId, graph.Process1Id);
            Assert.Equal(30m, handoff!.Qty);
            Assert.Equal(30m, handoff.BaseQty);
        }

        var reverseP1 = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data!.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "clear handoff",
        });
        Assert.True(reverseP1.Succeeded, reverseP1.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var handoff = await HandoffLotAsync(db, graph.OrderId, graph.Process1Id);
            Assert.NotNull(handoff);
            Assert.Equal(0m, handoff!.Qty);
            Assert.Equal(0m, handoff.BaseQty);
            Assert.True(await db.ProductionBalLotMovements.AnyAsync(x =>
                x.ProductionBalLotId == handoff.Uid
                && x.MovementType == ProductionBalLotMovementTypes.ProduceReversal));
        }
    }

    [Fact]
    public async Task Three_process_chain_and_partial_and_mixed_quantities()
    {
        var graph = await SeedThreeProcessAsync("WO-H3");
        await SetPlannedOutputAsync(graph.Process1Id, 30m);
        await SetPlannedOutputAsync(graph.Process2Id, 10m);
        var sut = CreateService();

        Assert.True((await CreateAndPostAsync(sut, graph.Process1Id, 30m)).Succeeded);
        var ws2 = await sut.GetWorkspaceAsync(graph.Process2Id);
        Assert.Equal(30m, Assert.Single(ws2.Data!.Materials,
            x => x.SupplySource == ProductionProcessHandoff.SupplySource).AvailableQty);

        var partial = await CreateAndPostAsync(
            sut, graph.Process2Id, good: 10m, scrap: 2m, reject: 1m, hold: 1m,
            date: new DateTime(2026, 10, 1, 9, 0, 0));
        Assert.True(partial.Succeeded, partial.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var handoff1 = await HandoffLotAsync(db, graph.OrderId, graph.Process1Id);
            Assert.Equal(16m, handoff1!.Qty); // 30 - (10+2+1+1)
            var handoff2 = await HandoffLotAsync(db, graph.OrderId, graph.Process2Id);
            Assert.Equal(10m, handoff2!.Qty); // good only
        }

        var p3 = await CreateAndPostAsync(sut, graph.Process3Id, 10m, date: new DateTime(2026, 10, 1, 10, 0, 0));
        Assert.True(p3.Succeeded, p3.Message);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(0m, (await HandoffLotAsync(db, graph.OrderId, graph.Process2Id))!.Qty);
        }
    }

    [Fact]
    public async Task Mismatched_final_flag_writes_nothing()
    {
        var graph = await SeedTwoProcessAsync("WO-H4", finalOnFirst: true);
        var sut = CreateService();
        var created = await sut.CreateAsync(Request(graph.Process1Id, Guid.NewGuid().ToString("N"), 5m));
        Assert.True(created.Succeeded, created.Message);

        var posted = await sut.PostAsync(created.Data!.Uid);
        Assert.False(posted.Succeeded);
        Assert.Contains("final process must be the last process", posted.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ProductionBalLots.Where(x => x.WorkOrderId == graph.OrderId).ToListAsync());
        Assert.Equal(ProductionOutputStatuses.New,
            (await db.ProductionOutputs.SingleAsync(x => x.Uid == created.Data.Uid)).Status);
    }

    [Fact]
    public async Task Uom_contract_conversion_blank_fallback_and_mismatches()
    {
        var factorGraph = await SeedTwoProcessAsync("WO-H5", conversionFactor: 2m);
        await SetPlannedOutputAsync(factorGraph.Process1Id, 30m);
        var sut = CreateService();
        Assert.True((await CreateAndPostAsync(sut, factorGraph.Process1Id, 30m)).Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var lot = await HandoffLotAsync(db, factorGraph.OrderId, factorGraph.Process1Id);
            Assert.Equal(30m, lot!.Qty);
            Assert.Equal(60m, lot.BaseQty);
        }

        var blankProducer = await SeedTwoProcessAsync("WO-H5B", blankProducerUom: true);
        await SetPlannedOutputAsync(blankProducer.Process1Id, 5m);
        Assert.True((await CreateAndPostAsync(CreateService(), blankProducer.Process1Id, 5m)).Succeeded);

        var blankConsumer = await SeedTwoProcessAsync("WO-H5C", blankConsumerUom: true);
        await SetPlannedOutputAsync(blankConsumer.Process1Id, 5m);
        var blankSut = CreateService();
        Assert.True((await CreateAndPostAsync(blankSut, blankConsumer.Process1Id, 5m)).Succeeded);
        Assert.True((await CreateAndPostAsync(blankSut, blankConsumer.Process2Id, 5m,
            date: new DateTime(2026, 10, 1, 9, 0, 0))).Succeeded);

        var mismatchProducer = await SeedTwoProcessAsync("WO-H5D", producerUom: "KG");
        await SetPlannedOutputAsync(mismatchProducer.Process1Id, 5m);
        var badProducer = await CreateAndPostAsync(CreateService(), mismatchProducer.Process1Id, 5m);
        Assert.False(badProducer.Succeeded);
        Assert.Contains("PlannedOutputUom must equal route OutputUom", badProducer.Message, StringComparison.OrdinalIgnoreCase);

        var mismatchConsumer = await SeedTwoProcessAsync("WO-H5E", consumerUom: "KG");
        await SetPlannedOutputAsync(mismatchConsumer.Process1Id, 5m);
        var badConsumerSut = CreateService();
        Assert.True((await CreateAndPostAsync(badConsumerSut, mismatchConsumer.Process1Id, 5m)).Succeeded);
        var badConsumer = await CreateAndPostAsync(badConsumerSut, mismatchConsumer.Process2Id, 5m,
            date: new DateTime(2026, 10, 1, 9, 0, 0));
        Assert.False(badConsumer.Succeeded);
        Assert.Contains("Next process UOM", badConsumer.Message, StringComparison.OrdinalIgnoreCase);

        var blankRoute = await SeedTwoProcessAsync("WO-H5F", blankRouteUom: true, blankProducerUom: true);
        var blankRoutePost = await CreateAndPostAsync(CreateService(), blankRoute.Process1Id, 5m);
        Assert.False(blankRoutePost.Succeeded);
        Assert.Contains("Route OutputUom is blank", blankRoutePost.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Date_rule_rejects_backdated_consume_and_backdated_produce_add()
    {
        var graph = await SeedTwoProcessAsync("WO-H6");
        await SetPlannedOutputAsync(graph.Process1Id, 35m);
        var sut = CreateService();
        Assert.True((await CreateAndPostAsync(sut, graph.Process1Id, 20m,
            date: new DateTime(2026, 10, 2, 12, 0, 0))).Succeeded);

        var earlyConsume = await CreateAndPostAsync(sut, graph.Process2Id, 5m,
            date: new DateTime(2026, 10, 1, 8, 0, 0));
        Assert.False(earlyConsume.Succeeded);
        Assert.Contains("Process Seq 10", earlyConsume.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True((await CreateAndPostAsync(sut, graph.Process1Id, 10m,
            date: new DateTime(2026, 10, 3, 8, 0, 0))).Succeeded);

        var backdatedAdd = await CreateAndPostAsync(sut, graph.Process1Id, 5m,
            date: new DateTime(2026, 10, 2, 10, 0, 0));
        Assert.False(backdatedAdd.Succeeded);
        Assert.Contains("future LastMovementDate", backdatedAdd.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True((await CreateAndPostAsync(sut, graph.Process1Id, 5m,
            date: new DateTime(2026, 10, 4, 8, 0, 0))).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var lot = await HandoffLotAsync(db, graph.OrderId, graph.Process1Id);
        Assert.Equal(35m, lot!.Qty);
        Assert.Equal(new DateTime(2026, 10, 4, 8, 0, 0), lot.LastMovementDate);

        var stillEarly = await CreateAndPostAsync(sut, graph.Process2Id, 5m,
            date: new DateTime(2026, 10, 2, 11, 0, 0));
        Assert.False(stillEarly.Succeeded);
    }

    [Fact]
    public async Task Recovery_requires_new_draft_after_rollback()
    {
        var graph = await SeedTwoProcessAsync("WO-H7");
        var sut = CreateService();
        var p1 = await CreateAndPostAsync(sut, graph.Process1Id, 10m);
        Assert.True(p1.Succeeded, p1.Message);

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data!.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "recovery",
        });
        Assert.True(rollback.Succeeded, rollback.Message);

        var repostSame = await sut.PostAsync(p1.Data.Uid);
        Assert.False(repostSame.Succeeded);
        Assert.Contains("Only NEW drafts", repostSame.Message, StringComparison.OrdinalIgnoreCase);

        var replacement = await CreateAndPostAsync(sut, graph.Process1Id, 10m);
        Assert.True(replacement.Succeeded, replacement.Message);
        Assert.NotEqual(p1.Data.PostingRequestId, replacement.Data!.PostingRequestId);
    }

    [Fact]
    public async Task Prior_history_without_handoff_lot_shows_zero_available()
    {
        var graph = await SeedTwoProcessAsync("WO-H8");
        await SetPlannedOutputAsync(graph.Process1Id, 30m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var op1 = await db.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == graph.Process1Id);
            op1.GoodQty = 30m;
            op1.ProcessedQty = 30m;
            op1.RemainingQty = 470m;
            await db.SaveChangesAsync();
        }

        var workspace = await CreateService().GetWorkspaceAsync(graph.Process2Id);
        Assert.True(workspace.Succeeded, workspace.Message);
        var handoff = Assert.Single(workspace.Data!.Materials,
            x => x.SupplySource == ProductionProcessHandoff.SupplySource);
        Assert.Equal(0m, handoff.AvailableQty);

        var post = await CreateAndPostAsync(CreateService(), graph.Process2Id, 1m);
        Assert.False(post.Succeeded);
        Assert.Contains("Insufficient previous-process balance", post.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Name, TestCategories.SqlServer)]
    public async Task SqlServer_two_posts_merge_into_one_handoff_lot()
    {
        var cs = TryResolveScratch();
        if (cs is null)
        {
            return;
        }

        // A connection string (not one shared SqlConnection) ensures every factory context owns an
        // independent physical connection, matching production transaction behavior.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var graph = await SeedTwoProcessOnFactoryAsync(factory, "WO-SQL-H");
        await SetPlannedOutputAsync(factory, graph.Process1Id, 15m);
        var sut = new ProductionOutputService(
            factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            AllowDailyAccessOnly().Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            new TestRunningNumberService());

        Assert.True((await CreateAndPostAsync(sut, graph.Process1Id, 10m)).Succeeded);
        Assert.True((await CreateAndPostAsync(sut, graph.Process1Id, 5m,
            date: new DateTime(2026, 10, 1, 9, 0, 0))).Succeeded);

        await using var verify = await factory.CreateDbContextAsync();
        var lots = await verify.ProductionBalLots
            .Where(x => x.WorkOrderId == graph.OrderId
                && x.WorkOrderOperationId == graph.Process1Id
                && x.LotNo == ProductionProcessHandoff.HandoffLotNo(graph.Process1Id))
            .ToListAsync();
        Assert.Single(lots);
        Assert.Equal(15m, lots[0].Qty);
    }

    private async Task AssertBalancesUnchangedAsync(long orderId, decimal expectedHandoffQty, decimal expectedRouteQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var handoffs = await db.ProductionBalLots
            .Where(x => x.WorkOrderId == orderId && x.ProducingRouteStepId == null)
            .ToListAsync();
        var routes = await db.ProductionBalLots
            .Where(x => x.WorkOrderId == orderId && x.ProducingRouteStepId != null)
            .ToListAsync();
        Assert.Equal(expectedHandoffQty, handoffs.Sum(x => x.Qty));
        Assert.Equal(expectedRouteQty, routes.Sum(x => x.Qty));
    }

    private Task SetPlannedOutputAsync(long operationId, decimal plannedOutputQty) =>
        SetPlannedOutputAsync(_factory, operationId, plannedOutputQty);

    private static async Task SetPlannedOutputAsync(
        IDbContextFactory<AppDbContext> factory, long operationId, decimal plannedOutputQty)
    {
        await using var db = await factory.CreateDbContextAsync();
        var operation = await db.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == operationId);
        operation.PlannedOutputQty = plannedOutputQty;
        operation.RemainingQty = IvQty.Round(Math.Max(plannedOutputQty - operation.GoodQty, 0m));
        await db.SaveChangesAsync();
    }

    private static async Task<ProductionBalLot?> HandoffLotAsync(AppDbContext db, long orderId, long processId) =>
        await db.ProductionBalLots.SingleOrDefaultAsync(x =>
            x.WorkOrderId == orderId
            && x.WorkOrderOperationId == processId
            && x.LotNo == ProductionProcessHandoff.HandoffLotNo(processId));

    private async Task<IvMasterOperationResult<ProductionOutputDetail>> CreateAndPostAsync(
        ProductionOutputService sut,
        long operationId,
        decimal good,
        decimal scrap = 0m,
        decimal reject = 0m,
        decimal hold = 0m,
        DateTime? date = null)
    {
        var created = await sut.CreateAsync(Request(operationId, Guid.NewGuid().ToString("N"), good, scrap, reject, hold, date));
        if (!created.Succeeded || created.Data is null) return created;
        return await sut.PostAsync(created.Data.Uid);
    }

    private ProductionOutputService CreateService() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            AllowDailyAccessOnly().Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            new TestRunningNumberService());

    private static Mock<IAccessRightService> AllowDailyAccessOnly()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PlanningDailyProduction, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static ProductionOutputCreateRequest Request(
        long operationId,
        string token,
        decimal good,
        decimal scrap = 0m,
        decimal reject = 0m,
        decimal hold = 0m,
        DateTime? date = null) => new()
    {
        WorkOrderOperationId = operationId,
        PostingRequestId = token,
        ProductionDate = date ?? new DateTime(2026, 10, 1, 8, 0, 0),
        GoodQty = good,
        ScrapQty = scrap,
        RejectQty = reject,
        HoldQty = hold,
        OutputLotNo = "LOT-OUT",
    };

    private Task<GraphIds> SeedTwoProcessAsync(
        string workOrderNo,
        bool finalOnFirst = false,
        decimal conversionFactor = 1m,
        bool blankProducerUom = false,
        bool blankConsumerUom = false,
        bool blankRouteUom = false,
        string? producerUom = null,
        string? consumerUom = null) =>
        SeedGraphAsync(_factory, workOrderNo, processCount: 2, finalOnFirst, conversionFactor,
            blankProducerUom, blankConsumerUom, blankRouteUom, producerUom, consumerUom);

    private Task<GraphIds> SeedThreeProcessAsync(string workOrderNo) =>
        SeedGraphAsync(_factory, workOrderNo, processCount: 3);

    private static Task<GraphIds> SeedTwoProcessOnFactoryAsync(
        IDbContextFactory<AppDbContext> factory, string workOrderNo) =>
        SeedGraphAsync(factory, workOrderNo, processCount: 2);

    private static async Task<GraphIds> SeedGraphAsync(
        IDbContextFactory<AppDbContext> factory,
        string workOrderNo,
        int processCount,
        bool finalOnFirst = false,
        decimal conversionFactor = 1m,
        bool blankProducerUom = false,
        bool blankConsumerUom = false,
        bool blankRouteUom = false,
        string? producerUom = null,
        string? consumerUom = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = workOrderNo,
            ProductCode = $"FG-{workOrderNo}",
            OutputUom = "EA",
            Status = ProductionWorkOrderStatuses.Released,
            PlannedQty = 500m,
            RemainingQty = 500m,
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

        var routeUom = blankRouteUom ? null : "EA";
        var route = new ProductionWorkOrderRouteStep
        {
            WorkOrder = order,
            StageSequence = 10,
            WorkCentreCode = "WC10",
            OutputItemCode = "WIP-OUT",
            OutputType = PrRouteOutputTypes.WipStocked,
            YieldPercent = 100m,
            OutputUom = routeUom,
            OutputBaseUom = blankRouteUom ? null : "EA",
            OutputConversionFactorToBase = blankRouteUom ? null : conversionFactor,
            RowVersion = [1],
        };

        var ops = new List<ProductionWorkOrderOperation>();
        for (var i = 1; i <= processCount; i++)
        {
            var isLast = i == processCount;
            var isFirst = i == 1;
            string? uom = "EA";
            if (isFirst && blankProducerUom) uom = null;
            else if (isFirst && producerUom is not null) uom = producerUom;
            else if (!isFirst && blankConsumerUom) uom = null;
            else if (!isFirst && consumerUom is not null) uom = consumerUom;

            var op = new ProductionWorkOrderOperation
            {
                WorkOrder = order,
                RouteStep = route,
                OperationCode = $"P{i}",
                ProcessSequence = i * 10,
                ProcessType = "MANUAL",
                PlannedOutputQty = 500m,
                PlannedInputUom = uom,
                PlannedOutputUom = uom,
                IsFinalOperation = finalOnFirst ? isFirst : isLast,
                StandardDurationMinutes = 5m,
                RemainingQty = 500m,
                RowVersion = [1],
            };
            ops.Add(op);
            route.Operations.Add(op);
            order.Operations.Add(op);
        }

        order.RouteSteps.Add(route);
        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();
        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        return new GraphIds(
            order.Uid,
            route.Uid,
            ops[0].Uid,
            ops.Count > 1 ? ops[1].Uid : 0,
            ops.Count > 2 ? ops[2].Uid : 0);
    }

    private static string? TryResolveScratch()
    {
        var required = string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (required)
                Assert.Fail("Production handoff SQL Server test requires ConnectionStrings__SqlServerTestConnection.");
            return null;
        }
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
            if (!builder.InitialCatalog.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                if (required)
                    Assert.Fail("Production handoff SQL Server test requires a database name containing 'test'.");
                return null;
            }
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
            using var db = new AppDbContext(options);
            if (db.Database.IsSqlServer() && db.Database.CanConnect())
                return cs;
            if (required)
                Assert.Fail("Production handoff SQL Server test database is unavailable.");
            return null;
        }
        catch (Exception ex)
        {
            if (required)
                Assert.Fail($"Production handoff SQL Server test database is unavailable: {ex.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct GraphIds(
        long OrderId, long RouteStepId, long Process1Id, long Process2Id, long Process3Id);

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
