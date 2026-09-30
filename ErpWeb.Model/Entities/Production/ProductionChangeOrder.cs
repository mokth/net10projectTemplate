namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Future-effect-only Work Order change contract. Posted history is never rewritten by this entity.
/// </summary>
public class ProductionChangeOrder
{
    public long Uid { get; set; }
    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ChangeOrderNo { get; set; } = string.Empty;
    public int SourceSnapshotRevision { get; set; }
    public int ProposedSnapshotRevision { get; set; }
    public string Status { get; set; } = ProductionChangeOrderStatuses.Draft;
    public string Reason { get; set; } = string.Empty;
    public DateTime? EffectiveDate { get; set; }
    public DateTime? RequestedDate { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<ProductionChangeOrderLine> Lines { get; set; } = new List<ProductionChangeOrderLine>();
}

public class ProductionChangeOrderLine
{
    public long Uid { get; set; }
    public long ChangeOrderId { get; set; }
    public ProductionChangeOrder? ChangeOrder { get; set; }
    public int LineNo { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public long? TargetUid { get; set; }
    public string? FieldName { get; set; }
    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }
    public string? DetailJson { get; set; }
}

