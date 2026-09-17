namespace ErpWeb.Core.Sales;

/// <summary>
/// Phase 4 — price override governance.
///
/// This is the SINGLE implementation of "may this line change the price the engine resolved?", called
/// by every document service. It lives here rather than in the pages because the page is not the
/// execution point: a tampered post must not be able to invent a price, and any future bulk/import
/// path must run the same rule.
///
/// The rule is deliberately narrow and needs no knowledge of the engine's own arithmetic:
/// <list type="bullet">
/// <item>a caller that echoes the resolved price back is NOT overriding, so the common case needs no
/// permission and stores nothing;</item>
/// <item>an override needs <c>PRICE_OVERRIDE</c> AND a reason, because an unexplained price change is
/// indistinguishable from a mistake.</item>
/// </list>
///
/// "NULL <c>OriginalUnitPrice</c> = never overridden" is preserved by <see cref="NormalizeOriginal"/>:
/// an unchanged line stores NULL rather than a copy of its own price.
/// </summary>
public static class SaPriceOverridePolicy
{
    /// <summary>Width of <c>OverrideReason</c>; longer text is truncated rather than rejected.</summary>
    public const int MaxReasonLength = 100;

    public const string PermissionRequiredMessage =
        "You do not have permission to change a line price. Ask an administrator for the price-override right.";

    public const string ReasonRequiredMessage =
        "A changed price needs a reason. Enter why this line's price differs from the price the system resolved.";

    /// <summary>
    /// True when the line actually departs from the engine price. A caller that does not supply the
    /// engine price at all (an older client, or a line typed with no resolution) is not treated as an
    /// override — there is nothing to compare against, and inventing a requirement there would block
    /// legitimate entry.
    /// </summary>
    public static bool IsOverride(decimal unitPrice, decimal? originalUnitPrice) =>
        originalUnitPrice is { } original && original != unitPrice;

    /// <summary>
    /// The value to PERSIST in <c>OriginalUnitPrice</c>: the engine price on a real override, otherwise
    /// NULL so the overwhelmingly common case leaves the column empty.
    /// </summary>
    public static decimal? NormalizeOriginal(decimal unitPrice, decimal? originalUnitPrice) =>
        IsOverride(unitPrice, originalUnitPrice) ? originalUnitPrice : null;

    /// <summary>
    /// The value to PERSIST in <c>OverrideReason</c>: the trimmed reason on a real override, otherwise
    /// NULL so a stray reason on an unchanged line cannot masquerade as a recorded override.
    /// </summary>
    public static string? NormalizeReason(decimal unitPrice, decimal? originalUnitPrice, string? reason) =>
        IsOverride(unitPrice, originalUnitPrice) ? Truncate(reason, MaxReasonLength) : null;

    /// <summary>
    /// Validates a prepared line set. Returns an operator-facing message for the FIRST offending line,
    /// or null when the whole set is acceptable.
    ///
    /// Permission is checked before the reason so a caller without the right is told the actual
    /// problem rather than being invited to type a reason that would still be refused.
    /// </summary>
    public static string? Validate(IReadOnlyList<SaPriceOverrideDeclaration> lines, bool canOverridePrice)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var anyOverride = lines.Any(x => IsOverride(x.UnitPrice, x.OriginalUnitPrice));
        if (!anyOverride)
        {
            return null;
        }

        if (!canOverridePrice)
        {
            return PermissionRequiredMessage;
        }

        // NOTE: FirstOrDefault on a collection of a VALUE type yields default(T), never null, so the
        // "is anything missing a reason?" question must be asked with Any rather than a null check.
        var reasonMissing = lines.Any(x =>
            IsOverride(x.UnitPrice, x.OriginalUnitPrice) && string.IsNullOrWhiteSpace(x.Reason));

        return reasonMissing ? ReasonRequiredMessage : null;
    }

    private static string? Truncate(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// One line's override declaration, as the service sees it after normalisation: the price about to be
/// stored, the engine price the caller reported (null when it reported none) and the stated reason.
/// </summary>
public readonly record struct SaPriceOverrideDeclaration(
    decimal UnitPrice,
    decimal? OriginalUnitPrice,
    string? Reason);
