using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

public interface IProductionOutputService
{
    Task<IvMasterOperationResult<ProductionOutputDetail>> CreateAsync(
        ProductionOutputCreateRequest request, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputDetail>> UpdateAsync(
        ProductionOutputUpdateRequest request, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<bool>> DeleteAsync(long outputId, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputDetail>> PostAsync(
        long outputId, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputDetail>> RollbackAsync(
        ProductionOutputRollbackRequest request, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputDetail>> GetAsync(
        long outputId, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputSearchPage>> SearchAsync(
        ProductionOutputSearchQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionEligibleOperationPage>> SearchEligibleOperationsAsync(
        ProductionEligibleOperationQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionEligibleOperationFilterOptions>> GetEligibleOperationFilterOptionsAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputEntryLookups>> GetEntryLookupsAsync(
        long workOrderOperationId, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputWorkspace>> GetWorkspaceAsync(
        long workOrderOperationId, DateTime? productionDate = null, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputWorkspace>> GetDocumentWorkspaceAsync(
        long outputId, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionSharedWipSourceInquiry>> GetSharedWipSourceInquiryAsync(
        ProductionSharedWipSourceInquiryRequest request, CancellationToken cancellationToken = default);
}

public class ProductionOutputCreateRequest
{
    public long WorkOrderOperationId { get; set; }
    public string PostingRequestId { get; set; } = string.Empty;
    public DateTime ProductionDate { get; set; }
    public string? ShiftCode { get; set; }
    public string? ActualMachineCode { get; set; }
    public string? OperatorCode { get; set; }
    public decimal GoodQty { get; set; }
    public decimal ScrapQty { get; set; }
    public decimal RejectQty { get; set; }
    public decimal HoldQty { get; set; }
    public string OutputLotNo { get; set; } = string.Empty;
    public IReadOnlyList<ProductionOutputMaterialInput> Materials { get; set; } = [];
}

public sealed class ProductionOutputMaterialInput
{
    public long WorkOrderMaterialId { get; set; }
    public decimal ConsumeQty { get; set; }
    public string? VarianceReasonCode { get; set; }
    public string? VarianceReasonText { get; set; }
}

public sealed class ProductionOutputUpdateRequest : ProductionOutputCreateRequest
{
    public long OutputId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ProductionOutputRollbackRequest
{
    public long OutputId { get; set; }
    public string PostingRequestId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProductionOutputSearchQuery
{
    public string? SearchText { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? Status { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class ProductionEligibleOperationQuery
{
    public string? WorkOrderNo { get; set; }
    public string? ProductCode { get; set; }
    public string? WorkCentreCode { get; set; }
    public string? OperationCode { get; set; }
    public string? OutputItemCode { get; set; }
    public string? MachineCode { get; set; }
    public string? RawMaterialCode { get; set; }
    public bool ExactMatch { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed record ProductionOutputDetail
{
    public long Uid { get; init; }
    public string DocumentNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long WorkOrderId { get; init; }
    public string WipConsumptionPolicy { get; init; } = ProductionWipConsumptionPolicies.Strict;
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public long RouteStepId { get; init; }
    public long WorkOrderOperationId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationDescription { get; init; }
    public string WorkCentreCode { get; init; } = string.Empty;
    public string? WorkOrderStatus { get; init; }
    public string? PlannedMachineCode { get; init; }
    public string? PlannedMachineDescription { get; init; }
    public DateTime ProductionDate { get; init; }
    public string? ShiftCode { get; init; }
    public string? ActualMachineCode { get; init; }
    public string? OperatorCode { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public decimal RejectQty { get; init; }
    public decimal HoldQty { get; init; }
    public string OutputUom { get; init; } = string.Empty;
    public string OutputItemCode { get; init; } = string.Empty;
    public string? OutputType { get; init; }
    public string OutputLotNo { get; init; } = string.Empty;
    public string PostingRequestId { get; init; } = string.Empty;
    public DateTime? PostedDate { get; init; }
    public string? PostedBy { get; init; }
    public DateTime? ReversedDate { get; init; }
    public string? ReversedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<ProductionOutputMaterialLine> Materials { get; init; } = [];
}

public sealed class ProductionOutputSearchPage
{
    public IReadOnlyList<ProductionOutputListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class ProductionOutputListRow
{
    public long Uid { get; init; }
    public string DocumentNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string WipConsumptionPolicy { get; init; } = ProductionWipConsumptionPolicies.Strict;
    public string WorkOrderNo { get; init; } = string.Empty;
    public string OperationCode { get; init; } = string.Empty;
    public DateTime ProductionDate { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public decimal RejectQty { get; init; }
    public string OutputItemCode { get; init; } = string.Empty;
    public string OutputLotNo { get; init; } = string.Empty;
}

public sealed class ProductionEligibleOperationRow
{
    public long WorkOrderOperationId { get; init; }
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string WorkCentreCode { get; init; } = string.Empty;
    public int StageSequence { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationDescription { get; init; }
    public int ProcessSequence { get; init; }
    public string? WorkOrderStatus { get; init; }
    public string OutputItemCode { get; init; } = string.Empty;
    public string? SelectedMachineCode { get; init; }
    public string? SelectedMachineDescription { get; init; }
    public decimal PlannedOutputQty { get; init; }
    public decimal GoodQty { get; init; }
    public decimal RemainingQty { get; init; }
    public string? OutputUom { get; init; }
    public bool IsFinalOperation { get; init; }
    public string? OutputType { get; init; }
}

public sealed class ProductionEligibleOperationPage
{
    public IReadOnlyList<ProductionEligibleOperationRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class ProductionEligibleOperationFilterOptions
{
    public IReadOnlyList<ProductionOutputChoice> WorkOrders { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> Products { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> WorkCentres { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> Processes { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> OutputItems { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> RawMaterials { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> Machines { get; init; } = [];
}

public sealed class ProductionOutputEntryLookups
{
    public IReadOnlyList<ProductionOutputChoice> Shifts { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> Machines { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> Operators { get; init; } = [];
}

public sealed record ProductionOutputChoice(string Code, string Label);

public sealed class ProductionOutputWorkspace
{
    public ProductionEligibleOperationRow Operation { get; init; } = null!;
    public string WipConsumptionPolicy { get; init; } = ProductionWipConsumptionPolicies.Strict;
    public IReadOnlyList<ProductionOutputMaterialLine> Materials { get; init; } = [];
}

public sealed class ProductionSharedWipSourceInquiryRequest
{
    public long WorkOrderMaterialId { get; init; }
    public long? ProductionOutputId { get; init; }
    public DateTime? ProductionDate { get; init; }
    public decimal? ConsumeQty { get; init; }
}

public sealed class ProductionSharedWipSourceInquiry
{
    public string ConsumingDocumentNo { get; init; } = string.Empty;
    public string ConsumingWorkOrderNo { get; init; } = string.Empty;
    public decimal ConsumingQty { get; init; }
    public string ConsumingUom { get; init; } = string.Empty;
    public string ComponentCode { get; init; } = string.Empty;
    public string BaseUom { get; init; } = string.Empty;
    public string WipConsumptionPolicy { get; init; } = ProductionWipConsumptionPolicies.Strict;
    public bool IsHistorical { get; init; }
    public bool IsReversed { get; init; }
    public bool CanViewCost { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<ProductionSharedWipSourceRow> Sources { get; init; } = [];
}

public sealed class ProductionSharedWipSourceRow
{
    public long ProductionBalLotId { get; init; }
    public string LotNo { get; init; } = string.Empty;
    public string SourceWorkOrderNo { get; init; } = string.Empty;
    public int? SourceRouteStageSequence { get; init; }
    public string? SourceWorkCentreCode { get; init; }
    public DateTime? LastMovementDate { get; init; }
    public decimal AvailableBaseQty { get; init; }
    public decimal ConsumedBaseQty { get; init; }
    /// <summary>Actual pooled-average consumption value; omitted unless the caller has View Cost permission.</summary>
    public decimal? ActualPooledConsumptionCost { get; init; }
    public IReadOnlyList<ProductionSharedWipReceiptRow> Receipts { get; init; } = [];
}

public sealed class ProductionSharedWipReceiptRow
{
    public string ProducerDocumentNo { get; init; } = string.Empty;
    public string ProducerWorkOrderNo { get; init; } = string.Empty;
    public int? ProducerRouteStageSequence { get; init; }
    public string? ProducerWorkCentreCode { get; init; }
    public decimal? ReceiptBaseQty { get; init; }
    /// <summary>Genealogy quantity allocated to this output's consume movement, when recorded.</summary>
    public decimal? AllocatedBaseQty { get; init; }
}

public sealed class ProductionOutputMaterialLine
{
    public long WorkOrderMaterialId { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string IssueMethod { get; init; } = string.Empty;
    public string SupplySource { get; init; } = string.Empty;
    public decimal TolerancePercent { get; init; }
    public decimal RequiredQty { get; init; }
    public decimal WoBomRequiredQty { get; init; }
    public string RequiredUom { get; init; } = string.Empty;
    public decimal StandardQty { get; init; }
    public decimal ConsumeQty { get; init; }
    public decimal MaxConsumeQty { get; init; }
    public decimal VarianceQty { get; init; }
    public string? VarianceReasonCode { get; init; }
    public string? VarianceReasonText { get; init; }
    public decimal AvailableQty { get; init; }
    public decimal RemainingAfterConsume { get; init; }
    public string? BlockingReason { get; init; }
    public bool IsHandoff { get; init; }
    public long? HandoffFromOperationId { get; init; }
    public bool IsConsumeEditable { get; init; }
}
