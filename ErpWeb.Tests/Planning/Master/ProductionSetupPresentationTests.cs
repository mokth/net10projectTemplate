using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Planning.Masters;

namespace ErpWeb.Tests.Planning.Master;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionSetupPresentationTests
{
    [Theory]
    [InlineData(PrProcessTypes.Manual)]
    [InlineData(PrProcessTypes.Inspection)]
    [InlineData(PrProcessTypes.Wait)]
    [InlineData(PrProcessTypes.Subcontract)]
    public void Work_without_a_machine_does_not_prompt_for_machine_or_operator(string processType)
    {
        var operation = new PrProductDefOperationVm { ProcessType = processType };

        Assert.False(ProductionSetupText.RequiresMachineToActivate(operation));
        Assert.DoesNotContain("Choose", ProductionSetupText.MachineSummary(operation));
        Assert.DoesNotContain("Choose", ProductionSetupText.OperatorSummary(operation));
        Assert.Empty(operation.Machines);
    }

    [Theory]
    [InlineData(PrProcessTypes.Machine)]
    [InlineData(PrProcessTypes.Automated)]
    public void Machine_work_shows_missing_assignments_until_both_are_present(string processType)
    {
        var operation = new PrProductDefOperationVm { ProcessType = processType };
        Assert.True(ProductionSetupText.RequiresMachineToActivate(operation));
        Assert.Contains("Choose", ProductionSetupText.MachineSummary(operation));
        Assert.Contains("Choose", ProductionSetupText.OperatorSummary(operation));

        var machine = new PrProductDefMachineVm { MachineCode = "CUT01" };
        operation.Machines.Add(machine);
        Assert.DoesNotContain("Choose", ProductionSetupText.MachineSummary(operation));
        Assert.Contains("Choose", ProductionSetupText.OperatorSummary(operation));

        machine.Labours.Add(new PrProductDefLabourVm { LabourCode = "OP01" });
        Assert.DoesNotContain("Choose", ProductionSetupText.OperatorSummary(operation));
    }

    [Fact]
    public void Packing_guidance_follows_the_existing_activation_rules_without_adding_resources()
    {
        var operation = new PrProductDefOperationVm { ProcessType = PrProcessTypes.Packing };
        Assert.False(ProductionSetupText.RequiresMachineToActivate(operation));
        Assert.Contains("Optional", ProductionSetupText.MachineSummary(operation));

        operation.Machines.Add(new PrProductDefMachineVm { MachineCode = "PACK01" });
        Assert.True(ProductionSetupText.RequiresMachineToActivate(operation));
        Assert.Contains("Choose", ProductionSetupText.OperatorSummary(operation));
        Assert.Empty(operation.Machines[0].Labours);
    }

    [Fact]
    public void Nested_validation_identifies_the_work_step_or_material_instead_of_a_property_path()
    {
        var model = new PrProductDefEditVm
        {
            Operations =
            [
                new() { OperationCode = "CUT", OperationDescription = "Cut boards", WorkCentreCode = "CUTTING" },
                new() { OperationCode = "PACK", OperationDescription = "Pack boxes", WorkCentreCode = "PACKING" }
            ],
            Lines = [new() { ICode = "RM001", IName = "Carton", StdQty = 100m }]
        };

        var operatorLabel = ProductionSetupText.ValidationLabel("Operations[1].Machines[0].Labours[0].LabourCode", model);
        Assert.Contains("Pack boxes", operatorLabel);
        Assert.Contains("PACKING", operatorLabel);
        Assert.Contains("Operator", operatorLabel);
        Assert.DoesNotContain("Operations[", operatorLabel);
        Assert.DoesNotContain("Cut boards", operatorLabel);

        var materialLabel = ProductionSetupText.ValidationLabel("Lines[0].StdQty", model);
        Assert.Contains("Carton", materialLabel);
        Assert.Contains("RM001", materialLabel);
        Assert.Contains("Quantity needed", materialLabel);
        Assert.Equal(100m, model.Lines[0].StdQty);
    }

    [Theory]
    [InlineData("Operations[99].Machines", "Machine")]
    [InlineData("Lines[9999999999999999999999999].Warehouse", "Material warehouse")]
    [InlineData("Unknown.Field", "Production setup")]
    public void Validation_still_renders_when_a_row_is_missing_or_the_key_is_unrecognised(string key, string expected)
    {
        Assert.Equal(expected, ProductionSetupText.ValidationLabel(key, new PrProductDefEditVm()));
    }
}
