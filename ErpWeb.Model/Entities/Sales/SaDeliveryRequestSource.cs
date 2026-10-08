namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Exact Sales Order revision/line lineage contributing production demand to a Delivery Request.
/// </summary>
public class SaDeliveryRequestSource
{
    public long Uid { get; set; }
    public long DeliveryRequestId { get; set; }
    public SaDeliveryRequest DeliveryRequest { get; set; } = null!;

    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SoNo { get; set; } = string.Empty;
    public short CustRel { get; set; }
    public short SoLine { get; set; }
    public SaSoDetail? SalesOrderDetail { get; set; }

    public string ProductCode { get; set; } = string.Empty;
    public string SourceUom { get; set; } = string.Empty;
    public string ProductionUom { get; set; } = string.Empty;
    public decimal SourceQty { get; set; }
    public decimal AllocatedProductionQty { get; set; }
    public string? CustomerCode { get; set; }
    public DateTime? RequestedDeliveryDate { get; set; }

    // Lifecycle fields preserve source history while allowing active quantity to be derived.
    public bool IsActive { get; set; } = true;
    public DateTime? ReleasedDate { get; set; }
    public string? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
