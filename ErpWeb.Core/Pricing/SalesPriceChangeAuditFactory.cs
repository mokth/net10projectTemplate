using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Core.Pricing;

public static class SalesPriceChangeAuditOrigins
{
    public const string PriceReviewWorkbench = "PRICE_REVIEW_WORKBENCH";
    public const string ItemMaster = "ITEM_MASTER";
    public const string PriceListMaster = "PRICE_LIST_MASTER";
    public const string CustomerItemMaster = "CUSTOMER_ITEM_MASTER";
}

public static class SalesPriceChangeAuditTargets
{
    public const string ItemDefault = "ITEM_DEFAULT";
    public const string PriceList = "PRICE_LIST";
    public const string CustomerItem = "CUSTOMER_ITEM";
}

public static class SalesPriceChangeKinds
{
    public const string Create = "CREATE";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
    public const string Schedule = "SCHEDULE";
}

public static class SalesPriceChangeAuditAdjustmentMethods
{
    public const string DirectEdit = "DIRECT_EDIT";
    public const string Delete = "DELETE";
}

public sealed record SalesPriceChangeAuditContext(
    string CompanyCode,
    string Origin,
    string TargetType,
    DateTime EffectiveDate,
    string ChangedBy,
    string? AdjustmentMethod = null,
    decimal? AdjustmentValue = null,
    string? RoundingMode = null,
    int? DecimalPlaces = null,
    string? Reason = null,
    string? ItemSearchFilter = null,
    string? ItemTypeFilter = null,
    string? ItemClassFilter = null,
    string? ItemSubClassFilter = null,
    string? BrandFilter = null,
    string? CustCodeFilter = null,
    string? CustTypeFilter = null,
    string? CustGroupFilter = null,
    string? CustPriceCodeFilter = null,
    string? CurrencyFilter = null,
    string? UomFilter = null,
    DateTime? ChangedAtUtc = null);

public sealed record SalesPriceChangeAuditLine(
    string ChangeKind,
    string ItemCode,
    string? ItemDescriptionSnapshot = null,
    string? ItemTypeSnapshot = null,
    string? ItemClassSnapshot = null,
    string? ItemSubClassSnapshot = null,
    string? BrandSnapshot = null,
    string? CustCode = null,
    string? CustomerNameSnapshot = null,
    string? CustomerTypeSnapshot = null,
    string? CustomerGroupSnapshot = null,
    string? CustPriceCode = null,
    string? PriceListDescriptionSnapshot = null,
    int? SourcePriceListLineId = null,
    int? Moq = null,
    string? OldUom = null,
    string? NewUom = null,
    string? OldCurrencyCode = null,
    string? NewCurrencyCode = null,
    decimal? OldMinQty = null,
    decimal? OldMaxQty = null,
    decimal? NewMinQty = null,
    decimal? NewMaxQty = null,
    DateTime? OldValidFrom = null,
    DateTime? OldValidTo = null,
    DateTime? NewValidFrom = null,
    DateTime? NewValidTo = null,
    decimal? OldPrice = null,
    decimal? NewPrice = null);

