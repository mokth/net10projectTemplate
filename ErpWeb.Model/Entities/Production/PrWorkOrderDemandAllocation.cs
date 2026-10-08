namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Relational Delivery Request to Work Order demand bridge. A unique WorkOrderID
/// enforces that a Work Order consumes exactly one Delivery Request.
/// </summary>
public class PrWorkOrderDemandAllocation
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long WorkOrderId { get; set; }
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public long DeliveryRequestId { get; set; }
    public ErpWeb.Model.Entities.Sales.SaDeliveryRequest DeliveryRequest { get; set; } = null!;
    public decimal AllocatedQty { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? ReleasedDate { get; set; }
    public string? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
