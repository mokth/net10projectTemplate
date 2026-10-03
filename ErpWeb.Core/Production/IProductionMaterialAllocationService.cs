using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public interface IProductionMaterialAllocationService
{
    Task<IvMasterOperationResult<IReadOnlyList<ProductionMaterialStockCandidate>>> GetStockCandidatesAsync(
        long workOrderMaterialId,
        DateTime issueDate,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<int, decimal>? reservedBaseQtyByBalance = null,
        int? excludeInventoryBatchNo = null);

    Task<IvMasterOperationResult<ProductionMaterialAllocationResult>> AutoAllocateAsync(
        ProductionMaterialAllocationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ProductionMaterialAllocationRequest
{
    public long WorkOrderMaterialId { get; set; }
    public DateTime IssueDate { get; set; }
    public decimal RequestedQty { get; set; }
    /// <summary>
    /// Base quantities already proposed from balances by other lines in the same unsaved document.
    /// Subtracted from usable stock before FEFO/FIFO allocation.
    /// </summary>
    public IReadOnlyDictionary<int, decimal> ReservedBaseQtyByBalance { get; init; }
        = new Dictionary<int, decimal>();
    /// <summary>Current draft batch when editing; its allocations must not reserve against itself.</summary>
    public int? ExcludeInventoryBatchNo { get; set; }
}

public sealed class ProductionMaterialStockCandidate
{
    public int FromBalLocId { get; init; }
    public string Warehouse { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public int? LotId { get; init; }
    public string LotNo { get; init; } = string.Empty;
    public DateTime? ExpiryDate { get; init; }
    public DateTime? StockDate { get; init; }
    public decimal AvailableBaseQty { get; init; }
    public decimal CurrentBaseQty { get; init; }
    public decimal AsOfBaseQty { get; init; }
    /// <summary>Usable physical/as-of stock before advisory draft reservations.</summary>
    public decimal UsableBaseQty { get; init; }
    public decimal ReservedOtherDraftBaseQty { get; init; }
    public decimal ReservedCurrentDocumentBaseQty { get; init; }
    /// <summary>Usable stock after other-draft and current-document proposal reservations.</summary>
    public decimal AvailableToAllocateBaseQty { get; init; }
    public string BaseUom { get; init; } = string.Empty;
    public decimal SuggestedBaseQty { get; init; }
    public decimal? UnitPrice { get; init; }
}

public sealed class ProductionMaterialAllocationResult
{
    public decimal RequestedQty { get; init; }
    public decimal RequestedBaseQty { get; init; }
    public decimal AllocatedBaseQty { get; init; }
    public decimal ShortBaseQty { get; init; }
    public IReadOnlyList<ProductionMaterialStockCandidate> Allocations { get; init; } = [];
}
