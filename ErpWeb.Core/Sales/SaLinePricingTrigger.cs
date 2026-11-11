namespace ErpWeb.Core.Sales;

/// <summary>
/// Decides when a Sales line popup may call the live price engine.
/// Source identity is document keys (SO/DO/invoice line), never <c>PricingSource</c> tokens.
/// Quantity, tax group, and tax-basis changes still update the popup fields; RecalcDocument
/// recomputes extended amounts from the kept <c>UnitPrice</c>.
/// </summary>
public static class SaLinePricingTrigger
{
    public static bool HasInheritedSource(string? sourceDocumentNo, short? sourceLine) =>
        !string.IsNullOrWhiteSpace(sourceDocumentNo) && sourceLine is > 0;

    public static bool HasInheritedSource(string? sourceDocumentNo, int? sourceLine) =>
        !string.IsNullOrWhiteSpace(sourceDocumentNo) && sourceLine is > 0;

    /// <summary>
    /// Live resolve is only for a new direct line that has not accepted a manual price.
    /// Existing/saved and source-inherited lines keep UnitPrice. Item-code change is a
    /// separate trigger and is not gated here.
    /// </summary>
    public static bool AllowLiveResolve(
        bool isEditingExistingLine,
        bool hasInheritedSource,
        bool isManualOverride) =>
        !isEditingExistingLine && !hasInheritedSource && !isManualOverride;

    /// <summary>
    /// Commercial price lookup UOM. Stock/standard UOM is a fallback only and is never converted here.
    /// </summary>
    public static string CommercialUom(string? sellingUom, string? stdUom)
    {
        var selling = (sellingUom ?? string.Empty).Trim();
        return selling.Length > 0 ? selling : (stdUom ?? string.Empty).Trim();
    }
}
