using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Advisory stock reservation for a released Delivery Request.
/// This is fulfilment control metadata only; it never changes physical inventory.
/// A released row is retained as history. Quantity changes release the old row and
/// create a replacement active row.
/// </summary>
public sealed class SaDeliveryRequestStockReservation
{
    public long Uid { get; set; }
    public long DeliveryRequestId { get; set; }
    public SaDeliveryRequest DeliveryRequest { get; set; } = null!;
    public long DeliveryRequestSourceId { get; set; }
    public SaDeliveryRequestSource DeliveryRequestSource { get; set; } = null!;
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public int BalLocId { get; set; }
    public IvBalLoc BalLoc { get; set; } = null!;
    public decimal ReservedQty { get; set; }
    public bool IsActive { get; set; }
    public DateTime? ReleasedDate { get; set; }
    public string? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
