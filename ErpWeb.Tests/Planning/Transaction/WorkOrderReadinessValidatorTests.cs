using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests;

/// <summary>
/// Milestone 4 release-readiness gate (plan §9.1). Each test breaks exactly one rule on an
/// otherwise releasable snapshot so the reported code is unambiguous.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class WorkOrderReadinessValidatorTests
{
    private static WorkOrderReadinessValidator Sut() => new();

    private static WorkOrderReadinessContext Ready(bool currentFormat = true) =>
        new() { ReleaseEnabled = true, RequireCurrentSnapshotFormat = currentFormat };

    [Fact]
    public void A_complete_snapshot_is_ready()
    {
        var order = ValidOrder();

        var report = Sut().Validate(order, Ready());

        Assert.True(report.IsReady, report.Summary);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void Missing_route_step_blocks_release()
    {
        var order = ValidOrder();
        order.RouteSteps.Clear();

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.NoRoute);
    }

    [Fact]
    public void Route_step_without_operations_blocks_release()
    {
        var order = ValidOrder();
        order.RouteSteps.Single().Operations.Clear();

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.NoOperation);
    }

    [Fact]
    public void Missing_final_process_blocks_release()
    {
        var order = ValidOrder();
        order.RouteSteps.Single().Operations.Single().IsFinalOperation = false;

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.FinalProcessMissing);
    }

    [Fact]
    public void Ambiguous_final_process_blocks_release()
    {
        var order = ValidOrder();
        var routeStep = order.RouteSteps.Single();
        routeStep.Operations.Add(new ProductionWorkOrderOperation
        {
            WorkCentreCode = "WC01",
            OperationCode = "OP20",
            ProcessType = "MANUAL",
            ProcessSequence = 20,
            IsFinalOperation = true,
            StandardDurationMinutes = 5m
        });

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.FinalProcessAmbiguous);
    }

    [Fact]
    public void Final_process_that_is_not_last_blocks_release()
    {
        var order = ValidOrder();
        var routeStep = order.RouteSteps.Single();
        routeStep.Operations.Single().IsFinalOperation = true;
        routeStep.Operations.Add(new ProductionWorkOrderOperation
        {
            WorkCentreCode = "WC01",
            OperationCode = "OP20",
            ProcessType = "MANUAL",
            ProcessSequence = 20,
            IsFinalOperation = false,
            StandardDurationMinutes = 5m
        });

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.FinalProcessNotLast);
    }

    [Fact]
    public void Machine_process_without_a_machine_option_blocks_release()
    {
        var order = ValidOrder();
        order.RouteSteps.Single().Operations.Single().Machines.Clear();

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.MachineRequired);
    }

    [Fact]
    public void Machine_process_with_two_selected_options_blocks_release()
    {
        var order = ValidOrder();
        var operation = order.RouteSteps.Single().Operations.Single();
        operation.Machines.Add(new ProductionWorkOrderMachine
        {
            MachineCode = "MC02",
            IsSelected = true,
            CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
            OutputPerCycle = 1m,
            ParallelMachineCount = 1
        });

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.MachineRequired);
    }

    [Fact]
    public void Fixed_duration_process_without_a_standard_duration_blocks_release()
    {
        var order = ValidOrder();
        var operation = order.RouteSteps.Single().Operations.Single();
        operation.ProcessType = "MANUAL";
        operation.Machines.Clear();
        operation.StandardDurationMinutes = 0m;

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.OperationDurationRequired);
    }

    [Fact]
    public void Unknown_process_type_blocks_release()
    {
        var order = ValidOrder();
        order.RouteSteps.Single().Operations.Single().ProcessType = "TELEPORT";

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.ProcessTypeInvalid);
    }

    [Fact]
    public void Material_without_a_consuming_operation_blocks_release()
    {
        var order = ValidOrder();
        order.RouteSteps.Single().Operations.Single().Materials.Single().WorkOrderOperationId = null;

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.MaterialOperationMissing);
    }

    [Fact]
    public void Internal_route_wip_without_a_producer_blocks_release()
    {
        var order = ValidOrder();
        var material = order.RouteSteps.Single().Operations.Single().Materials.Single();
        material.SupplySource = "INTERNAL_ROUTE_WIP";
        material.ProducingRouteStepId = null;

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.WipProducerMissing);
    }

    [Fact]
    public void Unknown_issue_method_and_supply_source_block_release()
    {
        var order = ValidOrder();
        var material = order.RouteSteps.Single().Operations.Single().Materials.Single();
        material.IssueMethod = "GUESS";
        material.SupplySource = "WHATEVER";

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.IssueMethodInvalid);
        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.SupplySourceInvalid);
    }

    [Fact]
    public void Ambiguous_wip_producer_blocks_release()
    {
        var order = ValidOrder();
        var consumer = order.RouteSteps.Single();

        // Two route steps output the same WIP item.
        order.RouteSteps.Add(new ProductionWorkOrderRouteStep
        {
            Uid = 7,
            StageSequence = 5,
            WorkCentreCode = "WC00",
            OutputItemCode = "WIP01",
            OutputBaseQty = 1m,
            OutputUom = "PCS"
        });
        order.RouteSteps.Add(new ProductionWorkOrderRouteStep
        {
            Uid = 8,
            StageSequence = 5,
            WorkCentreCode = "WC00B",
            OutputItemCode = "WIP01",
            OutputBaseQty = 1m,
            OutputUom = "PCS"
        });

        var material = consumer.Operations.Single().Materials.Single();
        material.ComponentCode = "WIP01";
        material.SupplySource = "INTERNAL_ROUTE_WIP";
        material.ProducingRouteStepId = 7;

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.WipProducerAmbiguous);
    }

    [Fact]
    public void Consumer_sequenced_before_its_producer_blocks_release()
    {
        var order = ValidOrder();
        var consumer = order.RouteSteps.Single();
        consumer.StageSequence = 5;

        order.RouteSteps.Add(new ProductionWorkOrderRouteStep
        {
            Uid = 9,
            StageSequence = 40, // later than the consumer it feeds
            WorkCentreCode = "WC02",
            OutputItemCode = "WIP01",
            OutputBaseQty = 1m,
            OutputUom = "PCS"
        });

        var material = consumer.Operations.Single().Materials.Single();
        material.SupplySource = "INTERNAL_ROUTE_WIP";
        material.ProducingRouteStepId = 9;

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.SequenceDependencyConflict);
    }

    [Fact]
    public void A_wip_dependency_cycle_blocks_release()
    {
        // A consumes B, B consumes A.
        var order = ValidOrder();
        var a = order.RouteSteps.Single();
        a.Uid = 1;
        a.OutputItemCode = "ITEM_A";

        var b = new ProductionWorkOrderRouteStep
        {
            Uid = 2,
            StageSequence = 20,
            WorkCentreCode = "WC02",
            OutputItemCode = "ITEM_B",
            OutputBaseQty = 1m,
            OutputUom = "PCS"
        };
        b.Operations.Add(new ProductionWorkOrderOperation
        {
            WorkCentreCode = "WC02",
            OperationCode = "OP20",
            ProcessType = "MANUAL",
            ProcessSequence = 20,
            IsFinalOperation = true,
            StandardDurationMinutes = 5m
        });
        order.RouteSteps.Add(b);

        a.Operations.Single().Materials.Add(Material("ITEM_B", producingRouteStepId: 2));
        b.Operations.Single().Materials.Add(Material("ITEM_A", producingRouteStepId: 1));

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.DependencyCycle);
    }

    [Fact]
    public void Terminal_output_must_match_the_work_order_item()
    {
        var order = ValidOrder();
        order.RouteSteps.Single().OutputItemCode = "SOMETHING_ELSE";

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.FinalOutputMismatch);
    }

    [Fact]
    public void A_legacy_snapshot_cannot_be_released_without_a_refresh()
    {
        var order = ValidOrder();
        order.SnapshotFormatVersion = ProductionSnapshotFormatVersions.Legacy;
        order.IsLegacySnapshot = true;

        var report = Sut().Validate(order, Ready());

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired);
    }

    [Fact]
    public void A_stale_snapshot_hash_blocks_release()
    {
        var order = ValidOrder();
        order.SnapshotHash = new string('a', 64);

        var report = Sut().Validate(order, new WorkOrderReadinessContext
        {
            CurrentSnapshotHash = new string('b', 64)
        });

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.SnapshotHashInvalid);
    }

    [Fact]
    public void Release_is_refused_when_the_new_path_is_disabled()
    {
        var order = ValidOrder();

        var report = Sut().Validate(order, new WorkOrderReadinessContext { ReleaseEnabled = false });

        Assert.Contains(report.Errors, e => e.Code == ProductionReadinessErrorCodes.ReleaseDisabled);
    }

    [Fact]
    public void Machine_options_on_a_fixed_duration_process_are_a_warning_not_an_error()
    {
        var order = ValidOrder();
        var operation = order.RouteSteps.Single().Operations.Single();
        operation.ProcessType = "MANUAL";
        operation.StandardDurationMinutes = 10m;

        var report = Sut().Validate(order, Ready());

        Assert.True(report.IsReady, report.Summary);
        Assert.NotEmpty(report.Warnings);
    }

    // ── Builders ──────────────────────────────────────────────────────────────────────────────

    private static ProductionWorkOrder ValidOrder()
    {
        var routeStep = new ProductionWorkOrderRouteStep
        {
            Uid = 1,
            StageSequence = 10,
            WorkCentreCode = "WC01",
            OutputItemCode = "FG001",
            OutputBaseQty = 1m,
            OutputUom = "PCS",
            PlannedQty = 10m
        };

        var operation = new ProductionWorkOrderOperation
        {
            Uid = 100,
            WorkCentreCode = "WC01",
            OperationCode = "OP10",
            ProcessType = "MACHINE",
            ProcessSequence = 10,
            IsFinalOperation = true
        };

        operation.Machines.Add(new ProductionWorkOrderMachine
        {
            MachineCode = "MC01",
            IsDefault = true,
            IsSelected = true,
            CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
            OutputPerCycle = 5m,
            ParallelMachineCount = 1
        });

        var material = Material("RM001", producingRouteStepId: null);
        material.WorkOrderOperationId = 100;
        operation.Materials.Add(material);

        routeStep.Operations.Add(operation);

        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            ProductCode = "FG001",
            OutputUom = "PCS",
            PlannedQty = 10m,
            BomBaseQty = 1m,
            BomBaseUom = "PCS",
            SnapshotHash = new string('c', 64),
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
            IsLegacySnapshot = false
        };
        order.RouteSteps.Add(routeStep);
        return order;
    }

    private static ProductionWorkOrderMaterial Material(string componentCode, long? producingRouteStepId) => new()
    {
        LineNo = 1,
        MaterialSequence = 1,
        ComponentCode = componentCode,
        ComponentQtyPerParent = 1m,
        BomOutputQty = 1m,
        BomOutputUom = "PCS",
        RequiredUom = "PCS",
        BaseUom = "PCS",
        ConversionFactorToBase = 1m,
        MfgType = "BUY",
        IssueMethod = "MANUAL",
        SupplySource = producingRouteStepId is null ? "PURCHASED" : "INTERNAL_ROUTE_WIP",
        ProducingRouteStepId = producingRouteStepId
    };
}
