using ErpWeb.Core.Pricing;

namespace ErpWeb.Core.Sales;

public static class SaPriceMaintenanceTargets
{
    public const string ItemDefault = SalesPriceChangeAuditTargets.ItemDefault;
    public const string PriceList = SalesPriceChangeAuditTargets.PriceList;
    public const string CustomerItem = SalesPriceChangeAuditTargets.CustomerItem;

    public static bool IsSupported(string? value) =>
        string.Equals(value, ItemDefault, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, PriceList, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, CustomerItem, StringComparison.OrdinalIgnoreCase);
}

public static class SaPriceListUpdateModes
{
    public const string UpdateSelectedRow = "UPDATE_SELECTED_ROW";
    public const string ScheduleFromDate = "SCHEDULE_FROM_DATE";
}

public static class SaPriceReviewStatuses
{
    public const string Ready = "READY";
    public const string Unchanged = "UNCHANGED";
    public const string Blocked = "BLOCKED";
}

public static class SaPriceMaintenanceLimits
{
    public const int MaxReviewRows = 5_000;
    public const int MaxReasonLength = 200;
}

public static class SaPriceReviewWorkbookLimits
{
    public const string TemplateVersion = "SA_PRICE_REVIEW_V1";
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxRows = SaPriceMaintenanceLimits.MaxReviewRows;
}

public sealed class SaPriceReviewQuery
{
    public string TargetType { get; set; } = SaPriceMaintenanceTargets.ItemDefault;

    public string? ItemCode { get; set; }
    public string? ItemSearch { get; set; }
    public string? ItemType { get; set; }
    public string? ItemClass { get; set; }
    public string? ItemSubClass { get; set; }
    public string? Brand { get; set; }
    public bool ActiveItemsOnly { get; set; } = true;
    public bool LoadAllActiveItems { get; set; }

    public string? CustPriceCode { get; set; }
    public string? CustGroupCode { get; set; }
    public string? Uom { get; set; }
    public string? CurrencyCode { get; set; }
    public DateTime? ReviewAsOf { get; set; }

    public string? CustCode { get; set; }
    public string? CustType { get; set; }
    public string? CustGroup { get; set; }
    public bool ActiveCustomersOnly { get; set; } = true;
    public int? Moq { get; set; }

    public int Skip { get; set; }
    public int Take { get; set; } = SaPriceMaintenanceLimits.MaxReviewRows;
}

public sealed class SaPriceReviewPage
{
    public string TargetType { get; init; } = string.Empty;
    public IReadOnlyList<SaPriceReviewRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaPriceReviewRow
{
    public string ReviewRowKey { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;

    public string ItemCode { get; init; } = string.Empty;
    public string? ItemDescription { get; init; }
    public string? ItemType { get; init; }
    public string? ItemClass { get; init; }
    public string? ItemSubClass { get; init; }
    public string? Brand { get; init; }
    public string? Uom { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal? ProposedPrice { get; set; }
    public decimal? DifferenceAmount { get; set; }
    public decimal? DifferencePercent { get; set; }
    public string Status { get; set; } = SaPriceReviewStatuses.Ready;
    public string? Warning { get; set; }
    public bool Selected { get; set; }

    public string? CustPriceCode { get; init; }
    public string? PriceListDescription { get; init; }
    public int? PriceListLineId { get; init; }
    public decimal? MinQty { get; init; }
    public decimal? MaxQty { get; init; }
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public string? CurrencyCode { get; init; }

    public string? CustCode { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerType { get; init; }
    public string? CustomerGroup { get; init; }
    public int? Moq { get; init; }

    public byte[] RowVersion { get; init; } = [];
    public byte[] HeaderRowVersion { get; init; } = [];
}

public sealed class SaPriceReviewSelection
{
    public string ReviewRowKey { get; set; } = string.Empty;
    public decimal? BaselinePrice { get; set; }
    public decimal? NewPrice { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public byte[] HeaderRowVersion { get; set; } = [];
}

public abstract class SaPriceChangeRequestBase
{
    public string TargetType { get; set; } = string.Empty;
    public string AdjustmentMethod { get; set; } = SaPriceAdjustmentMethods.SetPrice;
    public decimal AdjustmentValue { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    public string RoundingMode { get; set; } = SaPriceRoundingModes.Normal;
    public string PriceListUpdateMode { get; set; } = SaPriceListUpdateModes.UpdateSelectedRow;
    public DateTime? EffectiveFrom { get; set; }
    public string? Reason { get; set; }
    public SaPriceReviewQuery? ReviewScope { get; set; }
    public IReadOnlyList<SaPriceReviewSelection> Selections { get; set; } = [];
}

public sealed class SaPricePreviewRequest : SaPriceChangeRequestBase
{
}

public sealed class SaPriceApplyRequest : SaPriceChangeRequestBase
{
}

public sealed class SaPricePreviewResult
{
    public SaPricePreviewSummary Summary { get; init; } = new();
    public IReadOnlyList<SaPriceReviewRow> Rows { get; init; } = [];
}

public sealed class SaPricePreviewSummary
{
    public int Matched { get; init; }
    public int Selected { get; init; }
    public int Changing { get; init; }
    public int Unchanged { get; init; }
    public int Blocked { get; init; }
    public int ItemsAffected { get; init; }
    public int CustomersAffected { get; init; }
    public int PriceListsAffected { get; init; }
    public decimal? MinimumDifference { get; init; }
    public decimal? MaximumDifference { get; init; }
    public decimal? AveragePercentChange { get; init; }
}

public sealed class SaPriceApplyResult
{
    public long? PriceChangeBatchId { get; init; }
    public string? BatchReference { get; init; }
    public int ChangedRowCount { get; init; }
}

public sealed class SaPriceReviewExportRequest
{
    public SaPriceReviewQuery Query { get; set; } = new();
    public string AdjustmentMethod { get; set; } = SaPriceAdjustmentMethods.SetPrice;
    public decimal AdjustmentValue { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    public string RoundingMode { get; set; } = SaPriceRoundingModes.Normal;
}

public sealed class SaPriceImportContext
{
    public string TargetType { get; set; } = string.Empty;
    public IReadOnlyList<SaPriceReviewRow> StagedRows { get; set; } = [];
}

public sealed class SaPriceImportRow
{
    public int RowNumber { get; init; }
    public string ReviewRowKey { get; init; } = string.Empty;
    public decimal? NewPrice { get; init; }
    public bool Selected { get; init; }
    public string? Error { get; init; }
}

public sealed class SaPriceImportPreview
{
    public bool IsValid { get; init; }
    public string? TemplateVersion { get; init; }
    public string? TargetType { get; init; }
    public int AcceptedRowCount { get; init; }
    public IReadOnlyList<SaPriceImportRow> Rows { get; init; } = [];
    public IReadOnlyList<SaPriceImportRow> Updates { get; init; } = [];
}
