using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

public enum ProductionOutputProjectionTransition
{
    IncludeCurrentPost,
    ExcludeCurrentRollback,
}

public static class ProductionOutputMaterialFacts
{
    public static string? ValidateConsume(
        decimal consumeQty,
        decimal standardQty,
        decimal maxQty,
        string? reasonCode,
        string? reasonText,
        bool interactive)
    {
        consumeQty = IvQty.Round(consumeQty);
        standardQty = IvQty.Round(standardQty);
        maxQty = IvQty.Round(maxQty);
        if (consumeQty < 0m)
            return "Consume quantity cannot be negative.";
        if (consumeQty > maxQty)
            return $"Consume quantity {consumeQty} exceeds the Daily Production document maximum {maxQty}.";

        var variance = ProductionMaterialExecutionCalc.DailyProductionVarianceQty(consumeQty, standardQty);
        var code = ProductionMaterialVarianceReasonCodes.Normalize(reasonCode);
        var text = string.IsNullOrWhiteSpace(reasonText) ? null : reasonText.Trim();

        if (variance == 0m)
            return null;

        if (code is null)
            return "A variance reason is required when consume differs from the document standard.";
        if (interactive && string.Equals(code, ProductionMaterialVarianceReasonCodes.LegacyUnclassified, StringComparison.Ordinal))
            return "LEGACY_UNCLASSIFIED is a system-only reason and cannot be entered on a Daily Production document.";
        if (interactive && !ProductionMaterialVarianceReasonCodes.IsUserSelectableCode(code))
            return $"Unknown variance reason '{reasonCode}'.";
        if (!interactive && !ProductionMaterialVarianceReasonCodes.IsValidPersistedCode(code))
            return $"Unknown variance reason '{reasonCode}'.";
        if (string.Equals(code, ProductionMaterialVarianceReasonCodes.Other, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(text))
            return "Reason detail is required when the variance reason is OTHER.";
        return null;
    }

    public static (string? Code, string? Text) NormalizeReason(
        decimal consumeQty, decimal standardQty, string? reasonCode, string? reasonText)
    {
        var variance = ProductionMaterialExecutionCalc.DailyProductionVarianceQty(consumeQty, standardQty);
        if (variance == 0m)
            return (null, null);
        return (
            ProductionMaterialVarianceReasonCodes.Normalize(reasonCode),
            string.IsNullOrWhiteSpace(reasonText) ? null : reasonText.Trim());
    }
}
