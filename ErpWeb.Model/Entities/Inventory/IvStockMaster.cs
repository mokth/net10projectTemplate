namespace ErpWeb.Model.Entities.Inventory;

public class IvStockMaster
{
    public string CompanyCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? IType { get; set; }
    public string? IClassCode { get; set; }
    public string? ISubClassCode { get; set; }
    public string? StdUom { get; set; }
    public string? PurUom { get; set; }
    public string? SellingUom { get; set; }
    public bool StockControl { get; set; } = true;
    public bool LotControl { get; set; }
    public string? DefWarehouse { get; set; }
    public string? DefLocation { get; set; }
    public decimal? MinStock { get; set; }
    public decimal? MaxStock { get; set; }
    public decimal? StdPackSize { get; set; }
    public decimal? PurStdPackSize { get; set; }
    public decimal? SellingPrice { get; set; }
    public decimal? PurchasePrice { get; set; }
    public string? SellingGlCode { get; set; }
    public string? PurchaseGlCode { get; set; }
    public string? Size { get; set; }
    public string? Color { get; set; }
    public string? Brand { get; set; }
    public string? Barcode { get; set; }
    public string? ImagePath { get; set; }
    public string? TaxGroup { get; set; }
    public string? PurchaseTaxGroup { get; set; }
    public string? Classification { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<IvBalLoc> Balances { get; set; } = new List<IvBalLoc>();
    public ICollection<IvLot> Lots { get; set; } = new List<IvLot>();
}
