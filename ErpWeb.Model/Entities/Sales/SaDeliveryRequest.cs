namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// A lightweight production-demand handoff between Sales Orders and Work Orders.
/// Progress is derived from the source and allocation bridges; the header does not
/// duplicate production execution facts.
/// </summary>
public class SaDeliveryRequest
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DeliveryRequestNo { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? ProductDescription { get; set; }
    public string ProductionUom { get; set; } = string.Empty;
    public decimal RequestedQty { get; set; }
    public DateTime RequiredDate { get; set; }
    public string? DefinitionCode { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ProjectCode { get; set; }
    public string? Priority { get; set; }
    public string Status { get; set; } = SaDeliveryRequestStatuses.Draft;
    public string? Remark { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SaDeliveryRequestSource> Sources { get; set; } = new List<SaDeliveryRequestSource>();
    public ICollection<ErpWeb.Model.Entities.Production.PrWorkOrderDemandAllocation> WorkOrderAllocations { get; set; } =
        new List<ErpWeb.Model.Entities.Production.PrWorkOrderDemandAllocation>();
    public ICollection<SaDeliveryRequestAuditEvent> AuditEvents { get; set; } =
        new List<SaDeliveryRequestAuditEvent>();
}

public static class SaDeliveryRequestStatuses
{
    public const string Draft = "DRAFT";
    public const string Released = "RELEASED";
    public const string InProduction = "IN_PRODUCTION";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static bool IsKnown(string? value) => value is Draft or Released or InProduction or Completed or Cancelled;
}
