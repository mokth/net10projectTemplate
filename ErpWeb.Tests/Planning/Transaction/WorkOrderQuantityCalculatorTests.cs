using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Planning.Transaction;
/// <summary>
/// Milestone 2 quantity contract (plan §7.3). These are pure calculation tests: the snapshot graph
/// is built in memory and no database is involved.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class WorkOrderQuantityCalculatorTests
{
    private static WorkOrderQuantityCalculator CreateSut(FakeUomConversionService uom) => new(uom);

    [Fact]
    public async Task Route_planned_qty_scales_the_work_order_quantity_by_the_route_base()
    {
        var uom = new FakeUomConversionService();
        var sut = CreateSut(uom);

        Assert.Equal(10m, sut.RoutePlannedQty(10m, 1m, 1m));
        Assert.Equal(25m, sut.RoutePlannedQty(10m, 2m, 5m));
        Assert.Equal(3.3333m, sut.RoutePlannedQty(10m, 3m, 1m));
    }

    [Fact]
    public async Task Material_requirement_applies_scrap_and_never_tolerance()
    {
        var uom = new FakeUomConversionService()
            .With("RM001", "PCS", "KG", 0.5m);
        var order = BuildOrder();
        var material = AddMaterial(order, requiredUom: "KG", baseUom: "KG", scrapPercent: 10m, tolerance: 5m);
        material.RequiredQty = 0m;

        var result = await CreateSut(uom).CalculateAsync(order);

        Assert.True(result.Succeeded, result.Summary);
        // basis 10 PCS, standard 2 per 1 PCS output, +10% scrap = 22 KG. Tolerance is not added.
        Assert.Equal(22m, material.RequiredQty);
        Assert.Equal(22m, material.RequiredBaseQty);
        Assert.Equal(1m, material.ConversionFactorToBase);
    }

    [Fact]
    public async Task Material_requirement_converts_the_operation_output_into_the_bom_denominator_uom()
    {
        // The operation outputs PCS but the BOM denominator is authored in KG, so the basis has to
        // be converted before the ratio is applied.
        var uom = new FakeUomConversionService()
            .With("RM001", "PCS", "KG", 0.5m);
        var order = BuildOrder();
        var material = AddMaterial(order, requiredUom: "KG", baseUom: "KG", scrapPercent: 0m, tolerance: 0m);
        material.BomOutputUom = "KG";
        material.BomOutputQty = 2m;

        var result = await CreateSut(uom).CalculateAsync(order);

        Assert.True(result.Succeeded, result.Summary);
        // 10 PCS -> 5 KG basis; 5 * 2 / 2 = 5 KG.
        Assert.Equal(5m, material.RequiredQty);
    }

    [Fact]
    public async Task Missing_conversion_is_blocking_and_reported()
    {
        var uom = new FakeUomConversionService();
        var order = BuildOrder();
        var material = AddMaterial(order, requiredUom: "KG", baseUom: "KG", scrapPercent: 0m, tolerance: 0m);
        material.BomOutputUom = "KG";

        var result = await CreateSut(uom).CalculateAsync(order);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == ProductionReadinessErrorCodes.UomConversionMissing);
        Assert.Equal(0m, material.RequiredQty);
    }

    [Fact]
    public async Task Zero_product_definition_base_quantity_is_blocking()
    {
        var order = BuildOrder();
        order.BomBaseQty = 0m;

        var result = await CreateSut(new FakeUomConversionService()).CalculateAsync(order);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionBaseQtyInvalid, error.Code);
    }

    [Fact]
    public async Task Machine_cycles_and_parallel_slots_both_round_up()
    {
        var order = BuildOrder();
        var machine = AddMachine(order, outputPerCycle: 4m, parallelCount: 3, cycleSeconds: 120m);

        var result = await CreateSut(new FakeUomConversionService()).CalculateAsync(order);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(10m, machine.RequiredMachineOutputQty);
        Assert.Equal(3m, machine.PlannedCycleCount);   // Ceiling(10 / 4)
        Assert.Equal(1m, machine.PlannedCycleSlots);   // Ceiling(3 / 3)
        Assert.Equal(2m, machine.PlannedRunMinutes);   // 1 slot * 120s / 60
    }

    [Fact]
    public async Task Machine_cycle_slots_round_up_when_capacity_does_not_divide()
    {
        var order = BuildOrder();
        var machine = AddMachine(order, outputPerCycle: 3m, parallelCount: 2, cycleSeconds: 60m);

        await CreateSut(new FakeUomConversionService()).CalculateAsync(order);

        Assert.Equal(4m, machine.PlannedCycleCount);   // Ceiling(10 / 3)
        Assert.Equal(2m, machine.PlannedCycleSlots);   // Ceiling(4 / 2)
    }

    [Fact]
    public async Task Zero_output_per_cycle_is_blocking()
    {
        var order = BuildOrder();
        AddMachine(order, outputPerCycle: 0m, parallelCount: 1, cycleSeconds: 60m);

        var result = await CreateSut(new FakeUomConversionService()).CalculateAsync(order);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == ProductionReadinessErrorCodes.OutputPerCycleMissing);
    }

    [Fact]
    public async Task Labour_amount_uses_the_consuming_operation_output_and_ignores_non_contributing_rows()
    {
        var order = BuildOrder();
        AddMachine(order, outputPerCycle: 5m, parallelCount: 1, cycleSeconds: 60m);
        var contributing = AddLabour(order, machineOwned: false, rate: 1.5m, contributes: true);
        var informational = AddLabour(order, machineOwned: true, rate: 4m, contributes: false);

        var result = await CreateSut(new FakeUomConversionService()).CalculateAsync(order);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(15m, contributing.PlannedAmount);
        Assert.Equal(0m, informational.PlannedAmount);
    }

    [Fact]
    public async Task Parallel_processes_each_receive_the_full_route_quantity()
    {
        var order = BuildOrder();
        var second = new ProductionWorkOrderOperation
        {
            OperationCode = "OP20",
            ProcessSequence = 10, // same sequence as OP10 = parallel
            ProcessType = "MANUAL",
            PlannedInputUom = "PCS",
            PlannedOutputUom = "PCS"
        };
        order.RouteSteps.Single().Operations.Add(second);

        var result = await CreateSut(new FakeUomConversionService()).CalculateAsync(order);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(10m, second.PlannedInputQty);
        Assert.Equal(10m, second.PlannedOutputQty);
    }

    [Fact]
    public async Task Queries_at_high_precision_and_stores_four_decimals_away_from_zero()
    {
        var order = BuildOrder();
        order.PlannedQty = 1m;
        order.BomBaseQty = 3m;
        order.RouteSteps.Single().OutputBaseQty = 1m;

        await CreateSut(new FakeUomConversionService()).CalculateAsync(order);

        Assert.Equal(0.3333m, order.RouteSteps.Single().PlannedQty);
    }

    // ── Graph builders ────────────────────────────────────────────────────────────────────────

    private static ProductionWorkOrder BuildOrder()
    {
        var routeStep = new ProductionWorkOrderRouteStep
        {
            StageSequence = 10,
            WorkCentreCode = "WC01",
            OutputItemCode = "FG001",
            OutputBaseQty = 1m,
            OutputUom = "PCS"
        };

        var operation = new ProductionWorkOrderOperation
        {
            OperationCode = "OP10",
            ProcessType = "MACHINE",
            ProcessSequence = 10,
            PlannedInputUom = "PCS",
            PlannedOutputUom = "PCS"
        };

        routeStep.Operations.Add(operation);

        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            ProductCode = "FG001",
            OutputUom = "PCS",
            PlannedQty = 10m,
            BomBaseQty = 1m,
            BomBaseUom = "PCS"
        };
        order.RouteSteps.Add(routeStep);
        return order;
    }

    private static ProductionWorkOrderMaterial AddMaterial(
        ProductionWorkOrder order,
        string requiredUom,
        string baseUom,
        decimal scrapPercent,
        decimal tolerance)
    {
        var material = new ProductionWorkOrderMaterial
        {
            LineNo = 1,
            MaterialSequence = 1,
            ComponentCode = "RM001",
            ComponentQtyPerParent = 2m,
            BomOutputQty = 1m,
            BomOutputUom = "PCS",
            ScrapPercent = scrapPercent,
            Tolerance = tolerance,
            RequiredUom = requiredUom,
            BaseUom = baseUom,
            ConversionFactorToBase = 1m,
            SupplySource = "PURCHASED",
            IssueMethod = "MANUAL"
        };
        order.RouteSteps.Single().Operations.Single().Materials.Add(material);
        return material;
    }

    private static ProductionWorkOrderMachine AddMachine(
        ProductionWorkOrder order,
        decimal outputPerCycle,
        int parallelCount,
        decimal cycleSeconds)
    {
        var machine = new ProductionWorkOrderMachine
        {
            MachineCode = "MC01",
            IsDefault = true,
            IsSelected = true,
            CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
            OutputPerCycle = outputPerCycle,
            OutputPerCycleUom = "PCS",
            ParallelMachineCount = parallelCount,
            CycleSeconds = cycleSeconds
        };
        order.RouteSteps.Single().Operations.Single().Machines.Add(machine);
        return machine;
    }

    private static ProductionWorkOrderLabour AddLabour(
        ProductionWorkOrder order,
        bool machineOwned,
        decimal rate,
        bool contributes)
    {
        var operation = order.RouteSteps.Single().Operations.Single();
        var labour = new ProductionWorkOrderLabour
        {
            LabourCode = "LB01",
            RateBasis = ProductionLabourRateBases.PerOutputUnit,
            Rate = rate,
            ContributesToPlan = contributes
        };

        if (machineOwned)
        {
            operation.Machines.Single().Labours.Add(labour);
        }
        else
        {
            operation.Labours.Add(labour);
        }

        return labour;
    }

    /// <summary>
    /// In-memory conversion table. Enumerates the approved conversions only; anything else fails
    /// with the production missing-conversion code so the calculator is forced to surface it.
    /// </summary>
    private sealed class FakeUomConversionService : IUomConversionService
    {
        private readonly Dictionary<(string Item, string From, string To), decimal> _factors = new();

        public FakeUomConversionService With(string item, string from, string to, decimal factor)
        {
            _factors[(item, from, to)] = factor;
            return this;
        }

        public Task<bool> HasConversionAsync(
            string companyCode, string itemCode, string fromUom, string toUom, CancellationToken cancellationToken = default) =>
            Task.FromResult(TryFactor(itemCode, fromUom, toUom, out _) || Same(fromUom, toUom));

        public Task<UomConversionResult> ConvertAsync(
            string companyCode, string itemCode, decimal quantity, string fromUom, string toUom,
            CancellationToken cancellationToken = default)
        {
            if (Same(fromUom, toUom))
            {
                return Task.FromResult(UomConversionResult.Ok(IvQty.Round(quantity), IvQty.Scale));
            }

            if (TryFactor(itemCode, fromUom, toUom, out var factor))
            {
                return Task.FromResult(UomConversionResult.Ok(
                    decimal.Round(quantity * factor, IvQty.Scale, MidpointRounding.AwayFromZero), IvQty.Scale));
            }

            return Task.FromResult(UomConversionResult.Fail(
                UomConversionFailureCodes.MissingConversion,
                $"No approved conversion from {fromUom} to {toUom} for item {itemCode}."));
        }

        private bool TryFactor(string item, string from, string to, out decimal factor)
        {
            if (_factors.TryGetValue((item, from, to), out factor))
            {
                return true;
            }

            if (_factors.TryGetValue((item, to, from), out var reverse))
            {
                factor = reverse == 0m ? 0m : 1m / reverse;
                return reverse != 0m;
            }

            factor = 0m;
            return false;
        }

        private static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
