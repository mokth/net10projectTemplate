namespace ErpWeb.Core.StockLedger;

public enum StockValuationMovementClass
{
    Normal,
    ValueOnlyAdjustment
}

/// <summary>
/// Central classification for facts that change value without representing a normal physical
/// receipt/issue. This is intentionally shared by valuation, snapshots and future adjustment
/// writers so a new value-only movement cannot silently inflate receipt or COGS totals.
/// </summary>
public sealed class StockValuationMovementClassifier
{
    private static readonly IReadOnlySet<string> ValueOnlyCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ADJUST_IN", "ADJUST_OUT",
        "PURCHASE_PRICE_VARIANCE_IN", "PURCHASE_PRICE_VARIANCE_OUT",
        "LANDED_COST_IN", "LANDED_COST_OUT",
        "STANDARD_REVALUE_IN", "STANDARD_REVALUE_OUT",
        "RETURN_STANDARD_VARIANCE_IN", "RETURN_STANDARD_VARIANCE_OUT",
        "COST_METHOD_CUTOVER_IN", "COST_METHOD_CUTOVER_OUT"
    };

    public StockValuationMovementClass Classify(string? movementCode)
    {
        var normalized = Normalize(movementCode);
        if (ValueOnlyCodes.Contains(normalized))
            return StockValuationMovementClass.ValueOnlyAdjustment;

        if (normalized.EndsWith("_REVERSAL", StringComparison.Ordinal))
        {
            var original = normalized[..^"_REVERSAL".Length];
            if (ValueOnlyCodes.Contains(original))
                return StockValuationMovementClass.ValueOnlyAdjustment;
        }

        return StockValuationMovementClass.Normal;
    }

    public bool IsValueOnlyAdjustment(string? movementCode) =>
        Classify(movementCode) == StockValuationMovementClass.ValueOnlyAdjustment;

    private static string Normalize(string? movementCode) =>
        (movementCode ?? string.Empty).Trim().ToUpperInvariant();
}
