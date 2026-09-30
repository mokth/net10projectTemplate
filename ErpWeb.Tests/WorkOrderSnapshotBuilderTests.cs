using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Plan §7 — the snapshot builder must copy the authored definition faithfully, re-point the
/// references that cannot survive a copy, and refuse to invent anything the definition omits.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class WorkOrderSnapshotBuilderTests : IAsyncLifetime
{
    private static readonly Guid StepKeyWip = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StepKeyFinal = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OpKeyCut = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OpKeyFinish = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly WorkOrderSnapshotBuilder _builder;

    public WorkOrderSnapshotBuilderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        var uom = new Mock<IUomConversionService>();
        uom.Setup(x => x.ConvertAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, decimal qty, string _, string _, CancellationToken _) =>
                UomConversionResult.Ok(qty, IvQty.Scale));

        _builder = new WorkOrderSnapshotBuilder(
            _factory,
            new ProductDefinitionSnapshotLoader(_factory),
            new WorkOrderQuantityCalculator(uom.Object));
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "FG001",
                IDesc = "Finished Good",
                StdUom = "PCS",
                MfgType = PrMfgTypes.Make,
                DefWarehouse = "WH01",
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "WIP01",
                IDesc = "Cut WIP",
                StdUom = "PCS",
                MfgType = PrMfgTypes.Make,
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "RM001",
                IDesc = "Raw Material",
                StdUom = "KG",
                MfgType = PrMfgTypes.Buy,
                DefWarehouse = "WH02",
            });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private static WorkOrderSnapshotRequest Request() => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        LocationCode = "SITE",
        WorkOrderNo = "WO-0001",
        ProductCode = "FG001",
        PlannedQty = 100m,
        DefinitionEffectiveDate = new DateTime(2026, 7, 15),
        PlannedStartDateTime = new DateTime(2026, 7, 20, 8, 0, 0),
        PlannedCompletionDateTime = new DateTime(2026, 7, 24, 17, 0, 0),
    };

    /// <summary>
    /// Two route steps: a machine-driven cut producing WIP01, then a duration-based final step
    /// producing FG001 and consuming WIP01 from the first step.
    /// </summary>
    private static PrBomHdr Revision(bool withProducer = true, bool orphanMaterial = false)
    {
        var header = new PrBomHdr
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            Version = 3,
            Status = PrBomStatuses.Active,
            EffectiveFrom = new DateTime(2026, 1, 1),
            BaseQty = 5m,
            BaseUom = "PCS",
            RowVersion = [1],
        };

        var cutStep = new PrBomRouteStep
        {
            RouteStepKey = StepKeyWip,
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            StageSequence = 10,
            OutputItemCode = "WIP01",
            OutputType = PrRouteOutputTypes.WipStocked,
            StandardOutputQty = 1m,
            OutputUom = "PCS",
            YieldPercent = 100m,
            RowVersion = [1],
        };
        var finalStep = new PrBomRouteStep
        {
            RouteStepKey = StepKeyFinal,
            CompanyCode = "DEMO",
            WorkCentreCode = "WC02",
            StageSequence = 20,
            OutputItemCode = "FG001",
            OutputType = PrRouteOutputTypes.FinishedGoods,
            StandardOutputQty = 1m,
            OutputUom = "PCS",
            YieldPercent = 100m,
            RowVersion = [1],
        };

        var cutOperation = new PrBomOperation
        {
            OperationKey = OpKeyCut,
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            OutputItemCode = "WIP01",
            CentralSequence = 10,
            OperationCode = "CUT10",
            ProcessSequence = 10,
            ProcessType = PrProcessTypes.Machine,
            OutputBaseQty = 1m,
            OutputUom = "PCS",
            RowVersion = [1],
        };
        cutOperation.Machines.Add(new PrBomMachineOption
        {
            MachineCode = "MC02",
            Priority = 2,
            IsPrimary = false,
            CycleSeconds = 60m,
            OutputPerCycle = 10m,
            ParallelMachineCount = 2,
            RowVersion = [1],
        });
        cutOperation.Machines.Add(new PrBomMachineOption
        {
            MachineCode = "MC01",
            Priority = 1,
            IsPrimary = true,
            CycleSeconds = 45m,
            OutputPerCycle = 10m,
            ParallelMachineCount = 2,
            RowVersion = [1],
        });
        cutOperation.Machines.First(m => m.MachineCode == "MC01").Labours.Add(new PrBomLabourStandard
        {
            LabourCode = "OP1",
            LabourDescription = "Operator",
            CostPerOutputUnit = 3m,
            RowVersion = [1],
        });
        cutOperation.LabourRequirements.Add(new PrBomLabourRequirement
        {
            LabourCode = "QC",
            RequiredHeadcount = 1m,
            SetupMinutes = 5m,
            RunMinutes = 15m,
            CostRate = 5m,
            CostBasis = PrLabourCostBases.PerOutputUnit,
            RowVersion = [1],
        });

        var finishOperation = new PrBomOperation
        {
            OperationKey = OpKeyFinish,
            CompanyCode = "DEMO",
            WorkCentreCode = "WC02",
            OutputItemCode = "FG001",
            CentralSequence = 20,
            OperationCode = "FIN20",
            ProcessSequence = 10,
            ProcessType = PrProcessTypes.Manual,
            StandardDurationMinutes = 30m,
            IsFinalOperation = true,
            OutputBaseQty = 1m,
            OutputUom = "PCS",
            RowVersion = [1],
        };

        var wipMaterial = new PrDefBOM
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            ICode = "WIP01",
            IName = "Cut WIP",
            StdQty = 1m,
            StdUom = "PCS",
            SeqNo = 1,
            ScrapPercent = 0m,
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
            ProducingRouteStepId = withProducer ? 500 : null,
            RowVersion = [1],
        };
        var rawMaterial = new PrDefBOM
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            ICode = "RM001",
            IName = "Raw Material",
            StdQty = 2m,
            StdUom = "KG",
            SeqNo = 2,
            ScrapPercent = 10m,
            Tolerance = 0.5m,
            Warehouse = "WH03",
            IssueMethod = PrMaterialIssueMethods.Backflush,
            SupplySource = PrMaterialSupplySources.Purchased,
            RowVersion = [1],
        };

        // Authored identities are explicit because the persisted revision always has them: the
        // material's OperationID is the authoritative owner reference (plan §6.2).
        cutOperation.Uid = 1001;
        finishOperation.Uid = 1002;
        wipMaterial.OperationId = 1002;
        rawMaterial.OperationId = 1002;

        // The producer reference points at the cut step's own identity, which the snapshot must
        // re-point at the newly created route-step occurrence.
        cutStep.Uid = 500;
        finishOperation.Materials.Add(wipMaterial);
        finishOperation.Materials.Add(rawMaterial);
        finalStep.Operations.Add(finishOperation);
        cutStep.Operations.Add(cutOperation);

        header.RouteSteps.Add(cutStep);
        header.RouteSteps.Add(finalStep);
        header.Operations.Add(cutOperation);
        header.Operations.Add(finishOperation);
        header.Lines.Add(wipMaterial);
        header.Lines.Add(rawMaterial);

        if (orphanMaterial)
        {
            header.Lines.Add(new PrDefBOM
            {
                CompanyCode = "DEMO",
                ProdCode = "FG001",
                ICode = "RM009",
                StdQty = 1m,
                StdUom = "KG",
                SeqNo = 9,
                IssueMethod = PrMaterialIssueMethods.Manual,
                SupplySource = PrMaterialSupplySources.Purchased,
                RowVersion = [1],
            });
        }

        return header;
    }

    private async Task<PrBomHdr> PersistAsync(PrBomHdr header)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PrBomHdrs.Add(header);
        await db.SaveChangesAsync();
        return header;
    }

    [Fact]
    public async Task Builds_the_full_hierarchy_from_the_definition()
    {
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());

        Assert.True(result.Succeeded, result.FailureMessage);
        var workOrder = result.WorkOrder!;

        Assert.Equal(2, workOrder.RouteSteps.Count);
        Assert.Equal(10, workOrder.RouteSteps.First().StageSequence);
        Assert.Equal(20, workOrder.RouteSteps.Last().StageSequence);

        var cutOperation = workOrder.RouteSteps.First().Operations.Single();
        Assert.Equal("CUT10", cutOperation.OperationCode);
        Assert.Equal(1, cutOperation.SequenceNo);
        Assert.Equal(PrProcessTypes.Machine, cutOperation.ProcessType);
        Assert.Equal(ProductionCalendarSourceTypes.Machine, cutOperation.CalendarSourceType);

        var finishOperation = workOrder.RouteSteps.Last().Operations.Single();
        Assert.Equal(2, finishOperation.SequenceNo);
        Assert.Equal(PrProcessTypes.Manual, finishOperation.ProcessType);
        Assert.Equal(ProductionCalendarSourceTypes.PlantDefault, finishOperation.CalendarSourceType);
        Assert.Empty(finishOperation.Machines);
        Assert.True(finishOperation.IsFinalOperation);
        Assert.Equal(new[] { 1, 2 }, workOrder.Operations.Select(o => o.SequenceNo).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Copies_the_definition_identity_and_resolves_the_item_description()
    {
        var revision = await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());

        var workOrder = result.WorkOrder!;
        Assert.Equal(revision.Uid, workOrder.SourceBomHdrId);
        Assert.Equal(3, workOrder.SourceBomVersion);
        Assert.Equal(revision.Uid, workOrder.SourceProductDefinitionRevisionId);
        Assert.Equal(new DateTime(2026, 1, 1), workOrder.SourceEffectiveFrom);
        Assert.Equal(new DateTime(2026, 7, 15), workOrder.DefinitionEffectiveDate);
        Assert.Equal("Finished Good", workOrder.ProductDescription);
        Assert.Equal("PCS", workOrder.OutputUom);
        Assert.Equal(5m, workOrder.BomBaseQty);
        Assert.Equal(ProductionWorkOrderStatuses.Draft, workOrder.Status);
        Assert.Equal(ProductionSnapshotFormatVersions.Current, workOrder.SnapshotFormatVersion);
        Assert.False(workOrder.IsLegacySnapshot);
        Assert.Equal(ProductionSnapshotHashVersions.Current, workOrder.SnapshotHashVersion);
        Assert.Equal(ProductionDefinitionSourceHashVersions.Current, workOrder.DefinitionSourceHashVersion);
        Assert.Equal(64, workOrder.SnapshotHash.Length);
        Assert.Equal(64, workOrder.DefinitionSourceHash!.Length);
        Assert.Equal(result.DefinitionSourceHash, workOrder.DefinitionSourceHash);
    }

    [Fact]
    public async Task Computes_derived_quantities_from_the_route_basis()
    {
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());

        var workOrder = result.WorkOrder!;
        // 100 planned / 5 definition base x 1 route output base.
        Assert.Equal(20m, workOrder.RouteSteps.First().PlannedQty);
        Assert.Equal(20m, workOrder.RouteSteps.Last().PlannedQty);

        var cutOperation = workOrder.RouteSteps.First().Operations.Single();
        Assert.Equal(20m, cutOperation.PlannedOutputQty);

        // 20 x 2 / 5 x 1.10 scrap.
        var raw = workOrder.Materials.Single(m => m.ComponentCode == "RM001");
        Assert.Equal(8.8m, raw.RequiredQty);
        Assert.Equal("KG", raw.RequiredUom);
        Assert.Equal("KG", raw.BaseUom);
        Assert.Equal(1m, raw.ConversionFactorToBase);
        Assert.Equal("WH03", raw.WarehouseCode);
        Assert.Equal(PrMfgTypes.Buy, raw.MfgType);
        Assert.Equal(5m, raw.BomOutputQty);
        Assert.Equal("PCS", raw.BomOutputUom);

        var machine = cutOperation.Machines.Single(m => m.IsSelected);
        Assert.Equal("MC01", machine.MachineCode);
        Assert.Equal(2m, machine.PlannedCycleCount);
        Assert.Equal(1m, machine.PlannedCycleSlots);
        Assert.Equal(0.75m, machine.PlannedRunMinutes);
    }

    [Fact]
    public async Task Every_material_row_is_reachable_from_both_its_operation_and_the_header()
    {
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());

        var workOrder = result.WorkOrder!;
        var finishOperation = workOrder.RouteSteps.Last().Operations.Single();

        Assert.Equal(2, finishOperation.Materials.Count);
        Assert.Equal(2, workOrder.Materials.Count);
        foreach (var material in finishOperation.Materials)
        {
            Assert.Contains(material, workOrder.Materials);
            Assert.Same(finishOperation, material.WorkOrderOperation);
        }
    }

    [Fact]
    public async Task Snapshots_every_machine_alternative_but_selects_exactly_one()
    {
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());

        var cutOperation = result.WorkOrder!.RouteSteps.First().Operations.Single();
        Assert.Equal(2, cutOperation.Machines.Count);
        Assert.Single(cutOperation.Machines.Where(m => m.IsSelected));

        // The authored default wins over the lower priority number.
        Assert.Equal("MC01", cutOperation.Machines.Single(m => m.IsSelected).MachineCode);
        Assert.False(cutOperation.Machines.Single(m => m.MachineCode == "MC02").IsSelected);
        Assert.All(cutOperation.Machines, m =>
            Assert.Equal(ProductionMachineCycleQuantityModes.Discrete, m.CycleQuantityMode));
    }

    [Fact]
    public async Task Fixed_duration_processes_do_not_snapshot_machines()
    {
        var revision = Revision();
        var finish = revision.Operations.Single(o => o.OperationCode == "FIN20");
        finish.ProcessType = PrProcessTypes.Inspection;
        finish.Machines.Add(new PrBomMachineOption
        {
            MachineCode = "MC99",
            Priority = 1,
            IsPrimary = true,
            OutputPerCycle = 1m,
        });
        await PersistAsync(revision);

        var result = await _builder.BuildAsync(Request());

        Assert.True(result.Succeeded);
        var finishOperation = result.WorkOrder!.RouteSteps.Last().Operations.Single();
        Assert.Empty(finishOperation.Machines);
        Assert.Equal(ProductionCalendarSourceTypes.PlantDefault, finishOperation.CalendarSourceType);
        Assert.Contains(result.Warnings, w => w.Contains("MC99") || w.Contains("machine options"));
    }

    [Fact]
    public async Task Labour_ownership_follows_the_authored_source()
    {
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());

        var cutOperation = result.WorkOrder!.RouteSteps.First().Operations.Single();
        var selected = cutOperation.Machines.Single(m => m.IsSelected);

        // The standard hangs off the selected machine option.
        var operatorLabour = Assert.Single(selected.Labours);
        Assert.Equal("OP1", operatorLabour.LabourCode);
        Assert.Equal(ProductionLabourRateBases.PerOutputUnit, operatorLabour.RateBasis);
        Assert.True(operatorLabour.ContributesToPlan);
        Assert.Equal(60m, operatorLabour.PlannedAmount);

        // The free-standing requirement is operation-level.
        var qcLabour = Assert.Single(cutOperation.Labours);
        Assert.Equal("QC", qcLabour.LabourCode);
        Assert.Equal(1m, qcLabour.PlannedUnits);
        Assert.Equal(20m, qcLabour.PlannedMinutes);
        Assert.True(qcLabour.ContributesToPlan);
        Assert.Equal(100m, qcLabour.PlannedAmount);
    }

    [Fact]
    public async Task Machine_owned_labour_on_an_unselected_alternative_does_not_contribute()
    {
        var revision = Revision();
        var cut = revision.Operations.Single(o => o.OperationCode == "CUT10");
        cut.Machines.Single(m => m.MachineCode == "MC02").Labours.Add(new PrBomLabourStandard
        {
            LabourCode = "OP2",
            CostPerOutputUnit = 7m,
            RowVersion = [1],
        });
        await PersistAsync(revision);

        var result = await _builder.BuildAsync(Request());

        var cutOperation = result.WorkOrder!.RouteSteps.First().Operations.Single();
        var alternativeLabour = Assert.Single(
            cutOperation.Machines.Single(m => m.MachineCode == "MC02").Labours);
        Assert.False(alternativeLabour.ContributesToPlan);
        Assert.Equal(0m, alternativeLabour.PlannedAmount);
    }

    [Fact]
    public async Task Internal_wip_producer_is_repointed_at_the_new_route_step_occurrence()
    {
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());

        var workOrder = result.WorkOrder!;
        var wip = workOrder.Materials.Single(m => m.ComponentCode == "WIP01");
        var producer = workOrder.RouteSteps.First(s => s.OutputItemCode == "WIP01");

        Assert.Same(producer, wip.ProducingRouteStep);

        // The definition's own route-step id must not leak into the snapshot's foreign key: the
        // snapshot row has a different identity of its own.
        Assert.NotSame(wip.ProducingRouteStep, null);
        Assert.Equal(StepKeyWip, wip.ProducingRouteStep!.SourceRouteStepKey);
        Assert.Equal(PrMaterialSupplySources.InternalRouteWip, wip.SupplySource);
    }

    [Fact]
    public async Task Internal_wip_without_a_producer_blocks_the_build()
    {
        await PersistAsync(Revision(withProducer: false));

        var result = await _builder.BuildAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.WipProducerMissing, result.FailureCode);
    }

    [Fact]
    public async Task Material_without_consuming_operation_is_kept_for_the_readiness_gate()
    {
        await PersistAsync(Revision(orphanMaterial: true));

        var result = await _builder.BuildAsync(Request());

        Assert.True(result.Succeeded, result.FailureMessage);
        var orphan = result.WorkOrder!.Materials.Single(m => m.ComponentCode == "RM009");
        Assert.Null(orphan.WorkOrderOperation);
        Assert.DoesNotContain(orphan, result.WorkOrder.RouteSteps.SelectMany(s => s.Operations).SelectMany(o => o.Materials));
    }

    [Fact]
    public async Task Non_positive_planned_quantity_is_rejected_before_resolving()
    {
        var request = Request();
        request.PlannedQty = 0m;

        var result = await _builder.BuildAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.WorkOrderQtyInvalid, result.FailureCode);
    }

    [Fact]
    public async Task Missing_definition_revision_is_reported_with_the_loader_code()
    {
        var request = Request();
        request.ProductCode = "FG999";

        var result = await _builder.BuildAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Overlapping_revisions_refuse_to_build_rather_than_pick_one()
    {
        await PersistAsync(Revision());
        var second = Revision();
        second.Version = 4;
        second.RouteSteps.Clear();
        second.Operations.Clear();
        second.Lines.Clear();
        await PersistAsync(second);

        var result = await _builder.BuildAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionAmbiguous, result.FailureCode);
    }

    [Fact]
    public async Task Rebuilding_the_same_definition_produces_the_same_hashes()
    {
        await PersistAsync(Revision());

        var first = await _builder.BuildAsync(Request());
        var second = await _builder.BuildAsync(Request());

        Assert.Equal(first.WorkOrder!.SnapshotHash, second.WorkOrder!.SnapshotHash);
        Assert.Equal(first.DefinitionSourceHash, second.DefinitionSourceHash);
    }

    [Fact]
    public async Task Snapshot_hash_ignores_database_assigned_surrogate_keys()
    {
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(Request());
        var workOrder = result.WorkOrder!;
        var before = workOrder.SnapshotHash;

        // Simulate what a reload from the database does: the surrogate keys exist now.
        long next = 1;
        foreach (var step in workOrder.RouteSteps)
        {
            step.Uid = next++;
        }

        foreach (var operation in workOrder.RouteSteps.SelectMany(s => s.Operations))
        {
            operation.Uid = next++;
            foreach (var material in operation.Materials)
            {
                material.WorkOrderOperationId = operation.Uid;
                material.ProducingRouteStepId = workOrder.RouteSteps.First().Uid;
            }
        }

        Assert.Equal(before, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public async Task Change_to_the_definition_after_the_build_is_detectable()
    {
        await PersistAsync(Revision());
        var built = await _builder.BuildAsync(Request());

        // Refresh previews against a revision whose stored content changed after the snapshot was
        // taken, so the change has to be persisted rather than only mutated in memory.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.PrBomHdrs
                .SelectMany(h => h.Lines)
                .SingleAsync(l => l.ICode == "RM001");
            line.StdQty = 3m;
            await db.SaveChangesAsync();
        }

        var rebuilt = await _builder.BuildAsync(Request());

        Assert.NotEqual(built.DefinitionSourceHash, rebuilt.DefinitionSourceHash);
        Assert.NotEqual(built.WorkOrder!.SnapshotHash, rebuilt.WorkOrder!.SnapshotHash);
    }

    [Fact]
    public async Task Backward_direction_anchors_on_the_completion_date()
    {
        var request = Request();
        request.SchedulingDirection = ProductionSchedulingDirections.Backward;
        await PersistAsync(Revision());

        var result = await _builder.BuildAsync(request);

        Assert.Equal(
            request.PlannedCompletionDateTime,
            result.WorkOrder!.ScheduleAnchorDateTime);
    }
}
