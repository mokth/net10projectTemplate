using System.Globalization;

namespace ErpWeb.Core.Settings;

/// <summary>
/// The pure resolution ladder: Branch → Company → Global → code default.
///
/// <para>
/// No database, no DI, no logging — the same discipline as <c>SaCompanyPriceMethod</c>, so the whole
/// ladder is testable without a fixture. The service layer owns loading raw values; this type owns
/// deciding which one wins and why.
/// </para>
///
/// <para>
/// Two behaviours are deliberate and load-bearing:
/// </para>
/// <list type="bullet">
/// <item>a scope the definition does not allow is skipped entirely (I3) — the table never widens it;</item>
/// <item>a stored value that cannot be parsed, or a token that is not in the allowed list, does NOT
/// throw and does NOT win. It is recorded in <see cref="SettingResolution.RejectedReason"/> and the
/// walk falls through to the next level (I2). A settings table is read by every page; a mistyped value
/// must degrade to the code default rather than break a document.</item>
/// </list>
/// </summary>
public static class AppSettingResolver
{
    private static readonly string[] TrueTokens = ["1", "TRUE", "YES", "Y", "ON"];
    private static readonly string[] FalseTokens = ["0", "FALSE", "NO", "N", "OFF"];

    /// <summary>
    /// Resolves <paramref name="definition"/> from the three raw stored values. Pass <c>null</c> for a
    /// level the caller must not resolve at — the resolver does not invent a scope.
    /// </summary>
    public static SettingResolution Resolve(
        AppSettingDefinition definition,
        string? branchRaw,
        string? companyRaw,
        string? globalRaw)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string? rejectedReason = null;

        foreach (var (scope, raw) in Candidates(definition, branchRaw, companyRaw, globalRaw))
        {
            // blank means "not set at this level" — not an error.
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!TryAccept(definition, raw, out var accepted, out var reason))
            {
                rejectedReason ??= $"{scope}: {reason}";
                continue;
            }

            return new SettingResolution(Canonicalise(definition, accepted), scope, IsDefault: false, RejectedReason: null);
        }

        return new SettingResolution(
            Canonicalise(definition, definition.DefaultValue?.Trim()),
            AppSettingScope.None,
            IsDefault: true,
            RejectedReason: rejectedReason);
    }

    /// <summary>
    /// Resolves a definition backed by an existing column. There is no ladder: the owning column is the
    /// only source, and an unusable stored value falls back to the default exactly as above.
    /// </summary>
    public static SettingResolution ResolveColumnBacked(
        AppSettingDefinition definition,
        string? raw,
        AppSettingScope provenance = AppSettingScope.Company)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!string.IsNullOrWhiteSpace(raw) && TryAccept(definition, raw, out var accepted, out var reason))
        {
            return new SettingResolution(Canonicalise(definition, accepted), provenance, IsDefault: false, RejectedReason: null);
        }

        var rejected = string.IsNullOrWhiteSpace(raw)
            ? null
            : $"{provenance}: {DescribeFailure(definition, raw)}";

        return new SettingResolution(
            Canonicalise(definition, definition.DefaultValue?.Trim()),
            AppSettingScope.None,
            IsDefault: true,
            RejectedReason: rejected);
    }

    /// <summary>
    /// Parses and formats a value into the canonical stored form for its type, or returns null when it
    /// cannot be represented. Used by the write path so what is stored is always what the resolver will
    /// accept back.
    /// </summary>
    public static bool TryNormaliseForStorage(
        AppSettingDefinition definition,
        string? raw,
        out string? canonical,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!TryAccept(definition, raw, out var accepted, out error))
        {
            canonical = null;
            return false;
        }

        canonical = Canonicalise(definition, accepted);
        return true;
    }

    private static IEnumerable<(AppSettingScope Scope, string? Raw)> Candidates(
        AppSettingDefinition definition,
        string? branchRaw,
        string? companyRaw,
        string? globalRaw)
    {
        if (definition.Allows(AppSettingScope.Branch))
        {
            yield return (AppSettingScope.Branch, branchRaw);
        }

        if (definition.Allows(AppSettingScope.Company))
        {
            yield return (AppSettingScope.Company, companyRaw);
        }

        if (definition.Allows(AppSettingScope.Global))
        {
            yield return (AppSettingScope.Global, globalRaw);
        }
    }

    private static bool TryAccept(
        AppSettingDefinition definition,
        string? raw,
        out string? accepted,
        out string? error)
    {
        accepted = null;
        error = null;

        if (raw is null)
        {
            error = "no value supplied.";
            return false;
        }

        switch (definition.Type)
        {
            case AppSettingType.Text:
                accepted = raw.Trim();
                return true;

            case AppSettingType.Number:
                if (decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    // "0." + hashes drops trailing zeros, so 45, 45.0 and 45.00 all canonicalise to "45".
                    // Without that, a value's textual form would depend on how the storage engine
                    // round-tripped the decimal, and two equal numbers would compare unequal as strings.
                    accepted = number.ToString("0.############################", CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"'{raw.Trim()}' is not a number.";
                return false;

            case AppSettingType.Date:
                if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    accepted = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"'{raw.Trim()}' is not a date.";
                return false;

            case AppSettingType.Flag:
                var flag = raw.Trim().ToUpperInvariant();
                if (TrueTokens.Contains(flag, StringComparer.Ordinal))
                {
                    accepted = "true";
                    return true;
                }

                if (FalseTokens.Contains(flag, StringComparer.Ordinal))
                {
                    accepted = "false";
                    return true;
                }

                error = $"'{raw.Trim()}' is not a yes/no value.";
                return false;

            case AppSettingType.Token:
                if (!definition.HasTokens)
                {
                    // A Token definition with no token list is a CATALOGUE bug, not a data condition:
                    // fail loudly rather than silently accepting anything (I9).
                    throw new InvalidOperationException(
                        $"The setting {definition.Module}.{definition.Key} is declared as a Token but "
                        + "declares no allowed tokens, so no stored value could ever be validated.");

                }

                var token = raw.Trim();
                if (definition.AllowedTokens!.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase)))
                {
                    accepted = token;
                    return true;
                }

                error = $"'{token}' is not one of {string.Join(", ", definition.AllowedTokens!)}.";
                return false;

            default:
                error = $"unsupported setting type {definition.Type}.";
                return false;
        }
    }

    private static string? Canonicalise(AppSettingDefinition definition, string? value) =>
        definition.Normalize is null ? value : definition.Normalize(value);

    private static string DescribeFailure(AppSettingDefinition definition, string raw)
    {
        // Re-uses the same validation so the reason can never drift from the rule.
        _ = TryAccept(definition, raw, out _, out var error);
        return error ?? "the stored value is not usable.";
    }
}
