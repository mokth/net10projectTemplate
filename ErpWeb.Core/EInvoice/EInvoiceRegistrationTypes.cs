namespace ErpWeb.Core.EInvoice;

/// <summary>
/// The LHDN registration identity types (<c>RegType</c>) MyInvois accepts for a party: BRN, NRIC,
/// PASSPORT or ARMY.
/// <para>
/// This is the single source of truth for the vocabulary. Every dropdown, validator and mapper reads
/// from here, so a value LHDN adds later is one edit in <see cref="All"/> /
/// <see cref="Options"/> rather than a hunt for four different local lists. Values are stored
/// canonically (upper case) on the master rows; <see cref="Normalize"/> is the only translation.
/// </para>
/// </summary>
public static class EInvoiceRegistrationTypes
{
    /// <summary>Business registration number (SSM).</summary>
    public const string Brn = "BRN";

    /// <summary>MyKad / identity card number.</summary>
    public const string Nric = "NRIC";

    /// <summary>Passport number.</summary>
    public const string Passport = "PASSPORT";

    /// <summary>Armed forces number.</summary>
    public const string Army = "ARMY";

    /// <summary>Every accepted value, in dropdown order.</summary>
    public static IReadOnlyList<string> All { get; } = [Brn, Nric, Passport, Army];

    /// <summary>Dropdown data (value + display text).</summary>
    public static IReadOnlyList<EInvoiceRegistrationTypeOption> Options { get; } =
    [
        new(Brn, "BRN - Business registration number"),
        new(Nric, "NRIC - MyKad"),
        new(Passport, "PASSPORT"),
        new(Army, "ARMY - Armed forces number")
    ];

    /// <summary>
    /// Canonical upper-case value, or <c>null</c> when the input is blank or not one of the four
    /// accepted types. <c>"brn"</c> and <c>" Brn "</c> resolve to <c>"BRN"</c>; <c>"IC"</c> and
    /// <c>"ABC"</c> resolve to <c>null</c> (legacy aliases are only tolerated by
    /// <see cref="LhdnCodeLookup.TryRegistrationType"/> when reading historical rows).
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>True when the value is one of the four accepted types (case/whitespace insensitive).</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null;
}

/// <summary>One entry of the registration-type dropdown.</summary>
public sealed record EInvoiceRegistrationTypeOption(string Value, string Name);
