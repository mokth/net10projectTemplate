using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

/// <summary>
/// Shared, read-only material stock availability. It deliberately does not decide whether a
/// material is issued manually, backflushed, or from a pick list; callers apply their own action
/// gates after reading the same stock facts.
/// </summary>
public interface IProductionMaterialStockAvailabilityReader
{
    Task<IvMasterOperationResult<ProductionMaterialStockAvailability>> GetAsync(
        long workOrderMaterialId,
        DateTime issueDate,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<int, decimal>? reservedBaseQtyByBalance = null,
        int? excludeInventoryBatchNo = null);
}

public sealed class ProductionMaterialStockAvailability
{
    public long WorkOrderMaterialId { get; init; }
    public long WorkOrderId { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string? ComponentDescription { get; init; }
    public decimal RequiredBaseQty { get; init; }
    public string? WarehouseCode { get; init; }
    public string? BaseUom { get; init; }
    public decimal ConversionFactorToBase { get; init; }
    public string IssueMethod { get; init; } = string.Empty;
    public string SupplySource { get; init; } = string.Empty;
    public string WorkOrderStatus { get; init; } = string.Empty;
    public IReadOnlyList<ProductionMaterialStockCandidate> Candidates { get; init; } = [];
}
