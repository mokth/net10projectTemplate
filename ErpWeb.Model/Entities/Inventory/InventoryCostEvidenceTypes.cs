namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// Structured evidence states for manually-originated inventory cost. These values are persisted
/// separately from the human-readable <see cref="IvTrxBatchDetail.PriceEvidence"/> text so ledger
/// valuation never has to infer approval semantics from free-form text.
/// </summary>
public static class InventoryCostEvidenceTypes
{
    public const string ManualApproved = "MANUAL_APPROVED";
    public const string ZeroCostApproved = "ZERO_COST_APPROVED";
    public const string OpeningApproved = "OPENING_APPROVED";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ManualApproved,
        ZeroCostApproved,
        OpeningApproved
    };

    public static bool IsKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) && All.Contains(value.Trim());

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToUpperInvariant();
        return IsKnown(normalized) ? normalized : null;
    }
}
