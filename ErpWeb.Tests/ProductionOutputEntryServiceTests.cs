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
using Moq;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionOutputEntryServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionOutputEntryServiceTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.PrShifts.Add(new PrShift { CompCode = "DEMO", BranchCode = "HQ", ShiftCd = "S1", ShiftDes = "Day" });
        db.PrMachines.AddRange(
            new PrMachine { CompCode = "DEMO", BranchCode = "HQ", MachineCd = "MC1", ProcessCd = "OP10", MachineDes = "Line 1", Active = true },
            new PrMachine { CompCode = "DEMO", BranchCode = "HQ", MachineCd = "MC2", ProcessCd = "OTHER", MachineDes = "Wrong process", Active = true },
            new PrMachine { CompCode = "DEMO", BranchCode = "HQ", MachineCd = "MC3", ProcessCd = "OP10", MachineDes = "Inactive line", Active = false });
        db.PrOperators.AddRange(
            new PrOperator { CompanyCode = "DEMO", BranchCode = "HQ", Code = "OP1", Name = "Active operator", Active = true },
            new PrOperator { CompanyCode = "DEMO", BranchCode = "HQ", Code = "OPX", Name = "Inactive operator", Active = false });
        db.SaveChanges();
    }

    [Fact]
    public void Entry_query_defaults_to_exact_twenty_row_pages()
    {
        var query = new ProductionEligibleOperationQuery();

        Assert.Equal(20, query.Take);
        Assert.Equal(0, query.Skip);
        Assert.False(query.ExactMatch);
        Assert.Null(query.RawMaterialCode);
    }

    [Fact]
    public async Task Get_is_scoped_and_direct_entry_lookups_use_daily_access_only()
    {
        var graph = await SeedGraphAsync("WO-SCOPE");
        var access = AllowDailyAccessOnly();
        var hq = CreateService(access.Object);
        var created = await hq.CreateAsync(Request(graph.OperationId, Guid.NewGuid().ToString("N")));

        Assert.True(created.Succeeded, created.Message);
        var outputId = created.Data!.Uid;

        var otherBranch = CreateService(access.Object, branch: "BR2");
        var hidden = await otherBranch.GetAsync(outputId);
        Assert.False(hidden.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, hidden.ErrorCode);

        var lookups = await hq.GetEntryLookupsAsync(graph.OperationId);
        Assert.True(lookups.Succeeded, lookups.Message);
        Assert.Contains(lookups.Data!.Shifts, x => x.Code == "S1");
        Assert.Contains(lookups.Data.Machines, x => x.Code == "MC1");
        Assert.Contains(lookups.Data.Operators, x => x.Code == "OP1");
        Assert.All(access.Invocations.Where(x => x.Method.Name == nameof(IAccessRightService.CanAsync)), invocation =>
            Assert.Equal(MenuCodes.PlanningDailyProduction, (string)invocation.Arguments[0]));
    }

    [Fact]
    public async Task Create_replay_is_idempotent_and_snapshots_the_planned_machine()
    {
        var graph = await SeedGraphAsync("WO-REPLAY");
        var sut = CreateService();
        var request = Request(graph.OperationId, Guid.NewGuid().ToString("N"));

        var first = await sut.CreateAsync(request);
        var replay = await sut.CreateAsync(request);

        Assert.True(first.Succeeded, first.Message);
        Assert.True(replay.Succeeded, replay.Message);
        Assert.Equal(first.Data!.Uid, replay.Data!.Uid);
        Assert.Equal("MC1", first.Data.PlannedMachineCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.ProductionOutputs.ToListAsync());
        Assert.Single(await db.ProductionPostingLinks.ToListAsync());
        Assert.Equal(request.PostingRequestId, (await db.ProductionOutputs.SingleAsync()).PostingRequestId);
    }

    [Fact]
    public async Task Reference_validation_rejects_wrong_process_and_inactive_values_but_allows_blanks()
    {
        var graph = await SeedGraphAsync("WO-REFERENCES");
        var sut = CreateService();

        var wrongProcess = Request(graph.OperationId, Guid.NewGuid().ToString("N"));
        wrongProcess.ActualMachineCode = "MC2";
        var wrongProcessResult = await sut.CreateAsync(wrongProcess);
        Assert.False(wrongProcessResult.Succeeded);
        Assert.Contains("process", wrongProcessResult.Message, StringComparison.OrdinalIgnoreCase);

        var inactive = Request(graph.OperationId, Guid.NewGuid().ToString("N"));
        inactive.OperatorCode = "OPX";
        var inactiveResult = await sut.CreateAsync(inactive);
        Assert.False(inactiveResult.Succeeded);
        Assert.Contains("inactive", inactiveResult.Message, StringComparison.OrdinalIgnoreCase);

        var optional = Request(graph.OperationId, Guid.NewGuid().ToString("N"));
        optional.ShiftCode = null;
        optional.ActualMachineCode = null;
        optional.OperatorCode = null;
        var optionalResult = await sut.CreateAsync(optional);
        Assert.True(optionalResult.Succeeded, optionalResult.Message);
    }

    [Fact]
    public async Task Update_cannot_change_operation_after_creation()
    {
        var graph = await SeedGraphAsync("WO-IMMUTABLE");
        var sut = CreateService();
        var created = await sut.CreateAsync(Request(graph.OperationId, Guid.NewGuid().ToString("N")));
        Assert.True(created.Succeeded, created.Message);

        var update = new ProductionOutputUpdateRequest
        {
            OutputId = created.Data!.Uid,
            WorkOrderOperationId = graph.OperationId + 999,
            PostingRequestId = created.Data.PostingRequestId,
            ProductionDate = created.Data.ProductionDate,
            GoodQty = 1m,
            RowVersion = created.Data.RowVersion,
        };
        var result = await sut.UpdateAsync(update);

        Assert.False(result.Succeeded);
        Assert.Contains("cannot be changed", result.Message, StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("MC1", (await db.ProductionOutputs.SingleAsync()).PlannedMachineCode);
    }

    [Fact]
    public async Task Search_and_filter_options_use_daily_eligibility_and_include_internal_wip_materials()
    {
        var first = await SeedGraphAsync("WO-SEARCH-1", materialCode: "RM-1");
        var second = await SeedGraphAsync("WO-SEARCH-2", materialCode: "WIP-2", internalWip: true);
        await SeedGraphAsync("WO-COMPLETED", status: ProductionWorkOrderStatuses.Completed, materialCode: "RM-3");

        var sut = CreateService();
        var page = await sut.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery
        {
            RawMaterialCode = "WIP-2",
            ExactMatch = true,
            Take = 20,
        });
        var options = await sut.GetEligibleOperationFilterOptionsAsync();
        var firstPage = await sut.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery { Take = 1 });

        Assert.True(page.Succeeded, page.Message);
        Assert.Equal(second.OperationId, Assert.Single(page.Data!.Rows).WorkOrderOperationId);
        Assert.True(options.Succeeded, options.Message);
        Assert.Contains(options.Data!.RawMaterials, x => x.Code == "WIP-2");
        Assert.DoesNotContain(options.Data.RawMaterials, x => x.Code == "RM-3");
        Assert.True(firstPage.Succeeded, firstPage.Message);
        Assert.Equal(2, firstPage.Data!.TotalCount);
        Assert.Single(firstPage.Data.Rows);
        Assert.Contains(firstPage.Data.Rows[0].WorkOrderOperationId, new[] { first.OperationId, second.OperationId });
    }

    [Fact]
    public async Task Saved_document_workspace_remains_available_after_operation_is_no_longer_searchable()
    {
        var graph = await SeedGraphAsync("WO-STALE");
        var sut = CreateService();
        var created = await sut.CreateAsync(Request(graph.OperationId, Guid.NewGuid().ToString("N")));
        Assert.True(created.Succeeded, created.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var order = await db.ProductionWorkOrders.SingleAsync(x => x.Uid == graph.OrderId);
            order.Status = ProductionWorkOrderStatuses.Completed;
            await db.SaveChangesAsync();
        }

        var eligible = await sut.GetWorkspaceAsync(graph.OperationId);
        var saved = await sut.GetDocumentWorkspaceAsync(created.Data!.Uid);

        Assert.False(eligible.Succeeded);
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(graph.OperationId, saved.Data!.Operation.WorkOrderOperationId);
    }

    private ProductionOutputService CreateService(
        IAccessRightService? access = null,
        string company = "DEMO",
        string branch = "HQ") =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            access ?? AllowDailyAccessOnly().Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            new TestRunningNumberService());

    private static Mock<IAccessRightService> AllowDailyAccessOnly()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PlanningDailyProduction,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(
                It.Is<string>(menu => menu != MenuCodes.PlanningDailyProduction),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return access;
    }

    private static ProductionOutputCreateRequest Request(long operationId, string token) => new()
    {
        WorkOrderOperationId = operationId,
        PostingRequestId = token,
        ProductionDate = new DateTime(2026, 10, 1, 8, 0, 0),
        ShiftCode = "S1",
        ActualMachineCode = "MC1",
        OperatorCode = "OP1",
        GoodQty = 2m,
        OutputLotNo = "LOT-1",
    };

    private async Task<GraphIds> SeedGraphAsync(
        string workOrderNo,
        string materialCode = "RM-1",
        bool internalWip = false,
        string status = ProductionWorkOrderStatuses.Released)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = workOrderNo,
            ProductCode = $"FG-{workOrderNo}",
            ProductDescription = "Finished product",
            OutputUom = "EA",
            Status = status,
            PlannedQty = 10m,
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
        var route = new ProductionWorkOrderRouteStep
        {
            WorkOrder = order,
            WorkCentreCode = "WC10",
            OutputItemCode = "FG-OUT",
            OutputItemDescription = "Finished output",
            OutputType = "WIP_STOCKED",
            YieldPercent = 100m,
            OutputUom = "EA",
            OutputBaseUom = "EA",
            OutputConversionFactorToBase = 1m,
            RowVersion = [1],
        };
        var operation = new ProductionWorkOrderOperation
        {
            WorkOrder = order,
            RouteStep = route,
            OperationCode = "OP10",
            OperationDescription = "Production process",
            ProcessSequence = 1,
            ProcessType = "MACHINE",
            PlannedOutputQty = 10m,
            PlannedOutputUom = "EA",
            RowVersion = [1],
        };
        operation.Machines.Add(new ProductionWorkOrderMachine
        {
            MachineCode = "MC1",
            MachineDescription = "Line 1",
            IsSelected = true,
            IsDefault = true,
            Priority = 1,
        });
        route.Operations.Add(operation);
        order.RouteSteps.Add(route);
        order.Operations.Add(operation);

        // This fixture intentionally omits the Product Definition source graph. The production
        // service consumes the released Work Order snapshot only, so leave the optional source
        // foreign keys unenforced while seeding the isolated snapshot (as the existing production
        // allocation fixtures do).
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();

        var material = new ProductionWorkOrderMaterial
        {
            WorkOrder = order,
            WorkOrderOperation = operation,
            ComponentCode = materialCode,
            ComponentDescription = "Material " + materialCode,
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = internalWip ? PrMaterialSupplySources.InternalRouteWip : PrMaterialSupplySources.Purchased,
            RequiredQty = 2m,
            RequiredBaseQty = 2m,
            RequiredUom = "EA",
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            ProducingRouteStepId = internalWip ? route.Uid : null,
            RowVersion = [1],
        };
        db.ProductionWorkOrderMaterials.Add(material);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new GraphIds(order.Uid, operation.Uid);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct GraphIds(long OrderId, long OperationId);

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
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
