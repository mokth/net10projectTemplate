using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public interface IProductionMaterialConsumeVarianceInquiryService
{
    Task<IvMasterOperationResult<ProductionMaterialConsumeVariancePage>> SearchDetailAsync(
        ProductionMaterialConsumeVarianceQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialConsumeVarianceSummaries>> SearchSummariesAsync(
        ProductionMaterialConsumeVarianceQuery query, CancellationToken cancellationToken = default);
}

public static class ProductionMaterialVarianceDirections
{
    public const string Any = "ANY";
    public const string Over = "OVER";
    public const string Under = "UNDER";
    public const string Exact = "EXACT";
}

public sealed class ProductionMaterialConsumeVarianceQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? ProductCode { get; set; }
    public string? ComponentCode { get; set; }
    public string? WorkCentreCode { get; set; }
    public string? OperationCode { get; set; }
    public string VarianceDirection { get; set; } = ProductionMaterialVarianceDirections.Any;
    public string? VarianceReasonCode { get; set; }
    public string? DocumentStatus { get; set; }
    public string? PostedBy { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class ProductionMaterialConsumeVariancePage
{
    public IReadOnlyList<ProductionMaterialConsumeVarianceRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class ProductionMaterialConsumeVarianceRow
{
    public long ProductionOutputId { get; init; }
    public string DocumentNo { get; init; } = string.Empty;
    public DateTime ProductionDate { get; init; }
    public string DocumentStatus { get; init; } = string.Empty;
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string WorkCentreCode { get; init; } = string.Empty;
    public string OperationCode { get; init; } = string.Empty;
    public long? WorkOrderMaterialId { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string SupplySource { get; init; } = string.Empty;
    public string IssueMethod { get; init; } = string.Empty;
    public string RequiredUom { get; init; } = string.Empty;
    public decimal WoBomRequiredQty { get; init; }
    public decimal StandardQty { get; init; }
    public decimal ConsumeQty { get; init; }
    public decimal VarianceQty { get; init; }
    public decimal? VariancePct { get; init; }
    public decimal TolerancePercent { get; init; }
    public string? VarianceReasonCode { get; init; }
    public string? VarianceReasonText { get; init; }
    public string? PostedBy { get; init; }
}

public sealed class ProductionMaterialConsumeVarianceSummaries
{
    public IReadOnlyList<ProductionMaterialConsumeVarianceByComponent> ByComponent { get; init; } = [];
    public IReadOnlyList<ProductionMaterialConsumeVarianceByReason> ByReason { get; init; } = [];
    public IReadOnlyList<ProductionMaterialConsumeVarianceByReasonComponent> ByReasonComponent { get; init; } = [];
    public IReadOnlyList<ProductionMaterialConsumeVarianceByWorkOrder> ByWorkOrder { get; init; } = [];
    public IReadOnlyList<ProductionMaterialConsumeVarianceByWorkOrderComponent> ByWorkOrderComponent { get; init; } = [];
}

public sealed class ProductionMaterialConsumeVarianceByComponent
{
    public string ComponentCode { get; init; } = string.Empty;
    public string RequiredUom { get; init; } = string.Empty;
    public decimal TotalStandardQty { get; init; }
    public decimal TotalConsumeQty { get; init; }
    public decimal TotalVarianceQty { get; init; }
    public int OverDocumentCount { get; init; }
    public int UnderDocumentCount { get; init; }
    public int ExactDocumentCount { get; init; }
}

public sealed class ProductionMaterialConsumeVarianceByReason
{
    public string? VarianceReasonCode { get; init; }
    public int DocumentLineCount { get; init; }
    public int OverLineCount { get; init; }
    public int UnderLineCount { get; init; }
    public int ExactLineCount { get; init; }
    public int AffectedWorkOrderCount { get; init; }
    public int AffectedComponentCount { get; init; }
}

public sealed class ProductionMaterialConsumeVarianceByReasonComponent
{
    public string? VarianceReasonCode { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string RequiredUom { get; init; } = string.Empty;
    public decimal TotalVarianceQty { get; init; }
    public decimal TotalAbsoluteVarianceQty { get; init; }
}

public sealed class ProductionMaterialConsumeVarianceByWorkOrder
{
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public int VarianceLineCount { get; init; }
    public int OverLineCount { get; init; }
    public int UnderLineCount { get; init; }
    public int ExactLineCount { get; init; }
    public int AffectedComponentCount { get; init; }
}

public sealed class ProductionMaterialConsumeVarianceByWorkOrderComponent
{
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ComponentCode { get; init; } = string.Empty;
    public string RequiredUom { get; init; } = string.Empty;
    public decimal TotalStandardQty { get; init; }
    public decimal TotalConsumeQty { get; init; }
    public decimal TotalVarianceQty { get; init; }
}
