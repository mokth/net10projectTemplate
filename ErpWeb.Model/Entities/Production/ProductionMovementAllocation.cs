using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Model.Entities.Production;

public sealed class ProductionMovementAllocation
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long StockPostingId { get; set; }
    public StockPosting? StockPosting { get; set; }
    public long ReceiptMovementId { get; set; }
    public ProductionBalLotMovement? ReceiptMovement { get; set; }
    public long OutboundMovementId { get; set; }
    public ProductionBalLotMovement? OutboundMovement { get; set; }
    public decimal BaseQty { get; set; }
    public long? ReversesAllocationId { get; set; }
    public ProductionMovementAllocation? ReversesAllocation { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
