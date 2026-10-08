using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionAbsorbedCostCalculatorTests
{
    [Fact]
    public void Calculates_only_frozen_contributors_for_good_quantity()
    {
        var operation = new ProductionWorkOrderOperation
        {
            Uid = 10,
            OperationCode = "OP10",
            ProcessType = PrProcessTypes.Machine,
            PlannedOutputUom = "PCS",
            UtilitiesOverheadCostPerOutputUnit = 2m,
            OtherCostPerOutputUnit = 1m,
        };
        operation.Labours.Add(new ProductionWorkOrderLabour
        {
            Uid = 101,
            LabourCode = "DIRECT",
            RateBasis = ProductionLabourRateBases.PerOutputUnit,
            Rate = 8m,
            ContributesToPlan = true,
        });
        var selectedMachine = new ProductionWorkOrderMachine
        {
            Uid = 201,
            IsSelected = true,
            CostPerOutputUnit = 5m,
        };
        selectedMachine.Labours.Add(new ProductionWorkOrderLabour
        {
            Uid = 202,
            LabourCode = "MACHINE-LABOUR",
            RateBasis = ProductionLabourRateBases.PerOutputUnit,
            Rate = 4m,
            ContributesToPlan = true,
        });
        operation.Machines.Add(selectedMachine);
        operation.Machines.Add(new ProductionWorkOrderMachine
        {
            Uid = 301,
            IsSelected = false,
            CostPerOutputUnit = 99m,
        });

        var result = new ProductionAbsorbedCostCalculator().Calculate(operation, 10m, " pcs ");

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.Equal(5, result.Lines.Count);
        Assert.Equal(200m, result.TotalCost);
        Assert.Equal(
            [
                ProductionConversionCostTypes.Labour,
                ProductionConversionCostTypes.Labour,
                ProductionConversionCostTypes.Machine,
                ProductionConversionCostTypes.UtilitiesOverhead,
                ProductionConversionCostTypes.Other,
            ],
            result.Lines.Select(x => x.CostType));
        Assert.Contains(result.Lines, x => x.SourceLineKey == "LABOUR:101" && x.CostAmount == 80m);
        Assert.Contains(result.Lines, x => x.SourceLineKey == "LABOUR:202" && x.CostAmount == 40m);
        Assert.Contains(result.Lines, x => x.SourceLineKey == "MACHINE:201" && x.CostAmount == 50m);
        Assert.Contains(result.Lines, x => x.SourceLineKey == "UTILITIES_OVERHEAD:10" && x.CostAmount == 20m);
        Assert.Contains(result.Lines, x => x.SourceLineKey == "OTHER:10" && x.CostAmount == 10m);
    }

    [Fact]
    public void Fails_closed_for_uom_mismatch_and_duplicate_selected_machine()
    {
        var operation = new ProductionWorkOrderOperation
        {
            Uid = 10,
            OperationCode = "OP10",
            ProcessType = PrProcessTypes.Machine,
            PlannedOutputUom = "PCS",
        };
        operation.Machines.Add(new ProductionWorkOrderMachine { Uid = 1, IsSelected = true });
        operation.Machines.Add(new ProductionWorkOrderMachine { Uid = 2, IsSelected = true });

        var uomResult = new ProductionAbsorbedCostCalculator().Calculate(operation, 1m, "KG");
        Assert.False(uomResult.Succeeded);

        operation.Machines.Clear();
        operation.Machines.Add(new ProductionWorkOrderMachine { Uid = 1, IsSelected = true });
        operation.Machines.Add(new ProductionWorkOrderMachine { Uid = 2, IsSelected = true });
        var machineResult = new ProductionAbsorbedCostCalculator().Calculate(operation, 1m, "PCS");
        Assert.False(machineResult.Succeeded);
        Assert.Contains("exactly one selected", machineResult.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_contributing_labour_basis_blocks_costing()
    {
        var operation = new ProductionWorkOrderOperation
        {
            OperationCode = "OP10",
            ProcessType = PrProcessTypes.Manual,
            PlannedOutputUom = "PCS",
        };
        operation.Labours.Add(new ProductionWorkOrderLabour
        {
            LabourCode = "LB01",
            RateBasis = "PER_HOUR",
            Rate = 1m,
            ContributesToPlan = true,
        });

        var result = new ProductionAbsorbedCostCalculator().Calculate(operation, 1m, "PCS");

        Assert.False(result.Succeeded);
        Assert.Contains("unsupported rate basis", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }
}
