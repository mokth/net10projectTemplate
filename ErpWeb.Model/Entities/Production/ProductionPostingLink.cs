namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Reserved idempotency/posting link used by later material, WIP and FG commands. The unique key
/// makes a repeated command discoverable before any stock mutation is attempted.
/// </summary>
public class ProductionPostingLink
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string PostingRequestId { get; set; } = string.Empty;
    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public string? ProductionDocumentType { get; set; }
    public string? ProductionDocumentNo { get; set; }
    public long? ProductionDocumentLineId { get; set; }
    public int? InventoryBatchNo { get; set; }
    public int? SnapshotRevision { get; set; }
    public string? SnapshotHash { get; set; }
    public string? PostingOperationId { get; set; }
    public long? OriginalPostingLinkId { get; set; }
    public ProductionPostingLink? OriginalPostingLink { get; set; }
    public string Status { get; set; } = ProductionPostingLinkStatuses.Pending;
    public string? ResultCode { get; set; }
    public string? ResultMessage { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? CompletedDate { get; set; }
}

