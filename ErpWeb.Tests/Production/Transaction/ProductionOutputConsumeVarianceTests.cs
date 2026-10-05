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
public sealed class ProductionOutputConsumeVarianceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionOutputConsumeVarianceTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.PrShifts.Add(new PrShift { CompCode = "DEMO", BranchCode = "HQ", ShiftCd = "S1", ShiftDes = "Day" });
        db.PrMachines.Add(new PrMachine { CompCode = "DEMO", BranchCode = "HQ", MachineCd = "MC1", ProcessCd = "OP10", MachineDes = "Line 1", Active = true });
        db.PrOperators.Add(new PrOperator { CompanyCode = "DEMO", BranchCode = "HQ", Code = "OP1", Name = "Active operator", Active = true });
        db.SaveChanges();
    }

    [Fact]
    public async Task Create_defaults_empty_materials_to_standard_and_replay_conflicts_on_consume_change()
    {
        var graph = await SeedGraphAsync("WO-REPLAY-MAT", requiredQty: 1000m, plannedQty: 1000m, tolerance: 10m);
        var sut = CreateService();
        var token = Guid.NewGuid().ToString("N");
        var first = await sut.CreateAsync(Request(graph.OperationId, token, goodQty: 100m));
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(100m, Assert.Single(first.Data!.Materials, x => !x.IsHandoff).StandardQty);
        Assert.Equal(100m, first.Data.Materials.Single(x => !x.IsHandoff).ConsumeQty);

        var same = await sut.CreateAsync(Request(graph.OperationId, token, goodQty: 100m));
        Assert.True(same.Succeeded, same.Message);
        Assert.Equal(first.Data.Uid, same.Data!.Uid);

        var changed = Request(graph.OperationId, token, goodQty: 100m);
        changed.Materials =
        [
            new ProductionOutputMaterialInput
            {
                WorkOrderMaterialId = graph.MaterialId,
                ConsumeQty = 105m,
                VarianceReasonCode = ProductionMaterialVarianceReasonCodes.Yield,
            }
        ];
        var conflict = await sut.CreateAsync(changed);
        Assert.False(conflict.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, conflict.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        var fact = await db.ProductionOutputMaterials.SingleAsync(x => !x.IsHandoff);
        Assert.Equal(100m, fact.ConsumeQty);
    }

    [Fact]
    public async Task Create_rejects_tampered_material_set_and_synthetic_handoff_id()
    {
        var graph = await SeedGraphAsync("WO-TAMPER", requiredQty: 100m, plannedQty: 100m);
        var sut = CreateService();

        var missing = Request(graph.OperationId, Guid.NewGuid().ToString("N"), 10m);
        missing.Materials = [new ProductionOutputMaterialInput { WorkOrderMaterialId = graph.MaterialId + 99, ConsumeQty = 10m }];
        Assert.Contains("must match", (await sut.CreateAsync(missing)).Message, StringComparison.OrdinalIgnoreCase);

        var synthetic = Request(graph.OperationId, Guid.NewGuid().ToString("N"), 10m);
        synthetic.Materials = [new ProductionOutputMaterialInput { WorkOrderMaterialId = -graph.OperationId, ConsumeQty = 10m }];
        Assert.Contains("Handoff", (await sut.CreateAsync(synthetic)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Draft_override_is_kept_and_new_document_deletes_children()
    {
        var graph = await SeedGraphAsync("WO-DRAFT", requiredQty: 1000m, plannedQty: 1000m, tolerance: 10m);
        var sut = CreateService();
        var created = await sut.CreateAsync(OverRequest(graph, 100m, 105m, ProductionMaterialVarianceReasonCodes.Damage));
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(5m, created.Data!.Materials.Single(x => !x.IsHandoff).VarianceQty);

        var updated = await sut.UpdateAsync(new ProductionOutputUpdateRequest
        {
            OutputId = created.Data.Uid,
            WorkOrderOperationId = graph.OperationId,
            PostingRequestId = created.Data.PostingRequestId,
            ProductionDate = created.Data.ProductionDate,
            GoodQty = 200m,
            OutputLotNo = "LOT-1",
            RowVersion = created.Data.RowVersion,
            Materials =
            [
                new ProductionOutputMaterialInput
                {
                    WorkOrderMaterialId = graph.MaterialId,
                    ConsumeQty = 105m,
                    VarianceReasonCode = ProductionMaterialVarianceReasonCodes.Damage,
                }
            ],
        });
        Assert.True(updated.Succeeded, updated.Message);
        var line = Assert.Single(updated.Data!.Materials, x => !x.IsHandoff);
        Assert.Equal(200m, line.StandardQty);
        Assert.Equal(105m, line.ConsumeQty);
        Assert.Equal(-95m, line.VarianceQty);

        Assert.True((await sut.DeleteAsync(updated.Data.Uid)).Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ProductionOutputMaterials.ToListAsync());
        Assert.Empty(await db.ProductionOutputs.ToListAsync());
    }

    [Fact]
    public async Task Later_document_cannot_exceed_its_own_max_to_catch_up_earlier_under()
    {
        var graph = await SeedGraphAsync("WO-CATCHUP", requiredQty: 1000m, plannedQty: 1000m, tolerance: 10m);
        await AddLotAsync(graph, qty: 400m, cost: 4000m);
        var sut = CreateService();

        var first = await sut.CreateAsync(OverRequest(graph, 100m, 90m, ProductionMaterialVarianceReasonCodes.Yield, "LOT-A"));
        Assert.True(first.Succeeded, first.Message);
        Assert.True((await sut.PostAsync(first.Data!.Uid)).Succeeded);

        var catchUp = OverRequest(graph, 100m, 120m, ProductionMaterialVarianceReasonCodes.Yield, "LOT-B");
        catchUp.ProductionDate = new DateTime(2026, 10, 1, 10, 0, 0);
        var blocked = await sut.CreateAsync(catchUp);
        Assert.False(blocked.Succeeded);
        Assert.Contains("maximum", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_uses_actual_consume_for_cost_and_standard_to_date_projection()
    {
        var graph = await SeedGraphAsync("WO-PROJ", requiredQty: 1000m, plannedQty: 1000m, tolerance: 10m);
        await AddLotAsync(graph, qty: 200m, cost: 2000m);
        var sut = CreateService();

        var created = await sut.CreateAsync(OverRequest(graph, 100m, 105m, ProductionMaterialVarianceReasonCodes.Yield));
        Assert.True(created.Succeeded, created.Message);
        var posted = await sut.PostAsync(created.Data!.Uid);
        Assert.True(posted.Succeeded, posted.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            Assert.Equal(105m, material.ConsumedQty);
            Assert.Equal(5m, material.VarianceQty);

            var consume = await db.ProductionMaterialMovements.SingleAsync(x =>
                x.MovementType == ProductionMaterialMovementTypes.Consume);
            Assert.Equal(105m, consume.Qty);
            Assert.Equal(1050m, consume.TotalCost);

            var wip = await db.ProductionBalLots.SingleAsync(x => x.Kind == ProductionBalLotKinds.Wip);
            Assert.Equal(1050m, wip.TotalCost);

            var fact = await db.ProductionOutputMaterials.SingleAsync(x => !x.IsHandoff);
            Assert.Equal(100m, fact.StandardQty);
            Assert.Equal(105m, fact.ConsumeQty);
            Assert.Null(fact.HandoffFromOperationId);
        }

        var replay = await sut.PostAsync(created.Data.Uid);
        Assert.True(replay.Succeeded, replay.Message);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            Assert.Equal(105m, material.ConsumedQty);
            Assert.Equal(5m, material.VarianceQty);
            Assert.Equal(1, await db.ProductionMaterialMovements.CountAsync(x =>
                x.MovementType == ProductionMaterialMovementTypes.Consume));
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            material.RequiredQty = 9999m;
            await db.SaveChangesAsync();
        }

        var historical = await sut.GetDocumentWorkspaceAsync(created.Data.Uid);
        Assert.True(historical.Succeeded, historical.Message);
        Assert.Equal(100m, historical.Data!.Materials.Single(x => !x.IsHandoff).StandardQty);
        Assert.Equal(105m, historical.Data.Materials.Single(x => !x.IsHandoff).ConsumeQty);
    }

    [Fact]
    public async Task Rollback_keeps_facts_and_excludes_them_from_standard_to_date()
    {
        var graph = await SeedGraphAsync("WO-RB", requiredQty: 1000m, plannedQty: 1000m, tolerance: 10m);
        await AddLotAsync(graph, qty: 200m, cost: 2000m);
        var sut = CreateService();
        var created = await sut.CreateAsync(OverRequest(graph, 100m, 105m, ProductionMaterialVarianceReasonCodes.Human));
        Assert.True((await sut.PostAsync(created.Data!.Uid)).Succeeded);

        var rollback = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = created.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "restore",
        });
        Assert.True(rollback.Succeeded, rollback.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var fact = await db.ProductionOutputMaterials.SingleAsync(x => !x.IsHandoff);
        Assert.Equal(105m, fact.ConsumeQty);
        Assert.Equal(ProductionOutputStatuses.Reversed, (await db.ProductionOutputs.SingleAsync()).Status);
        var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
        Assert.Equal(0m, material.ConsumedQty);
        Assert.Equal(0m, material.VarianceQty);

        var replay = await sut.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = created.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "replay",
        });
        Assert.True(replay.Succeeded, replay.Message);
        var afterReplay = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
        Assert.Equal(0m, afterReplay.ConsumedQty);
    }

    [Fact]
    public async Task Workspace_availability_uses_base_qty_as_of_production_date()
    {
        var graph = await SeedGraphAsync("WO-ASOF", requiredQty: 10m, plannedQty: 10m, conversion: 2m);
        await AddLotAsync(graph, qty: 4m, cost: 40m, baseQty: 8m, lastMovement: new DateTime(2026, 10, 5, 8, 0, 0));
        var sut = CreateService();

        var early = await sut.GetWorkspaceAsync(graph.OperationId, new DateTime(2026, 10, 3));
        Assert.True(early.Succeeded, early.Message);
        Assert.Equal(0m, early.Data!.Materials.Single(x => !x.IsHandoff).AvailableQty);

        var late = await sut.GetWorkspaceAsync(graph.OperationId, new DateTime(2026, 10, 5, 9, 0, 0));
        Assert.True(late.Succeeded, late.Message);
        Assert.Equal(4m, late.Data!.Materials.Single(x => !x.IsHandoff).AvailableQty);
    }

    [Fact]
    public async Task Unsupported_material_is_blocked_in_workspace_create_and_post()
    {
        var graph = await SeedGraphAsync("WO-BACKFLUSH", requiredQty: 10m, plannedQty: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            material.IssueMethod = PrMaterialIssueMethods.Backflush;
            await db.SaveChangesAsync();
        }

        var sut = CreateService();
        var workspace = await sut.GetWorkspaceAsync(graph.OperationId, new DateTime(2026, 10, 1));
        Assert.True(workspace.Succeeded, workspace.Message);
        Assert.Contains("not supported", workspace.Data!.Materials.Single().BlockingReason, StringComparison.OrdinalIgnoreCase);

        var created = await sut.CreateAsync(Request(graph.OperationId, Guid.NewGuid().ToString("N"), 10m));
        Assert.False(created.Succeeded);
        Assert.Contains("not supported", created.Message, StringComparison.OrdinalIgnoreCase);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            material.IssueMethod = PrMaterialIssueMethods.Manual;
            await db.SaveChangesAsync();
        }

        var draft = await sut.CreateAsync(Request(graph.OperationId, Guid.NewGuid().ToString("N"), 10m));
        Assert.True(draft.Succeeded, draft.Message);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            material.IssueMethod = PrMaterialIssueMethods.Backflush;
            await db.SaveChangesAsync();
        }

        var posted = await sut.PostAsync(draft.Data!.Uid);
        Assert.False(posted.Succeeded);
        Assert.Contains("not supported", posted.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inquiry_is_uom_safe_and_tenant_scoped()
    {
        var graph = await SeedGraphAsync("WO-INQ", requiredQty: 10m, plannedQty: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var output = await CreatePostedHeaderAsync(db, graph, "DP-INQ");
            db.ProductionOutputMaterials.AddRange(
                Fact(output, graph.MaterialId, "RESIN", "KG", 10m, 12m, ProductionMaterialVarianceReasonCodes.Damage),
                Fact(output, graph.MaterialId, "BAG", "PCS", 5m, 4m, ProductionMaterialVarianceReasonCodes.Human),
                Fact(output, graph.MaterialId, "OIL", "L", 2m, 3m, ProductionMaterialVarianceReasonCodes.Yield));
            await db.SaveChangesAsync();
        }

        var inquiry = CreateInquiry();
        var detail = await inquiry.SearchDetailAsync(new ProductionMaterialConsumeVarianceQuery());
        Assert.True(detail.Succeeded, detail.Message);
        Assert.Equal(3, detail.Data!.TotalCount);

        var summaries = await inquiry.SearchSummariesAsync(new ProductionMaterialConsumeVarianceQuery());
        Assert.True(summaries.Succeeded, summaries.Message);
        Assert.Equal(3, summaries.Data!.ByComponent.Count);
        Assert.Equal(["KG", "L", "PCS"], summaries.Data.ByComponent.Select(x => x.RequiredUom).OrderBy(x => x));
        Assert.Equal(2m, summaries.Data.ByComponent.Single(x => x.RequiredUom == "KG").TotalVarianceQty);
        Assert.Equal(-1m, summaries.Data.ByComponent.Single(x => x.RequiredUom == "PCS").TotalVarianceQty);
        Assert.Equal(1m, summaries.Data.ByComponent.Single(x => x.RequiredUom == "L").TotalVarianceQty);
        Assert.All(summaries.Data.ByReason, x => Assert.True(x.DocumentLineCount > 0));

        var hidden = CreateInquiry(branch: "BR2");
        var other = await hidden.SearchDetailAsync(new ProductionMaterialConsumeVarianceQuery());
        Assert.True(other.Succeeded, other.Message);
        Assert.Equal(0, other.Data!.TotalCount);
    }

    [Fact]
    public async Task Path_b_backfill_uses_original_consume_not_effective_zero()
    {
        var graph = await SeedGraphAsync("WO-LEGACY", requiredQty: 1000m, plannedQty: 1000m, tolerance: 10m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var output = await CreatePostedHeaderAsync(db, graph, "DP-LEGACY");
            output.Status = ProductionOutputStatuses.Reversed;
            output.GoodQty = 100m;
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.ProductionMaterialMovements.AddRange(
                Movement(graph, output.Uid, ProductionMaterialMovementTypes.Consume, 105m),
                Movement(graph, output.Uid, ProductionMaterialMovementTypes.ConsumeReversal, 105m));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var count = await ProductionOutputMaterialLegacyBackfill.BackfillMissingFactsAsync(
                db, new DateTime(2026, 10, 5), "SEED");
            Assert.Equal(1, count);
            var fact = await db.ProductionOutputMaterials.SingleAsync();
            Assert.Equal(105m, fact.ConsumeQty);
            Assert.Equal(100m, fact.StandardQty);
            Assert.Equal(5m, fact.VarianceQty);
            Assert.Equal(ProductionMaterialVarianceReasonCodes.LegacyUnclassified, fact.VarianceReasonCode);
            Assert.False(fact.IsHandoff);
        }
    }

    [Fact]
    public async Task Tenant_fk_rejects_child_with_foreign_company()
    {
        var graph = await SeedGraphAsync("WO-FK", requiredQty: 10m, plannedQty: 10m);
        var sut = CreateService();
        var created = await sut.CreateAsync(Request(graph.OperationId, Guid.NewGuid().ToString("N"), 1m));
        Assert.True(created.Succeeded, created.Message);

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        db.ProductionOutputMaterials.Add(new ProductionOutputMaterial
        {
            CompanyCode = "ZZ",
            BranchCode = "HQ",
            ProductionOutputId = created.Data!.Uid,
            WorkOrderMaterialId = graph.MaterialId,
            ComponentCode = "X",
            RequiredUom = "EA",
            SupplySource = PrMaterialSupplySources.Purchased,
            IssueMethod = PrMaterialIssueMethods.Manual,
            ConversionFactorToBase = 1m,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "TEST",
            RowVersion = [1],
        });
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }

    private ProductionOutputCreateRequest OverRequest(
        GraphIds graph, decimal goodQty, decimal consume, string reason, string lot = "LOT-1")
    {
        var request = Request(graph.OperationId, Guid.NewGuid().ToString("N"), goodQty, lot);
        request.Materials =
        [
            new ProductionOutputMaterialInput
            {
                WorkOrderMaterialId = graph.MaterialId,
                ConsumeQty = consume,
                VarianceReasonCode = reason,
            }
        ];
        return request;
    }

    private ProductionOutputService CreateService(string company = "DEMO", string branch = "HQ") =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch),
            Allow(MenuCodes.PlanningDailyProduction),
            new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            new TestRunningNumberService());

    private ProductionMaterialConsumeVarianceInquiryService CreateInquiry(string branch = "HQ") =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(branch: branch),
            Allow(MenuCodes.PlanningMaterialConsumeVariance));

    private static IAccessRightService Allow(string menu)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(menu, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access.Object;
    }

    private static ProductionOutputCreateRequest Request(
        long operationId, string token, decimal goodQty = 2m, string lot = "LOT-1") => new()
    {
        WorkOrderOperationId = operationId,
        PostingRequestId = token,
        ProductionDate = new DateTime(2026, 10, 1, 8, 0, 0),
        ShiftCode = "S1",
        ActualMachineCode = "MC1",
        OperatorCode = "OP1",
        GoodQty = goodQty,
        OutputLotNo = lot,
    };

    private async Task AddLotAsync(
        GraphIds graph, decimal qty, decimal cost, decimal? baseQty = null, DateTime? lastMovement = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
        var lotBaseQty = baseQty ?? qty;
        db.ProductionBalLots.Add(new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.MaterialIn,
            ItemCode = material.ComponentCode,
            Qty = qty,
            Uom = material.RequiredUom ?? "EA",
            BaseQty = lotBaseQty,
            BaseUom = material.BaseUom ?? "EA",
            ConversionFactorToBase = material.ConversionFactorToBase,
            TotalCost = cost,
            AverageUnitCost = qty == 0m ? 0m : cost / qty,
            WorkOrderId = graph.OrderId,
            WorkOrderNo = "WO",
            WorkOrderMaterialId = material.Uid,
            WarehouseCode = "WH01",
            LocationCode = "BIN-A",
            LotNo = "RM-LOT",
            LastMovementDate = lastMovement ?? new DateTime(2026, 10, 1, 7, 0, 0),
            RowVersion = [1],
        });
        await db.SaveChangesAsync();
    }

    private async Task<ProductionOutput> CreatePostedHeaderAsync(AppDbContext db, GraphIds graph, string documentNo)
    {
        var output = new ProductionOutput
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DocumentNo = documentNo,
            Status = ProductionOutputStatuses.Posted,
            WorkOrderId = graph.OrderId,
            RouteStepId = graph.RouteStepId,
            WorkOrderOperationId = graph.OperationId,
            ProductionDate = new DateTime(2026, 10, 1, 8, 0, 0),
            GoodQty = 100m,
            OutputUom = "EA",
            OutputItemCode = "FG-OUT",
            OutputLotNo = "LOT-X",
            SnapshotHash = new string('A', 64),
            PostingRequestId = Guid.NewGuid().ToString("N"),
            PostedBy = "admin",
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "TEST",
            RowVersion = [1],
        };
        db.ProductionOutputs.Add(output);
        await db.SaveChangesAsync();
        return output;
    }

    private static ProductionOutputMaterial Fact(
        ProductionOutput output,
        long workOrderMaterialId,
        string component,
        string uom,
        decimal standard,
        decimal consume,
        string reason) => new()
    {
        CompanyCode = output.CompanyCode,
        BranchCode = output.BranchCode,
        ProductionOutputId = output.Uid,
        WorkOrderMaterialId = workOrderMaterialId,
        ComponentCode = component,
        RequiredUom = uom,
        SupplySource = PrMaterialSupplySources.Purchased,
        IssueMethod = PrMaterialIssueMethods.Manual,
        ConversionFactorToBase = 1m,
        StandardQty = standard,
        ConsumeQty = consume,
        VarianceQty = consume - standard,
        VarianceReasonCode = reason,
        CreatedDate = DateTime.UtcNow,
        CreatedBy = "TEST",
        RowVersion = [1],
    };

    private static ProductionMaterialMovement Movement(
        GraphIds graph, long outputId, string type, decimal qty) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        WorkOrderId = graph.OrderId,
        WorkOrderMaterialId = graph.MaterialId,
        WorkOrderOperationId = graph.OperationId,
        MovementType = type,
        MovementDate = new DateTime(2026, 10, 1),
        ItemCode = "RM-1",
        Qty = qty,
        Uom = "EA",
        BaseQty = qty,
        BaseUom = "EA",
        ConversionFactorToBase = 1m,
        WarehouseCode = "WH01",
        LocationCode = "BIN-A",
        LotNo = "RM-LOT",
        ProductionOutputId = outputId,
        CreatedDate = DateTime.UtcNow,
        CreatedBy = "TEST",
    };

    private async Task<GraphIds> SeedGraphAsync(
        string workOrderNo,
        decimal requiredQty = 2m,
        decimal plannedQty = 10m,
        decimal tolerance = 0m,
        decimal conversion = 1m)
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
            Status = ProductionWorkOrderStatuses.Released,
            PlannedQty = plannedQty,
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
            PlannedOutputQty = plannedQty,
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

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();

        var material = new ProductionWorkOrderMaterial
        {
            WorkOrder = order,
            WorkOrderOperation = operation,
            ComponentCode = "RM-1",
            ComponentDescription = "Resin",
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased,
            RequiredQty = requiredQty,
            RequiredBaseQty = requiredQty * conversion,
            RequiredUom = "EA",
            BaseUom = "EA",
            ConversionFactorToBase = conversion,
            Tolerance = tolerance,
            RowVersion = [1],
        };
        db.ProductionWorkOrderMaterials.Add(material);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new GraphIds(order.Uid, operation.Uid, route.Uid, material.Uid);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct GraphIds(long OrderId, long OperationId, long RouteStepId, long MaterialId);

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
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
