using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Single supported-mode predicate for Daily Production workspace, Create, Update, and Post.
/// Do not duplicate IssueMethod/SupplySource checks elsewhere.
/// </summary>
public static class ProductionOutputMaterialSupport
{
    public static string? Validate(ProductionWorkOrderMaterial material) =>
        BlockingReason(material);

    public static bool IsSupported(ProductionWorkOrderMaterial material) =>
        BlockingReason(material) is null;

    public static string? BlockingReason(ProductionWorkOrderMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (string.Equals(material.IssueMethod, PrMaterialIssueMethods.Backflush, StringComparison.OrdinalIgnoreCase)
            || string.Equals(material.IssueMethod, PrMaterialIssueMethods.PickList, StringComparison.OrdinalIgnoreCase)
            || string.Equals(material.SupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.OrdinalIgnoreCase))
        {
            return $"{material.IssueMethod}/{material.SupplySource} is not supported for Daily Production in this milestone.";
        }

        if (string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.OrdinalIgnoreCase))
        {
            return material.ProducingRouteStepId is null
                ? "Producing route step is missing."
                : null;
        }

        if (string.Equals(material.IssueMethod, PrMaterialIssueMethods.Manual, StringComparison.OrdinalIgnoreCase)
            && (material.SupplySource is PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply))
        {
            return null;
        }

        return $"{material.IssueMethod}/{material.SupplySource} is not supported for Daily Production in this milestone.";
    }
}
