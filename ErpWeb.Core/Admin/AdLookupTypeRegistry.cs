using ErpWeb.Core.Menus;

namespace ErpWeb.Core.Admin;

/// <summary>One seeded default value for a lookup family.</summary>
public sealed record AdLookupSeedValue(string Code, string Description);

/// <summary>
/// Metadata for one supported lookup family. This controls display, grouping, the real consumer-column
/// width and the initial defaults — it must never carry business workflow behaviour.
///
/// <para>
/// <see cref="MaintenanceMenuCode"/> and <see cref="Route"/> are the server-owned placement of the
/// family's maintenance screen. They are mandatory for a maintainable family: authorization fails
/// closed when the menu code is missing, so a half-registered family can never fall back to the
/// all-family <c>ADMIN_LOOKUPS</c> grant.
/// </para>
/// </summary>
public sealed record AdLookupTypeDefinition(
    string Code,
    string DisplayName,
    string Module,
    int MaxCodeLength,
    bool UserMaintainable,
    IReadOnlyList<AdLookupSeedValue>? DefaultValues = null,
    string? MaintenanceMenuCode = null,
    string? Route = null);

/// <summary>
/// The approved lookup families with their real consumer-column limits (plan §3.2).
///
/// <para>
/// <b>MaxCodeLength is the assignment limit</b>, derived from the column that stores the code on the
/// owning master. The physical <c>AdLookupValue.Code</c> column is wider (50) so a value can never be
/// truncated on assignment: it is simply refused at lookup-master save time.
/// </para>
/// </summary>
public static class AdLookupTypeRegistry
{
    public static IReadOnlyList<AdLookupTypeDefinition> All { get; } =
    [
        new AdLookupTypeDefinition(
            AdLookupTypes.WarehouseType,
            "Warehouse Type",
            "Inventory",
            MaxCodeLength: 20,
            UserMaintainable: true,
            DefaultValues:
            [
                new AdLookupSeedValue("GENERAL", "General Warehouse"),
                new AdLookupSeedValue("RAW_MATERIAL", "Raw Material"),
                new AdLookupSeedValue("FINISHED_GOODS", "Finished Goods"),
                new AdLookupSeedValue("QUARANTINE", "Quarantine"),
                new AdLookupSeedValue("RETURN", "Return"),
                new AdLookupSeedValue("TRANSIT", "Transit"),
                new AdLookupSeedValue("CONSIGNMENT", "Consignment")
            ],
            MaintenanceMenuCode: MenuCodes.InventoryWarehouseType,
            Route: "/inventory/lookups/WAREHOUSE_TYPE"),

        // No hard-coded business list: existing distinct MachineType values are backfilled by the DBA
        // script, and the company maintains its own values afterwards.
        new AdLookupTypeDefinition(
            AdLookupTypes.MachineType,
            "Machine Type",
            "Planning",
            MaxCodeLength: 30,
            UserMaintainable: true,
            MaintenanceMenuCode: MenuCodes.PlanningMachineType,
            Route: "/planning/lookups/MACHINE_TYPE"),

        new AdLookupTypeDefinition(
            AdLookupTypes.WorkCentreClass,
            "Work Centre Class",
            "Planning",
            MaxCodeLength: 10,
            UserMaintainable: true,
            MaintenanceMenuCode: MenuCodes.PlanningWorkCentreClass,
            Route: "/planning/lookups/WORK_CENTRE_CLASS"),

        new AdLookupTypeDefinition(
            AdLookupTypes.SupplierType,
            "Supplier Type",
            "Purchase",
            MaxCodeLength: 20,
            UserMaintainable: true,
            MaintenanceMenuCode: MenuCodes.PurchaseSupplierType,
            Route: "/purchase/lookups/SUPPLIER_TYPE"),

        new AdLookupTypeDefinition(
            AdLookupTypes.SupplierSubGroup,
            "Supplier Subgroup",
            "Purchase",
            MaxCodeLength: 20,
            UserMaintainable: true,
            MaintenanceMenuCode: MenuCodes.PurchaseSupplierSubGroup,
            Route: "/purchase/lookups/SUPPLIER_SUBGROUP"),

        // Metadata only in this deployment — the customer switch is a separate, separately approved
        // migration (plan §15). Not seeded, not listed by /admin/lookups.
        new AdLookupTypeDefinition(
            AdLookupTypes.CustomerIndustry,
            "Customer Industry (reserved)",
            "Sales",
            MaxCodeLength: 20,
            UserMaintainable: false),

        new AdLookupTypeDefinition(
            AdLookupTypes.CustomerChannel,
            "Customer Channel (reserved)",
            "Sales",
            MaxCodeLength: 20,
            UserMaintainable: false),

        new AdLookupTypeDefinition(
            AdLookupTypes.CustomerSource,
            "Customer Source (reserved)",
            "Sales",
            MaxCodeLength: 20,
            UserMaintainable: false)
    ];

    private static readonly Dictionary<string, AdLookupTypeDefinition> ByCode =
        All.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Families a user may maintain from the lookup master screen.</summary>
    public static IReadOnlyList<AdLookupTypeDefinition> Maintainable { get; } =
        All.Where(x => x.UserMaintainable).ToList();

    public static bool IsKnown(string? code) =>
        !string.IsNullOrWhiteSpace(code) && ByCode.ContainsKey(code.Trim());

    public static bool TryGet(string? code, out AdLookupTypeDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            definition = null!;
            return false;
        }

        return ByCode.TryGetValue(code.Trim(), out definition!);
    }

    public static AdLookupTypeDefinition? Find(string? code) =>
        TryGet(code, out var definition) ? definition : null;
}
