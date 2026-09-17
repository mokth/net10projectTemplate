using ErpWeb.Core.Sales;

namespace ErpWeb.Core.Settings;

/// <summary>
/// The authority for which settings exist. A key that is not here is not a setting: a row in
/// <c>dbo.AdSmParam</c> carrying it is ignored on read and reported only as a diagnostic.
///
/// <para>
/// Adding a setting means adding a definition. It does NOT mean DDL, a migration or a new column —
/// which is the whole point of this feature.
/// </para>
///
/// <para>
/// <see cref="All"/> must contain no duplicate <c>(Module, Key)</c> pair, must declare at least one
/// allowed token for every <see cref="AppSettingType.Token"/> definition, and must have a default that
/// parses as its own type. All three are asserted by <c>AppSettingCatalogueTests</c>.
/// </para>
/// </summary>
public static class AppSettingCatalogue
{
    /// <summary>Key constants, grouped by module so a call site reads as <c>AppSettingKeys.Sales.PriceMethod</c>.</summary>
    public static class SalesKeys
    {
        public const string PriceMethod = "PRICE_METHOD";
        public const string AllowBelowCost = "ALLOW_BELOW_COST";
        public const string QuoteValidDays = "QUOTE_VALID_DAYS";
    }

    public static class InventoryKeys
    {
        public const string AllowNegativeStock = "ALLOW_NEGATIVE_STOCK";
        public const string QtyDecimals = "QTY_DECIMALS";
        public const string RequireLotOnReceipt = "REQUIRE_LOT_ON_RECEIPT";
    }

    public static class ProcurementKeys
    {
        public const string AllowOverReceipt = "ALLOW_OVER_RECEIPT";
        public const string RequirePrApproval = "REQUIRE_PR_APPROVAL";
    }

    public static class AdminKeys
    {
        public const string SessionTimeoutMinutes = "SESSION_TIMEOUT_MINUTES";
    }

    public static class PlanningKeys
    {
        public const string AllowCapacityOverride = "ALLOW_CAPACITY_OVERRIDE";
    }

    public static class ProductionKeys
    {
        public const string AllowPartialCompletion = "ALLOW_PARTIAL_COMPLETION";
    }

    public static class QaKeys
    {
        public const string RequireInspectionOnReceipt = "REQUIRE_INSPECTION_ON_RECEIPT";
    }

    /// <summary>
    /// The sales pricing method, projected read-only over the existing <c>Company.SalesPriceMethod</c>
    /// column. Company only — <c>BranchCode</c> must never enter a pricing key (sales-price-engine.md §5.9).
    /// The token list is <c>SaCompanyPriceMethod.All</c> and the canonicaliser is its <c>Normalize</c>, so
    /// the projection can never disagree with the engine.
    /// </summary>
    public static readonly AppSettingDefinition SalesPriceMethod = new(
        AppSettingModules.Sales,
        SalesKeys.PriceMethod,
        AppSettingType.Token,
        AppSettingScope.Company,
        SaCompanyPriceMethod.CustomerItemAndList,
        SaCompanyPriceMethod.All,
        AppSettingBacking.ExistingColumn,
        m => SaCompanyPriceMethod.Normalize(m),
        "Which price sources may apply to a sales line. The evaluation order is fixed by specificity and "
        + "is not configurable. Live value lives on the company screen.");

    public static readonly AppSettingDefinition SalesAllowBelowCost = new(
        AppSettingModules.Sales,
        SalesKeys.AllowBelowCost,
        AppSettingType.Flag,
        AppSettingScope.Company | AppSettingScope.Branch,
        "false",
        Description: "Permit a sales line price below the item's cost. Default denies.");

    public static readonly AppSettingDefinition SalesQuoteValidDays = new(
        AppSettingModules.Sales,
        SalesKeys.QuoteValidDays,
        AppSettingType.Number,
        AppSettingScope.Company,
        "30",
        Description: "Default validity, in days, applied to a new quotation.");

    public static readonly AppSettingDefinition InventoryAllowNegativeStock = new(
        AppSettingModules.Inventory,
        InventoryKeys.AllowNegativeStock,
        AppSettingType.Flag,
        AppSettingScope.Company | AppSettingScope.Branch,
        "false",
        Description: "Permit a stock movement to take an on-hand balance below zero. Not yet wired.");

