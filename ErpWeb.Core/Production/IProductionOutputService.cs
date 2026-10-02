using ErpWeb.Core.Inventory;

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

    Task<IvMasterOperationResult<IReadOnlyList<ProductionEligibleOperationRow>>> SearchEligibleOperationsAsync(
        ProductionEligibleOperationQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionOutputWorkspace>> GetWorkspaceAsync(
        long workOrderOperationId, CancellationToken cancellationToken = default);
}

public class ProductionOutputCreateRequest
{
    public long WorkOrderOperationId { get; set; }
    public DateTime ProductionDate { get; set; }
    public string? ShiftCode { get; set; }
    public string? ActualMachineCode { get; set; }
    public string? OperatorCode { get; set; }
    public decimal GoodQty { get; set; }
    public decimal ScrapQty { get; set; }
    public decimal RejectQty { get; set; }
    public decimal HoldQty { get; set; }
    public string OutputLotNo { get; set; } = string.Empty;
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
    public int Take { get; set; } = 100;
}

public sealed class ProductionOutputDetail
{
    public long Uid { get; init; }
    public string DocumentNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public long RouteStepId { get; init; }
    public long WorkOrderOperationId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string WorkCentreCode { get; init; } = string.Empty;
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
    public byte[] RowVersion { get; init; } = [];
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
    public string WorkCentreCode { get; init; } = string.Empty;
    public string OperationCode { get; init; } = string.Empty;
    public string OutputItemCode { get; init; } = string.Empty;
    public decimal PlannedOutputQty { get; init; }
    public decimal GoodQty { get; init; }
    public decimal RemainingQty { get; init; }
    public string? OutputUom { get; init; }
    public bool IsFinalOperation { get; init; }
    public string? OutputType { get; init; }
}

public sealed class ProductionOutputWorkspace
{
    public ProductionEligibleOperationRow Operation { get; init; } = null!;
    public IReadOnlyList<ProductionOutputMaterialLine> Materials { get; init; } = [];
}

public sealed class ProductionOutputMaterialLine
{
    public long WorkOrderMaterialId { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string IssueMethod { get; init; } = string.Empty;
    public string SupplySource { get; init; } = string.Empty;
    public decimal RequiredQty { get; init; }
    public string RequiredUom { get; init; } = string.Empty;
    public decimal AvailableQty { get; init; }
    public string? BlockingReason { get; init; }
}
