namespace ErpWeb.Model.Entities.Inventory;

public class IvLot
{
    public int Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public string? SourceType { get; set; }
    public string? SourceDocNo { get; set; }
    public string? SupplierCode { get; set; }
    public DateTime? ReceiptDate { get; set; }
    public DateTime? MfgDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? QcStatus { get; set; }
    public string? Remarks { get; set; }
    public string? LocationCode { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public IvStockMaster StockMaster { get; set; } = null!;
    public ICollection<IvBalLoc> Balances { get; set; } = new List<IvBalLoc>();
}