/// <summary>
/// Pure audit graph builder. It never owns a DbContext, transaction, or SaveChanges call; the caller
/// adds the returned graph to its existing transaction so master and history commit or roll back together.
/// </summary>
public static class SalesPriceChangeAuditFactory
{
    public static SaPriceChangeBatch BuildBatch(
        SalesPriceChangeAuditContext context,
        IEnumerable<SalesPriceChangeAuditLine> lines)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);

        var batch = new SaPriceChangeBatch
        {
            CompanyCode = NormalizeRequired(context.CompanyCode),
            Origin = NormalizeRequired(context.Origin),
            TargetType = NormalizeRequired(context.TargetType),
            AdjustmentMethod = NormalizeToken(context.AdjustmentMethod),
            AdjustmentValue = context.AdjustmentValue,
            RoundingMode = NormalizeToken(context.RoundingMode),
            DecimalPlaces = context.DecimalPlaces,
            EffectiveDate = context.EffectiveDate.Date,
            Reason = NormalizeOptional(context.Reason),
            ItemSearchFilter = NormalizeOptional(context.ItemSearchFilter),
            ItemTypeFilter = NormalizeOptional(context.ItemTypeFilter),
            ItemClassFilter = NormalizeOptional(context.ItemClassFilter),
            ItemSubClassFilter = NormalizeOptional(context.ItemSubClassFilter),
            BrandFilter = NormalizeOptional(context.BrandFilter),
            CustCodeFilter = NormalizeOptional(context.CustCodeFilter),
            CustTypeFilter = NormalizeOptional(context.CustTypeFilter),
            CustGroupFilter = NormalizeOptional(context.CustGroupFilter),
            CustPriceCodeFilter = NormalizeOptional(context.CustPriceCodeFilter),
            CurrencyFilter = NormalizeOptional(context.CurrencyFilter),
            UomFilter = NormalizeOptional(context.UomFilter),
            ChangedAtUtc = context.ChangedAtUtc ?? DateTime.UtcNow,
            ChangedBy = NormalizeValue(context.ChangedBy)
        };

        foreach (var input in lines)
        {
            batch.Lines.Add(new SaPriceChangeLine
            {
                ChangeKind = NormalizeRequired(input.ChangeKind),
                ItemCode = NormalizeValue(input.ItemCode),
                ItemDescriptionSnapshot = NormalizeOptional(input.ItemDescriptionSnapshot),
                ItemTypeSnapshot = NormalizeOptional(input.ItemTypeSnapshot),
                ItemClassSnapshot = NormalizeOptional(input.ItemClassSnapshot),
                ItemSubClassSnapshot = NormalizeOptional(input.ItemSubClassSnapshot),
                BrandSnapshot = NormalizeOptional(input.BrandSnapshot),
                CustCode = NormalizeOptional(input.CustCode),
                CustomerNameSnapshot = NormalizeOptional(input.CustomerNameSnapshot),
                CustomerTypeSnapshot = NormalizeOptional(input.CustomerTypeSnapshot),
                CustomerGroupSnapshot = NormalizeOptional(input.CustomerGroupSnapshot),
                CustPriceCode = NormalizeOptional(input.CustPriceCode),
                PriceListDescriptionSnapshot = NormalizeOptional(input.PriceListDescriptionSnapshot),
                SourcePriceListLineId = input.SourcePriceListLineId,
                Moq = input.Moq,
                OldUom = NormalizeOptional(input.OldUom),
                NewUom = NormalizeOptional(input.NewUom),
                OldCurrencyCode = NormalizeOptional(input.OldCurrencyCode),
                NewCurrencyCode = NormalizeOptional(input.NewCurrencyCode),
                OldMinQty = input.OldMinQty,
                OldMaxQty = input.OldMaxQty,
                NewMinQty = input.NewMinQty,
                NewMaxQty = input.NewMaxQty,
                OldValidFrom = input.OldValidFrom?.Date,
                OldValidTo = input.OldValidTo?.Date,
                NewValidFrom = input.NewValidFrom?.Date,
                NewValidTo = input.NewValidTo?.Date,
                OldPrice = input.OldPrice,
                NewPrice = input.NewPrice
            });
        }

        batch.ChangedRowCount = batch.Lines.Count;
        return batch;
    }

    public static decimal? NormalizeLegacyMoney(double? value) =>
        value is null
            ? null
            : decimal.Round((decimal)value.Value, 4, MidpointRounding.AwayFromZero);

    public static double? UnscaleLegacyMoney(decimal? value) =>
        value is null
            ? null
            : (double)decimal.Round(value.Value, 4, MidpointRounding.AwayFromZero);

    public static bool MoneyEquals(double? left, decimal? right) =>
        NormalizeLegacyMoney(left) == right;

    private static string NormalizeRequired(string value) =>
        NormalizeToken(value) ?? string.Empty;

    private static string NormalizeValue(string value) =>
        (value ?? string.Empty).Trim();

    private static string? NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
