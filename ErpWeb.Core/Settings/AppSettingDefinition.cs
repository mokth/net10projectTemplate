namespace ErpWeb.Core.Settings;

/// <summary>The value shape a setting is stored and parsed as. The database holds four typed columns.</summary>
public enum AppSettingType
{
    /// <summary>Free text. An empty string is a legitimate value; <c>null</c>/blank means "not set".</summary>
    Text,

    /// <summary>Decimal, parsed and formatted with the invariant culture.</summary>
    Number,

    /// <summary>Date only; the time component is dropped.</summary>
    Date,

    /// <summary>Boolean, accepted as 1/0, true/false, yes/no, y/n.</summary>
    Flag,

    /// <summary>One of a fixed token list declared by the definition. A token with no list is a catalogue error.</summary>
    Token
}

/// <summary>
/// Which tenancy levels a setting may be stored at. A flags value, because a definition can permit
/// several (for example Company and Branch).
/// </summary>
[Flags]
public enum AppSettingScope
{
    /// <summary>Not a scope — used as the provenance of a code default, and as "no scope".</summary>
    None = 0,

    Global = 1,

    Company = 2,

    Branch = 4
}

/// <summary>Where a definition's live value actually lives.</summary>
public enum AppSettingBacking
{
    /// <summary>Rows in <c>dbo.AdSmParam</c>. Read and written through this feature.</summary>
    Registry,

    /// <summary>
    /// An existing typed column, projected read-only through an <c>IAppSettingValueProvider</c>.
    /// Never stored in <c>AdSmParam</c> and never written through the settings service, so the
    /// registry can never become a second authority for a value that already has a home.
    /// </summary>
    ExistingColumn
}

/// <summary>
/// One legal setting: the single authority for whether a key exists, what type it has, which scopes it
/// may be stored at, what its allowed tokens are and what it resolves to when nothing is stored.
///
/// <para>
/// A definition is the ONLY way a key becomes a setting. Rows in <c>dbo.AdSmParam</c> whose key is not
/// in <see cref="AppSettingCatalogue"/> are ignored on read and surfaced only as a diagnostic.
/// </para>
/// </summary>
/// <param name="Module">Owning module — must be in <see cref="AppSettingModules.All"/>.</param>
/// <param name="Key">Key within the module, e.g. <c>PRICE_METHOD</c>.</param>
/// <param name="Type">Value shape.</param>
/// <param name="AllowedScopes">Scopes the setting may be stored at. Authoritative over the table (I3).</param>
/// <param name="DefaultValue">Resolved when no row matches. Never null for a well-formed definition.</param>
/// <param name="AllowedTokens">Required for <see cref="AppSettingType.Token"/>; ignored otherwise.</param>
/// <param name="Backing">Registry row or a read-only projection over an existing column.</param>
/// <param name="Normalize">
/// Optional canonicaliser applied to the value AFTER provenance is decided, so a projection reports the
/// same form the owning module uses. Deliberately not used for validation — an invalid stored token is
/// reported as rejected rather than silently corrected.
/// </param>
/// <param name="Description">Shown on the admin screen so an operator can tell what a key affects.</param>
public sealed record AppSettingDefinition(
    string Module,
    string Key,
    AppSettingType Type,
    AppSettingScope AllowedScopes,
    string? DefaultValue,
    IReadOnlyList<string>? AllowedTokens = null,
    AppSettingBacking Backing = AppSettingBacking.Registry,
    Func<string?, string?>? Normalize = null,
    string? Description = null)
{
    /// <summary>
    /// Case-insensitive composite key used for catalogue lookups. Values are persisted with whatever
    /// casing the definition declares, so the lookup must not be case-sensitive.
    /// </summary>
    public static string BuildLookupKey(string? module, string? key) =>
        $"{(module ?? string.Empty).Trim().ToUpperInvariant()}|{(key ?? string.Empty).Trim().ToUpperInvariant()}";

    public string LookupKey => BuildLookupKey(Module, Key);

    /// <summary>True when the definition permits <paramref name="scope"/>.</summary>
    public bool Allows(AppSettingScope scope) => scope != AppSettingScope.None && AllowedScopes.HasFlag(scope);

    /// <summary>True when the definition declares at least one allowed token.</summary>
    public bool HasTokens => AllowedTokens is { Count: > 0 };
}

/// <summary>
/// The outcome of resolving one definition. <see cref="Provenance"/> says WHERE the value came from,
/// which is what makes a support question answerable ("this company is set to PRICE_LIST_ONLY"), and
/// <see cref="RejectedReason"/> records a stored-but-unusable value without throwing (I2).
/// </summary>
public sealed record SettingResolution(
    string? Value,
    AppSettingScope Provenance,
    bool IsDefault,
    string? RejectedReason);
