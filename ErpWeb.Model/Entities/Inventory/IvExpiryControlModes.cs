namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// Canonical item-level expiry policy values.
/// </summary>
public static class IvExpiryControlModes
{
    public const string None = "NONE";
    public const string Optional = "OPTIONAL";
    public const string Required = "REQUIRED";

    public static IReadOnlyList<string> Allowed { get; } =
    [
        None,
        Optional,
        Required
    ];

    public static bool TryNormalize(string? value, out string normalized)
    {
        var candidate = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (Allowed.Contains(candidate, StringComparer.Ordinal))
        {
            normalized = candidate;
            return true;
        }

        normalized = None;
        return false;
    }
}
