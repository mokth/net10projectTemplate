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

namespace ErpWeb.Tests.Production.Transaction;
[Trait(TestCategories.Name, TestCategories.Production)]
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

    [Fact]
    public async Task Sequence_gate_filters_search_and_blocks_direct_workspace_create_and_stale_post()
    {
        var graph = await SeedTwoStageSequenceGraphAsync("WO-SEQUENCE");
        var sut = CreateService();

        var initial = await sut.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery { Take = 20 });
        Assert.True(initial.Succeeded, initial.Message);
        Assert.Equal(1, initial.Data!.TotalCount);
        Assert.Equal(graph.FirstOperationId, Assert.Single(initial.Data.Rows).WorkOrderOperationId);

        var directWorkspace = await sut.GetWorkspaceAsync(graph.LaterOperationId);
        Assert.False(directWorkspace.Succeeded);
        Assert.Contains("Previous Stage 10", directWorkspace.Message, StringComparison.OrdinalIgnoreCase);

        var blockedCreate = await sut.CreateAsync(Request(graph.LaterOperationId, Guid.NewGuid().ToString("N")));
        Assert.False(blockedCreate.Succeeded);
        Assert.Contains("Previous Stage 10", blockedCreate.Message, StringComparison.OrdinalIgnoreCase);

        await SetOperationGoodAsync(graph.FirstOperationId, 10m);
        var opened = await sut.CreateAsync(Request(graph.LaterOperationId, Guid.NewGuid().ToString("N")));
        Assert.True(opened.Succeeded, opened.Message);

        // Simulate a predecessor correction after a valid draft was saved. PostAsync must make the
        // final sequence decision from the live execution projection, not the old workspace.
        await SetOperationGoodAsync(graph.FirstOperationId, 0m);
        var stalePost = await sut.PostAsync(opened.Data!.Uid);
        Assert.False(stalePost.Succeeded);
        Assert.Contains("Previous Stage 10", stalePost.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rollback_cannot_reopen_a_stage_when_later_stage_production_is_posted()
    {
        var graph = await SeedTwoStageSequenceGraphAsync("WO-SEQUENCE-ROLLBACK");
        var sut = CreateService();

        var firstRequest = Request(graph.FirstOperationId, Guid.NewGuid().ToString("N"));
        firstRequest.GoodQty = 10m;
        var first = await sut.CreateAsync(firstRequest);
        Assert.True(first.Succeeded, first.Message);
        var firstPost = await sut.PostAsync(first.Data!.Uid);
        Assert.True(firstPost.Succeeded, firstPost.Message);

        var later = await sut.CreateAsync(Request(graph.LaterOperationId, Guid.NewGuid().ToString("N")));
        Assert.True(later.Succeeded, later.Message);
        var laterPost = await sut.PostAsync(later.Data!.Uid);
        Assert.True(laterPost.Succeeded, laterPost.Message);

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = first.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "verify route integrity",
        });

        Assert.False(rollback.Succeeded);
        Assert.Contains("downstream Daily Production", rollback.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(later.Data.DocumentNo, rollback.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Same_route_step_partial_good_opens_next_process_and_handoff_caps_post()
    {
        var graph = await SeedTwoProcessSequenceGraphAsync("WO-PROCESS-OVERLAP");
        var sut = CreateService();

        var initial = await sut.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery
        {
            WorkOrderNo = "WO-PROCESS-OVERLAP",
            ExactMatch = true,
            Take = 20,
        });
        Assert.True(initial.Succeeded, initial.Message);
        Assert.Equal(graph.Process1Id, Assert.Single(initial.Data!.Rows).WorkOrderOperationId);

        var blockedWorkspace = await sut.GetWorkspaceAsync(graph.Process2Id);
        Assert.False(blockedWorkspace.Succeeded);
        Assert.Contains("must post Good qty first", blockedWorkspace.Message, StringComparison.OrdinalIgnoreCase);

        var p1Request = ProcessRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0));
        var p1 = await sut.CreateAsync(p1Request);
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await sut.PostAsync(p1.Data!.Uid)).Succeeded);

        var opened = await sut.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery
        {
            WorkOrderNo = "WO-PROCESS-OVERLAP",
            ExactMatch = true,
            Take = 20,
        });
        Assert.True(opened.Succeeded, opened.Message);
        Assert.Equal(2, opened.Data!.TotalCount);
        Assert.Contains(opened.Data.Rows, x => x.WorkOrderOperationId == graph.Process1Id);
        Assert.Contains(opened.Data.Rows, x => x.WorkOrderOperationId == graph.Process2Id);

        var workspace = await sut.GetWorkspaceAsync(graph.Process2Id);
        Assert.True(workspace.Succeeded, workspace.Message);
        var handoff = Assert.Single(workspace.Data!.Materials,
            x => x.SupplySource == ProductionProcessHandoff.SupplySource);
        Assert.Equal(40m, handoff.AvailableQty);

        var over = await sut.CreateAsync(ProcessRequest(graph.Process2Id, 41m, new DateTime(2026, 10, 1, 9, 0, 0)));
        Assert.True(over.Succeeded, over.Message);
        var overPost = await sut.PostAsync(over.Data!.Uid);
        Assert.False(overPost.Succeeded);
        Assert.Contains("Insufficient previous-process balance", overPost.Message, StringComparison.OrdinalIgnoreCase);

        var ok = await sut.CreateAsync(ProcessRequest(graph.Process2Id, 40m, new DateTime(2026, 10, 1, 9, 0, 0)));
        Assert.True(ok.Succeeded, ok.Message);
        Assert.True((await sut.PostAsync(ok.Data!.Uid)).Succeeded, ok.Message);

        var after = await sut.SearchEligibleOperationsAsync(new ProductionEligibleOperationQuery
        {
            WorkOrderNo = "WO-PROCESS-OVERLAP",
            ExactMatch = true,
            Take = 20,
        });
        Assert.True(after.Succeeded, after.Message);
        Assert.Contains(after.Data!.Rows, x => x.WorkOrderOperationId == graph.Process1Id);
    }

    [Fact]
    public async Task Stale_next_process_draft_is_rejected_after_predecessor_good_returns_to_zero()
    {
        var graph = await SeedTwoProcessSequenceGraphAsync("WO-PROCESS-STALE");
        var sut = CreateService();

        var p1 = await sut.CreateAsync(ProcessRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await sut.PostAsync(p1.Data!.Uid)).Succeeded);

        var p2 = await sut.CreateAsync(ProcessRequest(graph.Process2Id, 10m, new DateTime(2026, 10, 1, 9, 0, 0)));
        Assert.True(p2.Succeeded, p2.Message);

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "clear predecessor Good",
        });
        Assert.True(rollback.Succeeded, rollback.Message);

        var stalePost = await sut.PostAsync(p2.Data!.Uid);
        Assert.False(stalePost.Succeeded);
        Assert.Contains("must post Good qty first", stalePost.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_rollback_allows_unconsumed_increment_when_projected_good_stays_positive()
    {
        var graph = await SeedTwoProcessSequenceGraphAsync("WO-PROCESS-RB-OK");
        var sut = CreateService();

        var first = await sut.CreateAsync(ProcessRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(first.Succeeded, first.Message);
        Assert.True((await sut.PostAsync(first.Data!.Uid)).Succeeded);

        var extra = await sut.CreateAsync(ProcessRequest(graph.Process1Id, 10m, new DateTime(2026, 10, 1, 8, 30, 0)));
        Assert.True(extra.Succeeded, extra.Message);
        Assert.True((await sut.PostAsync(extra.Data!.Uid)).Succeeded);

        var p2 = await sut.CreateAsync(ProcessRequest(graph.Process2Id, 40m, new DateTime(2026, 10, 1, 9, 0, 0)));
        Assert.True(p2.Succeeded, p2.Message);
        Assert.True((await sut.PostAsync(p2.Data!.Uid)).Succeeded);

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = extra.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "unconsumed predecessor increment",
        });
        Assert.True(rollback.Succeeded, rollback.Message);
    }

    [Fact]
    public async Task Process_rollback_to_zero_good_is_blocked_when_later_process_is_posted()
    {
        var graph = await SeedTwoProcessSequenceGraphAsync("WO-PROCESS-RB-ZERO");
        var sut = CreateService();

        var p1 = await sut.CreateAsync(ProcessRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await sut.PostAsync(p1.Data!.Uid)).Succeeded);

        var p2 = await sut.CreateAsync(ProcessRequest(graph.Process2Id, 20m, new DateTime(2026, 10, 1, 9, 0, 0)));
        Assert.True(p2.Succeeded, p2.Message);
        Assert.True((await sut.PostAsync(p2.Data!.Uid)).Succeeded);

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "would clear predecessor Good",
        });
        Assert.False(rollback.Succeeded);
        Assert.Contains("downstream Daily Production", rollback.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(p2.Data.DocumentNo, rollback.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signed_lot_consumption_still_blocks_rollback_when_projected_good_stays_positive()
    {
        var graph = await SeedTwoProcessSequenceGraphAsync("WO-PROCESS-RB-LOT");
        var sut = CreateService();

        var first = await sut.CreateAsync(ProcessRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(first.Succeeded, first.Message);
        Assert.True((await sut.PostAsync(first.Data!.Uid)).Succeeded);

        var extra = await sut.CreateAsync(ProcessRequest(graph.Process1Id, 10m, new DateTime(2026, 10, 1, 8, 30, 0)));
        Assert.True(extra.Succeeded, extra.Message);
        Assert.True((await sut.PostAsync(extra.Data!.Uid)).Succeeded);

        var p2 = await sut.CreateAsync(ProcessRequest(graph.Process2Id, 45m, new DateTime(2026, 10, 1, 9, 0, 0)));
        Assert.True(p2.Succeeded, p2.Message);
        Assert.True((await sut.PostAsync(p2.Data!.Uid)).Succeeded);

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = first.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "later consumption of earlier produce",
        });
        Assert.False(rollback.Succeeded);
        Assert.Contains("later effective consumption", rollback.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("downstream Daily Production", rollback.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_production_consumption_and_rollback_preserve_exact_material_and_wip_costs()
    {
        var graph = await SeedGraphAsync("WO-COST");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.WorkOrderId == graph.OrderId);
            db.ProductionBalLots.Add(new ProductionBalLot
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                Kind = ProductionBalLotKinds.MaterialIn,
                ItemCode = material.ComponentCode,
                Description = material.ComponentDescription,
                Qty = 2m,
                Uom = "EA",
                BaseQty = 2m,
                BaseUom = "EA",
                ConversionFactorToBase = 1m,
                TotalCost = 10m,
                AverageUnitCost = 5m,
                WorkOrderId = graph.OrderId,
                WorkOrderNo = "WO-COST",
                WorkOrderMaterialId = material.Uid,
                WarehouseCode = "WH01",
                LocationCode = "BIN-A",
                LotNo = "RM-LOT",
                LastMovementDate = new DateTime(2026, 10, 1, 7, 0, 0),
                RowVersion = [1],
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateService();
        var first = await sut.CreateAsync(Request(graph.OperationId, Guid.NewGuid().ToString("N")));
        Assert.True(first.Succeeded, first.Message);
        Assert.True((await sut.PostAsync(first.Data!.Uid)).Succeeded);

        var secondRequest = Request(graph.OperationId, Guid.NewGuid().ToString("N"));
        secondRequest.GoodQty = 8m;
        secondRequest.ProductionDate = new DateTime(2026, 10, 1, 9, 0, 0);
        var second = await sut.CreateAsync(secondRequest);
        Assert.True(second.Succeeded, second.Message);
        Assert.True((await sut.PostAsync(second.Data!.Uid)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.MaterialIn);
            Assert.Equal(0m, source.BaseQty);
            Assert.Equal(0m, source.TotalCost);
            var consumes = await db.ProductionMaterialMovements
                .Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume)
                .OrderBy(x => x.Uid)
                .ToListAsync();
            Assert.Equal([2m, 8m], consumes.Select(x => x.TotalCost));
            var wip = await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.Wip);
            Assert.Equal(10m, wip.BaseQty);
            Assert.Equal(10m, wip.TotalCost);
        }

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = second.Data!.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "Verify cost restoration",
        });
        Assert.True(rollback.Succeeded, rollback.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.MaterialIn);
            Assert.Equal(1.6m, source.BaseQty);
            Assert.Equal(8m, source.TotalCost);
            var wip = await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.Wip);
            Assert.Equal(2m, wip.BaseQty);
            Assert.Equal(2m, wip.TotalCost);
        }
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

    private static ProductionOutputCreateRequest ProcessRequest(long operationId, decimal goodQty, DateTime date) => new()
    {
        WorkOrderOperationId = operationId,
        PostingRequestId = Guid.NewGuid().ToString("N"),
        ProductionDate = date,
        GoodQty = goodQty,
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
            StageSequence = 10,
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
            IsFinalOperation = true,
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

    private async Task<SequenceGraphIds> SeedTwoStageSequenceGraphAsync(string workOrderNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = workOrderNo,
            ProductCode = "FG-SEQUENCE",
            OutputUom = "EA",
            Status = ProductionWorkOrderStatuses.Released,
            PlannedQty = 10m,
            RemainingQty = 10m,
            SnapshotRevision = 1,
            SnapshotHash = new string('B', 64),
            SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
            IsLegacySnapshot = false,
            DefinitionEffectiveDate = new DateTime(2026, 10, 1),
            PlannedStartDateTime = new DateTime(2026, 10, 1),
            PlannedCompletionDateTime = new DateTime(2026, 10, 2),
            RowVersion = [1],
        };
        var firstRoute = SequenceRoute(order, 10, "WC-A");
        var laterRoute = SequenceRoute(order, 20, "WC-B");
        var first = SequenceOperation(order, firstRoute);
        var later = SequenceOperation(order, laterRoute);
        firstRoute.Operations.Add(first);
        laterRoute.Operations.Add(later);
        order.RouteSteps.Add(firstRoute);
        order.RouteSteps.Add(laterRoute);
        order.Operations.Add(first);
        order.Operations.Add(later);

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new SequenceGraphIds(first.Uid, later.Uid);
    }

    private async Task<TwoProcessGraphIds> SeedTwoProcessSequenceGraphAsync(string workOrderNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = workOrderNo,
            ProductCode = "FG-PROCESS",
            OutputUom = "EA",
            Status = ProductionWorkOrderStatuses.Released,
            PlannedQty = 100m,
            RemainingQty = 100m,
            SnapshotRevision = 1,
            SnapshotHash = new string('C', 64),
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
            StageSequence = 10,
            WorkCentreCode = "WC10",
            OutputItemCode = "WIP-OUT",
            OutputType = PrRouteOutputTypes.WipStocked,
            YieldPercent = 100m,
            OutputUom = "EA",
            OutputBaseUom = "EA",
            OutputConversionFactorToBase = 1m,
            RowVersion = [1],
        };
        var first = new ProductionWorkOrderOperation
        {
            WorkOrder = order,
            RouteStep = route,
            OperationCode = "P1",
            ProcessSequence = 10,
            ProcessType = "MANUAL",
            PlannedOutputQty = 100m,
            PlannedInputUom = "EA",
            PlannedOutputUom = "EA",
            IsFinalOperation = false,
            RemainingQty = 100m,
            RowVersion = [1],
        };
        var later = new ProductionWorkOrderOperation
        {
            WorkOrder = order,
            RouteStep = route,
            OperationCode = "P2",
            ProcessSequence = 20,
            ProcessType = "MANUAL",
            PlannedOutputQty = 100m,
            PlannedInputUom = "EA",
            PlannedOutputUom = "EA",
            IsFinalOperation = true,
            RemainingQty = 100m,
            RowVersion = [1],
        };
        route.Operations.Add(first);
        route.Operations.Add(later);
        order.RouteSteps.Add(route);
        order.Operations.Add(first);
        order.Operations.Add(later);

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new TwoProcessGraphIds(order.Uid, first.Uid, later.Uid);
    }

    private async Task SetOperationGoodAsync(long operationId, decimal goodQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var operation = await db.ProductionWorkOrderOperations.SingleAsync(x => x.Uid == operationId);
        operation.GoodQty = goodQty;
        operation.ProcessedQty = goodQty;
        operation.RemainingQty = IvQty.Round(Math.Max(operation.PlannedOutputQty - goodQty, 0m));
        await db.SaveChangesAsync();
    }

    private static ProductionWorkOrderRouteStep SequenceRoute(
        ProductionWorkOrder order, int stageSequence, string workCentreCode) => new()
    {
        WorkOrder = order,
        StageSequence = stageSequence,
        WorkCentreCode = workCentreCode,
        OutputItemCode = "WIP-SEQUENCE",
        OutputType = PrRouteOutputTypes.WipNonstock,
        YieldPercent = 100m,
        OutputUom = "EA",
        OutputBaseUom = "EA",
        OutputConversionFactorToBase = 1m,
        RowVersion = [1],
    };

    private static ProductionWorkOrderOperation SequenceOperation(
        ProductionWorkOrder order, ProductionWorkOrderRouteStep route) => new()
    {
        WorkOrder = order,
        RouteStep = route,
        OperationCode = "OP10",
        ProcessSequence = 10,
        ProcessType = "MANUAL",
        PlannedOutputQty = 10m,
        PlannedInputUom = "EA",
        PlannedOutputUom = "EA",
        IsFinalOperation = true,
        StandardDurationMinutes = 1m,
        RemainingQty = 10m,
        RowVersion = [1],
    };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct GraphIds(long OrderId, long OperationId);
    private readonly record struct SequenceGraphIds(long FirstOperationId, long LaterOperationId);
    private readonly record struct TwoProcessGraphIds(long OrderId, long Process1Id, long Process2Id);

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
