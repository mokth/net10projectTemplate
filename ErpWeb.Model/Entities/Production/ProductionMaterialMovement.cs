using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Immutable production-side material execution fact. Inventory remains authoritative for the
/// physical stock movement; this row preserves its exact Work Order, operation and stock lineage.
/// </summary>
public class ProductionMaterialMovement
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;

    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public long WorkOrderMaterialId { get; set; }
    public ProductionWorkOrderMaterial? WorkOrderMaterial { get; set; }
    public long WorkOrderOperationId { get; set; }
    public ProductionWorkOrderOperation? WorkOrderOperation { get; set; }

    public string MovementType { get; set; } = string.Empty;
    public DateTime MovementDate { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal BaseQty { get; set; }
    public string BaseUom { get; set; } = string.Empty;
    public decimal ConversionFactorToBase { get; set; }

    public string WarehouseCode { get; set; } = string.Empty;
    public string LocationCode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public int? LotId { get; set; }
    public IvLot? Lot { get; set; }
    public int FromBalLocId { get; set; }
    public IvBalLoc? FromBalLoc { get; set; }
    public string ItemStatus { get; set; } = string.Empty;

    public int InventoryBatchId { get; set; }
    public IvTrxBatch? InventoryBatch { get; set; }
    public int InventoryBatchNo { get; set; }
    public int InventoryBatchDetailId { get; set; }
    public IvTrxBatchDetail? InventoryBatchDetail { get; set; }
    public short InventoryTrxLineNo { get; set; }
    public int? InventoryHistoryId { get; set; }
    public string? InventoryPostingOperationId { get; set; }

    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }

    public long PostingLinkId { get; set; }
    public ProductionPostingLink? PostingLink { get; set; }
    public long? OriginalMovementId { get; set; }
    public ProductionMaterialMovement? OriginalMovement { get; set; }

    public string? Reason { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
