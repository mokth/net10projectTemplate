using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Entities.Production;

/// <summary>Draft-to-inventory allocation map. One row represents one inventory detail.</summary>
public sealed class ProductionMaterialIssueLine
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long PostingLinkId { get; set; }
    public ProductionPostingLink? PostingLink { get; set; }
    public int InventoryBatchId { get; set; }
    public IvTrxBatch? InventoryBatch { get; set; }
    public int InventoryBatchDetailId { get; set; }
    public IvTrxBatchDetail? InventoryBatchDetail { get; set; }
    public int InventoryBatchNo { get; set; }
    /// <summary>Inventory document generation captured by this immutable issue mapping.</summary>
    public int DocumentRevision { get; set; }
    public short InventoryTrxLineNo { get; set; }
    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public long WorkOrderOperationId { get; set; }
    public ProductionWorkOrderOperation? WorkOrderOperation { get; set; }
    public long WorkOrderMaterialId { get; set; }
    public ProductionWorkOrderMaterial? WorkOrderMaterial { get; set; }
    public decimal IssueQty { get; set; }
    public decimal BaseQty { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
