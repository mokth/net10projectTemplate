namespace ErpWeb.Core.Security;

/// <summary>
/// Shared keys and helpers for the cookie security revalidation timestamp that is persisted in the
/// authentication ticket properties, so cookie revalidation does not hit the database on every request.
/// </summary>
public static class SecurityRevalidationConstants
{
    /// <summary>Ticket property storing the UTC timestamp of the last successful DB security validation.</summary>
    public const string ValidatedUtcKey = "SecurityValidatedUtc";

    /// <summary>UTC timestamp of the last successful DB security validation (or null when never validated).</summary>
    public static DateTimeOffset? ReadValidatedUtc(IDictionary<string, string?> items)
    {
        if (items.TryGetValue(ValidatedUtcKey, out var raw) &&
            DateTimeOffset.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
