using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Planning.Transaction;
/// <summary>
/// Plan §4.2 — the snapshot hash and the definition source hash must be reproducible tokens, not
/// incidental checksums: same content hashes the same, changed content hashes differently, and
/// volatile audit metadata never influences the result.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class WorkOrderSnapshotHasherTests
{
    private static readonly Guid RouteStepKeyA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RouteStepKeyB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OperationKeyA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OperationKeyB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static DateTime? AsLocal(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Local);

    private static ProductionWorkOrder Header() => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        ProductCode = "FG001",
        OutputUom = "PCS",
        SourceBomHdrId = 42,
        SourceBomVersion = 3,
        BomBaseQty = 5m,
        BomBaseUom = "PCS",
        PlannedQty = 100m,
        SnapshotHashVersion = ProductionSnapshotHashVersions.V1,
        DefinitionEffectiveDate = new DateTime(2026, 7, 1),
        SourceEffectiveFrom = new DateTime(2026, 1, 1),
        ScheduleAnchorDateTime = new DateTime(2026, 7, 6, 8, 0, 0),
        PlannedStartDateTime = new DateTime(2026, 7, 6, 8, 0, 0),
        PlannedCompletionDateTime = new DateTime(2026, 7, 7, 16, 30, 0),
        SchedulingDirection = ProductionSchedulingDirections.Forward,
        SourceType = ProductionSourceTypes.Manual,
    };

    private static ProductionWorkOrderOperation Operation(
        Guid key,
        int processSequence,
        decimal durationMinutes = 30m,
        bool isFinal = false) => new()
        {
            SourceOperationId = processSequence * 10L,
            SourceOperationKey = key,
            ProcessSequence = processSequence,
            ProcessType = PrProcessTypes.Machine,
            OperationCode = $"OP{processSequence:00}",
            WorkCentreCode = "WC01",
            IsFinalOperation = isFinal,
            StandardDurationMinutes = durationMinutes,
            PlannedInputQty = 100m,
            PlannedInputUom = "PCS",
            PlannedOutputQty = 98m,
            PlannedOutputUom = "PCS",
            CalendarSourceType = ProductionCalendarSourceTypes.Machine,
            CalendarSourceId = 7,
            CalendarSourceLastModified = new DateTime(2026, 6, 1, 9, 0, 0),
            ScheduleSourceHash = new string('A', 64),
            CalendarHorizonStart = new DateTime(2026, 1, 1),
            CalendarHorizonEnd = new DateTime(2026, 12, 31),
            PlannedStartDateTime = new DateTime(2026, 7, 6, 8, 0, 0),
            PlannedCompletionDateTime = new DateTime(2026, 7, 6, 12, 0, 0),
        };

    private static ProductionWorkOrderMachine Machine(string code = "MC01", bool selected = true) => new()
    {
        MachineCode = code,
        Priority = 1,
        IsDefault = selected,
        IsSelected = selected,
        ParallelMachineCount = 2,
        CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
        CycleSeconds = 45m,
        OutputPerCycle = 10m,
        OutputPerCycleUom = "PCS",
        ConversionSeconds = 120m,
        SetupSeconds = 600m,
        QueueSeconds = 60m,
        RequiredMachineOutputQty = 100m,
        RequiredMachineOutputUom = "PCS",
        PlannedCycleCount = 10m,
        PlannedCycleSlots = 5m,
        PlannedRunMinutes = 7.5m,
        MachineRatePerHour = 1200m,
        CalendarSourceId = 7,
        CalendarSourceLastModified = new DateTime(2026, 6, 1, 9, 0, 0),
        ScheduleSourceHash = new string('B', 64),
        CalendarHorizonStart = new DateTime(2026, 1, 1),
        CalendarHorizonEnd = new DateTime(2026, 12, 31),
        PlannedStartDateTime = new DateTime(2026, 7, 6, 8, 0, 0),
        PlannedCompletionDateTime = new DateTime(2026, 7, 6, 12, 0, 0),
    };

    private static ProductionWorkOrderMaterial Material(int sequence, string componentCode) => new()
    {
        MaterialSequence = sequence,
        LineNo = sequence,
        WorkOrderOperationId = 99,
        SourceOperationId = 10,
        SourceBomHdrId = 42,
        SourceBomVersion = 3,
        SourceMaterialKey = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        ComponentCode = componentCode,
        ComponentDescription = "component",
        MfgType = PrMfgTypes.Buy,
        ComponentQtyPerParent = 2m,
        StandardUom = "KG",
        BomOutputQty = 5m,
        BomOutputUom = "PCS",
        ScrapPercent = 5m,
        Tolerance = 0.25m,
        IssueMethod = PrMaterialIssueMethods.Manual,
        SupplySource = PrMaterialSupplySources.Purchased,
        RequiredQty = 42m,
        RequiredUom = "KG",
        RequiredBaseQty = 42m,
        BaseUom = "KG",
        ConversionFactorToBase = 1m,
        WarehouseCode = "WH01",
    };

    private static ProductionWorkOrder FullGraph()
    {
        var workOrder = Header();
        var stepA = new ProductionWorkOrderRouteStep
        {
            StageSequence = 10,
            SourceRouteStepId = 500,
            SourceRouteStepKey = RouteStepKeyA,
            WorkCentreCode = "WC01",
            OutputItemCode = "WIP01",
            OutputBaseQty = 1m,
            PlannedQty = 100m,
            OutputUom = "PCS",
            PlannedStartDateTime = new DateTime(2026, 7, 6, 8, 0, 0),
        };
        var stepB = new ProductionWorkOrderRouteStep
        {
            StageSequence = 20,
            SourceRouteStepId = 501,
            SourceRouteStepKey = RouteStepKeyB,
            WorkCentreCode = "WC02",
            OutputItemCode = "FG001",
            OutputBaseQty = 1m,
            PlannedQty = 100m,
            OutputUom = "PCS",
        };

        var opA = Operation(OperationKeyA, 10);
        opA.Machines.Add(Machine());
        opA.Labours.Add(new ProductionWorkOrderLabour
        {
            OperationId = opA.Uid,
            MachineId = null,
            LabourCode = "OPLAB",
            RateBasis = ProductionLabourRateBases.PerOutputUnit,
            Rate = 3m,
            ContributesToPlan = true,
            PlannedAmount = 300m,
        });

        var opB = Operation(OperationKeyB, 20, isFinal: true);
        opB.Materials.Add(Material(1, "RM001"));

        stepA.Operations.Add(opA);
        stepB.Operations.Add(opB);
        workOrder.RouteSteps.Add(stepA);
        workOrder.RouteSteps.Add(stepB);
        workOrder.Operations.Add(opA);
        workOrder.Operations.Add(opB);
        workOrder.Materials.Add(opB.Materials.First());
        return workOrder;
    }

    [Fact]
    public void Same_content_hashes_the_same()
    {
        var first = WorkOrderSnapshotHasher.ComputeSnapshotHash(FullGraph());
        var second = WorkOrderSnapshotHasher.ComputeSnapshotHash(FullGraph());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Local_and_unspecified_timestamps_hash_the_same()
    {
        var unspecified = FullGraph();
        var local = FullGraph();
        local.ScheduleAnchorDateTime = DateTime.SpecifyKind(local.ScheduleAnchorDateTime!.Value, DateTimeKind.Local);
        local.PlannedStartDateTime = DateTime.SpecifyKind(local.PlannedStartDateTime, DateTimeKind.Local);
        local.PlannedCompletionDateTime = DateTime.SpecifyKind(local.PlannedCompletionDateTime, DateTimeKind.Local);

        Assert.Equal(
            WorkOrderSnapshotHasher.ComputeSnapshotHash(unspecified),
            WorkOrderSnapshotHasher.ComputeSnapshotHash(local));
    }

    [Fact]
    public void Local_anchor_hash_saved_before_normalization_is_canonicalized()
    {
        var saved = FullGraph();
        saved.ScheduleAnchorDateTime = DateTime.SpecifyKind(saved.ScheduleAnchorDateTime!.Value, DateTimeKind.Local);
        var storedHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(saved, TimestampHashRules.AsStored);

        var reloaded = FullGraph();
        reloaded.SnapshotHash = storedHash;
        Assert.NotEqual(WorkOrderSnapshotHasher.ComputeSnapshotHash(reloaded), storedHash);
        Assert.True(WorkOrderSnapshotHasher.MatchesStoredSnapshotHash(reloaded));

        WorkOrderSnapshotHasher.CanonicalizeStoredTimestampKindHash(reloaded);

        Assert.Equal(WorkOrderSnapshotHasher.ComputeSnapshotHash(reloaded), reloaded.SnapshotHash);
    }

    [Fact]
    public void Local_planned_times_saved_before_normalization_are_canonicalized()
    {
        var saved = FullGraph();
        saved.ScheduleAnchorDateTime = DateTime.SpecifyKind(saved.ScheduleAnchorDateTime!.Value, DateTimeKind.Local);
        saved.PlannedStartDateTime = DateTime.SpecifyKind(saved.PlannedStartDateTime, DateTimeKind.Local);
        saved.PlannedCompletionDateTime = DateTime.SpecifyKind(saved.PlannedCompletionDateTime, DateTimeKind.Local);
        foreach (var step in saved.RouteSteps)
        {
            step.PlannedStartDateTime = AsLocal(step.PlannedStartDateTime);
            step.PlannedCompletionDateTime = AsLocal(step.PlannedCompletionDateTime);
            foreach (var operation in step.Operations)
            {
                operation.PlannedStartDateTime = AsLocal(operation.PlannedStartDateTime);
                operation.PlannedCompletionDateTime = AsLocal(operation.PlannedCompletionDateTime);
                foreach (var machine in operation.Machines)
                {
                    machine.PlannedStartDateTime = AsLocal(machine.PlannedStartDateTime);
                    machine.PlannedCompletionDateTime = AsLocal(machine.PlannedCompletionDateTime);
                }
            }
        }

        var storedHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(saved, TimestampHashRules.AsStored);
        var reloaded = FullGraph();
        reloaded.SnapshotHash = storedHash;

        Assert.True(WorkOrderSnapshotHasher.MatchesStoredSnapshotHash(reloaded));
        reloaded.PlannedQty += 1m;
        Assert.False(WorkOrderSnapshotHasher.MatchesStoredSnapshotHash(reloaded));
    }

    [Fact]
    public void Hash_is_independent_of_collection_insertion_order()
    {
        var ordered = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(ordered);

        var shuffled = FullGraph();
        shuffled.RouteSteps = shuffled.RouteSteps.Reverse().ToList();
        shuffled.Materials = shuffled.Materials.Reverse().ToList();

        Assert.Equal(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(shuffled));
    }

    [Fact]
    public void Work_order_number_does_not_change_the_hash()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.WorkOrderNo = "WO-99999";

        Assert.Equal(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void Decimal_scale_does_not_change_the_hash()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.PlannedQty = 100.0000m;

        Assert.Equal(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void DateTime_kind_and_hidden_time_do_not_leak_into_date_fields()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.DefinitionEffectiveDate = new DateTime(2026, 7, 1, 23, 59, 59, DateTimeKind.Utc);

        Assert.Equal(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void Plant_local_time_of_day_does_change_the_hash()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.PlannedStartDateTime = workOrder.PlannedStartDateTime.AddMinutes(30);

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void Planned_quantity_change_invalidates_the_snapshot()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.PlannedQty = 101m;

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void Selected_machine_change_invalidates_the_snapshot()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);
        var machine = workOrder.RouteSteps.First().Operations.First().Machines.First();

        machine.MachineCode = "MC02";

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void Derived_cycle_quantity_change_invalidates_the_snapshot()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.RouteSteps.First().Operations.First().Machines.First().PlannedCycleCount = 11m;

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void Material_uom_or_conversion_change_invalidates_the_snapshot()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.Materials.First().ConversionFactorToBase = 1000m;

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));

        var again = FullGraph();
        var second = WorkOrderSnapshotHasher.ComputeSnapshotHash(again);
        again.Materials.First().RequiredUom = "G";
        Assert.NotEqual(second, WorkOrderSnapshotHasher.ComputeSnapshotHash(again));
    }

    [Fact]
    public void Schedule_source_hash_change_invalidates_the_snapshot()
    {
        var workOrder = FullGraph();
        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        workOrder.RouteSteps.First().Operations.First().ScheduleSourceHash = new string('C', 64);

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder));
    }

    [Fact]
    public void Parallel_route_step_order_does_not_change_the_hash()
    {
        var workOrder = Header();
        var stepA = new ProductionWorkOrderRouteStep { StageSequence = 10, SourceRouteStepKey = RouteStepKeyA, WorkCentreCode = "WC01", OutputItemCode = "WIP01" };
        var stepB = new ProductionWorkOrderRouteStep { StageSequence = 10, SourceRouteStepKey = RouteStepKeyB, WorkCentreCode = "WC02", OutputItemCode = "WIP02" };
        stepA.Operations.Add(Operation(OperationKeyA, 10));
        stepB.Operations.Add(Operation(OperationKeyB, 10));
        workOrder.RouteSteps.Add(stepA);
        workOrder.RouteSteps.Add(stepB);

        var expected = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);

        // Equal stage sequences represent parallel steps, so the tie must be broken by the stable
        // authored key rather than by whichever row happened to be materialized first.
        var reversed = Header();
        reversed.RouteSteps.Add(stepB);
        reversed.RouteSteps.Add(stepA);

        Assert.Equal(expected, WorkOrderSnapshotHasher.ComputeSnapshotHash(reversed));
    }

    [Fact]
    public void Distinct_values_do_not_collide_when_concatenated()
    {
        var left = Header();
        left.ProductCode = "AB";
        left.OutputUom = "C";

        var right = Header();
        right.ProductCode = "A";
        right.OutputUom = "BC";

        Assert.NotEqual(
            WorkOrderSnapshotHasher.ComputeSnapshotHash(left),
            WorkOrderSnapshotHasher.ComputeSnapshotHash(right));
    }

    // ── Definition source hash ────────────────────────────────────────────────────────────────

    private static PrBomHdr Revision()
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
            Prefix = "WO",
        };
        var step = new PrBomRouteStep
        {
            BomHdrId = 42,
            RouteStepKey = RouteStepKeyA,
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            StageSequence = 10,
            OutputItemCode = "WIP01",
            OutputType = PrRouteOutputTypes.WipStocked,
            StandardOutputQty = 1m,
            OutputUom = "PCS",
            YieldPercent = 100m,
        };
        var operation = new PrBomOperation
        {
            BomHdrId = 42,
            RouteStepId = 500,
            OperationKey = OperationKeyA,
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            OutputItemCode = "WIP01",
            CentralSequence = 10,
            OperationCode = "OP10",
            ProcessSequence = 10,
            ProcessType = PrProcessTypes.Machine,
            StandardDurationMinutes = 30m,
            IsFinalOperation = true,
            OutputBaseQty = 1m,
            OutputUom = "PCS",
        };
        operation.Machines.Add(new PrBomMachineOption
        {
            OperationId = 99,
            MachineCode = "MC01",
            Priority = 1,
            IsPrimary = true,
            CycleSeconds = 45m,
            OutputPerCycle = 10m,
        });
        var material = new PrDefBOM
        {
            BomHdrId = 42,
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            ICode = "RM001",
            StdQty = 2m,
            StdUom = "KG",
            SeqNo = 1,
            ScrapPercent = 5m,
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased,
        };
        operation.Materials.Add(material);
        step.Operations.Add(operation);
        header.RouteSteps.Add(step);
        header.Operations.Add(operation);
        header.Lines.Add(material);
        return header;
    }

    [Fact]
    public void Definition_source_hash_is_stable_for_identical_payloads()
    {
        var first = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(Revision());
        var second = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(Revision());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Definition_source_hash_ignores_volatile_audit_metadata()
    {
        var expected = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(Revision());

        var mutated = Revision();
        mutated.CreatedDate = new DateTime(2026, 9, 1);
        mutated.CreatedBy = "someone";
        mutated.ModifiedDate = new DateTime(2026, 9, 2);
        mutated.ModifiedBy = "someone-else";
        mutated.RowVersion = [9, 9, 9];
        mutated.ValidationStatus = PrBomValidationStatuses.Valid;
        mutated.ValidatedDate = new DateTime(2026, 9, 3);
        mutated.Lines.First().RowVersion = [7, 7];
        mutated.Operations.First().RowVersion = [8, 8];

        Assert.Equal(expected, WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(mutated));
    }

    [Fact]
    public void Definition_source_hash_changes_when_a_manufacturing_field_changes()
    {
        var expected = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(Revision());

        var mutated = Revision();
        mutated.Lines.First().StdQty = 2.5m;

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(mutated));
    }

    [Fact]
    public void Definition_source_hash_changes_with_revision_identity_and_effective_bounds()
    {
        var expected = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(Revision());

        var versionBump = Revision();
        versionBump.Version = 4;
        var effectiveBump = Revision();
        effectiveBump.EffectiveFrom = new DateTime(2026, 2, 1);

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(versionBump));
        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(effectiveBump));
    }

    [Fact]
    public void Definition_source_hash_changes_when_the_producer_reference_changes()
    {
        var expected = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(Revision());

        var mutated = Revision();
        mutated.Lines.First().ProducingRouteStepId = 501;

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(mutated));
    }

    [Fact]
    public void Definition_source_hash_covers_the_machine_option_dimensions()
    {
        var expected = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(Revision());

        var mutated = Revision();
        mutated.Operations.First().Machines.First().OutputPerCycle = 12m;

        Assert.NotEqual(expected, WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(mutated));
    }

    [Fact]
    public void Definition_source_hash_covers_unowned_material_lines()
    {
        var header = Revision();
        var orphan = new PrDefBOM
        {
            BomHdrId = 42,
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            ICode = "RM002",
            StdQty = 1m,
            StdUom = "KG",
            SeqNo = 2,
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased,
        };
        header.Lines.Add(orphan);

        var withOrphan = WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(header);

        orphan.StdQty = 3m;

        Assert.NotEqual(withOrphan, WorkOrderSnapshotHasher.ComputeDefinitionSourceHashV1(header));
    }
}
