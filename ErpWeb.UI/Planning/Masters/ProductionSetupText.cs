using System.Text.RegularExpressions;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.UI.Planning.Masters;

/// <summary>Display text only. Persisted codes and production rules stay in the existing services.</summary>
internal static class ProductionSetupText
{
    internal static string CodeAndName(string code, string? name) =>
        string.IsNullOrWhiteSpace(name) || string.Equals(code, name, StringComparison.OrdinalIgnoreCase)
            ? code : $"{name} ({code})";

    internal static string ProcessType(string value) => value switch
    {
        PrProcessTypes.Machine => "Machine work",
        PrProcessTypes.Automated => "Automated work",
        PrProcessTypes.Manual => "Manual work",
        PrProcessTypes.Inspection => "Quality check",
        PrProcessTypes.Wait => "Waiting / drying",
        PrProcessTypes.Packing => "Packing",
        PrProcessTypes.Subcontract => "Work done by supplier",
        _ => value
    };

    internal static string IssueMethod(string value) => value switch
    {
        PrMaterialIssueMethods.Manual => "Record a material issue",
        PrMaterialIssueMethods.Backflush => "Automatic issue (backflush)",
        PrMaterialIssueMethods.PickList => "Use a picking list",
        _ => value
    };

    internal static string SupplySource(string value) => value switch
    {
        PrMaterialSupplySources.Purchased => "Purchased material",
        PrMaterialSupplySources.InternalRouteWip => "Made at another stage in this setup",
        PrMaterialSupplySources.SeparateProductDefinition => "Made using another production setup",
        PrMaterialSupplySources.ExternalSupply => "Supplied from outside",
        _ => value
    };

    internal static string Status(string value) => value switch
    {
        PrBomStatuses.Draft => "Draft",
        PrBomStatuses.Active => "In use",
        PrBomStatuses.Superseded => "Replaced by a newer version",
        PrBomStatuses.Inactive => "Inactive",
        _ => value
    };

    internal static string ManufacturingType(string value) => value switch
    {
        PrMfgTypes.Buy => "Purchased",
        PrMfgTypes.Make => "Made in-house",
        PrMfgTypes.Phantom => "Grouped materials (phantom)",
        _ => value
    };

    internal static bool RequiresMachineToActivate(PrProductDefOperationVm operation) =>
        operation.ProcessType is PrProcessTypes.Machine or PrProcessTypes.Automated
        || (operation.ProcessType == PrProcessTypes.Packing && operation.Machines.Count > 0);

    internal static string MachineSummary(PrProductDefOperationVm operation) =>
        operation.Machines.Count > 0 ? $"{operation.Machines.Count} assigned"
        : RequiresMachineToActivate(operation) ? "Choose a machine"
        : operation.ProcessType == PrProcessTypes.Packing ? "Optional for packing"
        : "No machine needed";

    internal static string OperatorSummary(PrProductDefOperationVm operation) =>
        operation.Machines.Any(x => x.Labours.Count > 0) ? "Operator assigned"
        : RequiresMachineToActivate(operation) ? "Choose an operator"
        : "No operator assignment needed";

    internal static string ValidationLabel(string key, PrProductDefEditVm model)
    {
        var match = Regex.Match(key, @"^(Operations|Lines)\[(\d+)\]");
        var context = string.Empty;
        if (match.Success && int.TryParse(match.Groups[2].Value, out var index))
        {
            if (match.Groups[1].Value == "Operations" && index < model.Operations.Count)
            {
                var operation = model.Operations[index];
                context = $"{CodeAndName(operation.OperationCode, operation.OperationDescription)} · {operation.WorkCentreCode} — ";
            }
            else if (match.Groups[1].Value == "Lines" && index < model.Lines.Count)
            {
                var line = model.Lines[index];
                context = $"{CodeAndName(line.ICode, line.IName)} — ";
            }
        }

        var field = key.Split('.').Last();
        var label = field switch
        {
            "ProdCode" => "Product",
            "DefinitionCode" => "Setup code",
            "DefinitionName" => "Setup name",
            "BaseQty" => "Quantity to make",
            "BaseUom" => "Production unit",
            "IsDefaultDefinition" => "Usual setup for this product",
            "Prefix" => "Work order numbering",
            "Remark" => "Notes",
            "MfgType" => "How the item is supplied",
            "Lines" => "Materials",
            "Operations" => "Work steps",
            "WorkCentreCode" => "Production area",
            "OutputItemCode" => "Item made at this stage",
            "CentralSequence" => "Stage order",
            "OutputBaseQty" => "Stage output quantity",
            "OutputUom" or "StdUom" => "Unit",
            "OperationCode" or "OperationKey" => "Work step",
            "ProcessType" => "Type of work",
            "ProcessSequence" => "Work step order",
            "StandardDurationMinutes" => "Time needed",
            "IsFinalOperation" => "Last work step in this stage",
            "SetupLossQty" => "Quantity lost during setup",
            "OperationLossQty" => "Quantity lost during work",
            "UtilitiesOverheadCostPerOutputUnit" => "Utilities and overhead cost",
            "OtherCostPerOutputUnit" => "Other cost",
            "Machines" or "MachineCode" => "Machine",
            "ResourceSequence" or "Priority" => "Machine order",
            "IsPrimary" => "Usual machine",
            "OutputPerCycle" => "Quantity per machine cycle",
            "CycleSeconds" => "Cycle time",
            "SetupSeconds" => "Setup time",
            "ConversionSeconds" => "Additional time",
            "QueueSeconds" => "Waiting time",
            "MachineRatePerHour" => "Machine cost per hour",
            "ParallelMachineCount" => "Machines running together",
            "Labours" or "LabourCode" => "Operator",
            "CostPerOutputUnit" => "Labour cost per unit made",
            "ICode" => "Material",
            "StdQty" => "Quantity needed",
            "Warehouse" => "Material warehouse",
            "ScrapPercent" => "Waste allowance (%)",
            "Tolerance" => "Material tolerance",
            "BomDefault" => "Usual material",
            "AlternateGroupCode" => "Substitute group",
            "IssueMethod" => "How materials are issued",
            "SupplySource" => "Where the material comes from",
            "ProducingRouteStepKey" => "Stage that makes this material",
            "ComponentDefinitionCode" => "Material's production setup",
            _ => "Production setup"
        };
        return context + label;
    }

    internal static string ValidationMessage(string message) => message
        .Replace(PrMaterialSupplySources.SeparateProductDefinition, "'Made using another production setup'", StringComparison.Ordinal)
        .Replace(PrMaterialSupplySources.InternalRouteWip, "'Made at another stage in this setup'", StringComparison.Ordinal)
        .Replace("Base UOM", "production unit", StringComparison.Ordinal)
        .Replace("inventory UOM", "stock unit", StringComparison.Ordinal)
        .Replace("BOM component", "material", StringComparison.Ordinal)
        .Replace("before activation", "before using this setup", StringComparison.Ordinal);
}
