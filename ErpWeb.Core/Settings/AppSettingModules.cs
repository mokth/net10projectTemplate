namespace ErpWeb.Core.Settings;

/// <summary>
/// The modules a setting can belong to. Deliberately just strings rather than an enum: a module is a
/// grouping label for the admin screen and a prefix on the lookup key, and adding one (Planning,
/// Production, QA) must cost no DDL and no migration.
///
/// <para>
/// <see cref="All"/> is the authority for "is this a real module" — both the admin screen and the
/// catalogue integrity test read it, so a typo in a definition fails a test rather than producing a
/// tab nobody ever sees.
/// </para>
/// </summary>
public static class AppSettingModules
{
    public const string Admin = "ADMIN";
    public const string Inventory = "INVENTORY";
    public const string Sales = "SALES";
    public const string Procurement = "PROCUREMENT";

    /// <summary>Reserved: no live setting yet, declared so the module is a valid target today.</summary>
    public const string Planning = "PLANNING";

    /// <summary>Reserved: no live setting yet, declared so the module is a valid target today.</summary>
    public const string Production = "PRODUCTION";

    /// <summary>Reserved: no live setting yet, declared so the module is a valid target today.</summary>
    public const string Qa = "QA";

    /// <summary>Every module, in the order the admin screen shows them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Admin, Sales, Procurement, Inventory, Planning, Production, Qa
    ];

    public static bool IsKnown(string? module) =>
        !string.IsNullOrWhiteSpace(module)
        && All.Contains(module.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Human label for a tab header. Falls back to the raw token so an unknown module is visible, not blank.</summary>
    public static string Describe(string? module) => (module ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        Admin => "Admin",
        Sales => "Sales",
        Procurement => "Procurement",
        Inventory => "Inventory",
        Planning => "Planning",
        Production => "Production",
        Qa => "Quality Assurance",
        var other => other
    };
}