    public static readonly AppSettingDefinition InventoryQtyDecimals = new(
        AppSettingModules.Inventory,
        InventoryKeys.QtyDecimals,
        AppSettingType.Number,
        AppSettingScope.Company,
        "4",
        Description: "Decimal places used when displaying quantities. Storage stays at full precision.");

    public static readonly AppSettingDefinition InventoryRequireLotOnReceipt = new(
        AppSettingModules.Inventory,
        InventoryKeys.RequireLotOnReceipt,
        AppSettingType.Flag,
        AppSettingScope.Company,
        "false",
        Description: "Require a lot number on every receipt line for a lot-controlled item. Not yet wired.");

    public static readonly AppSettingDefinition ProcurementAllowOverReceipt = new(
        AppSettingModules.Procurement,
        ProcurementKeys.AllowOverReceipt,
        AppSettingType.Flag,
        AppSettingScope.Company,
        "false",
        Description: "Permit a goods receipt above the ordered quantity within the item tolerance.");

    public static readonly AppSettingDefinition ProcurementRequirePrApproval = new(
        AppSettingModules.Procurement,
        ProcurementKeys.RequirePrApproval,
        AppSettingType.Flag,
        AppSettingScope.Company,
        "true",
        Description: "Require a purchase requisition to be approved before it can be ordered. Not yet wired.");

    public static readonly AppSettingDefinition AdminSessionTimeoutMinutes = new(
        AppSettingModules.Admin,
        AdminKeys.SessionTimeoutMinutes,
        AppSettingType.Number,
        AppSettingScope.Global,
        "60",
        Description: "Idle minutes before a signed-in session expires. Not yet wired.");

    public static readonly AppSettingDefinition PlanningAllowCapacityOverride = new(
        AppSettingModules.Planning,
        PlanningKeys.AllowCapacityOverride,
        AppSettingType.Flag,
        AppSettingScope.Company,
        "false",
        Description: "Reserved for the planning module.");

    public static readonly AppSettingDefinition ProductionAllowPartialCompletion = new(
        AppSettingModules.Production,
        ProductionKeys.AllowPartialCompletion,
        AppSettingType.Flag,
        AppSettingScope.Company,
        "false",
        Description: "Reserved for the production module.");

    public static readonly AppSettingDefinition QaRequireInspectionOnReceipt = new(
        AppSettingModules.Qa,
        QaKeys.RequireInspectionOnReceipt,
        AppSettingType.Flag,
        AppSettingScope.Company,
        "false",
        Description: "Reserved for the quality-assurance module.");

    /// <summary>Every definition, in module order then declaration order.</summary>
    public static readonly IReadOnlyList<AppSettingDefinition> All =
    [
        AdminSessionTimeoutMinutes,

        SalesPriceMethod,
        SalesAllowBelowCost,
        SalesQuoteValidDays,

        ProcurementAllowOverReceipt,
        ProcurementRequirePrApproval,

        InventoryAllowNegativeStock,
        InventoryQtyDecimals,
        InventoryRequireLotOnReceipt,

        PlanningAllowCapacityOverride,
        ProductionAllowPartialCompletion,
        QaRequireInspectionOnReceipt
    ];

    private static readonly IReadOnlyDictionary<string, AppSettingDefinition> ByKey =
        All.ToDictionary(d => d.LookupKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The ONLY way a key becomes a setting. Case-insensitive so a persisted casing can never miss.
    /// </summary>
    public static AppSettingDefinition? Find(string? module, string? key)
    {
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return ByKey.TryGetValue(AppSettingDefinition.BuildLookupKey(module, key), out var definition)
            ? definition
            : null;
    }

    /// <summary>Definitions for one module, in declaration order. Never derived from the database.</summary>
    public static IReadOnlyList<AppSettingDefinition> ForModule(string? module)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            return [];
        }

        var trimmed = module.Trim();
        return All.Where(d => string.Equals(d.Module, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Every definition backed by an existing column — each one needs a registered provider.</summary>
    public static IReadOnlyList<AppSettingDefinition> ColumnBacked { get; } =
        All.Where(d => d.Backing == AppSettingBacking.ExistingColumn).ToList();
}
