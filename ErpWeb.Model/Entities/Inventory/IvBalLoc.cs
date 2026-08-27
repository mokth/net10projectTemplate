namespace ErpWeb.Model.Entities.Inventory;

public class IvBalLoc
{
    public int Id { get; set; }
    public int? LotId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string WhCode { get; set; } = string.Empty;
    public string LocCode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public string IStatus { get; set; } = string.Empty;
    public short? RevNo { get; set; }
    public string? RefNo { get; set; }
    public decimal StdQty { get; set; }
    public string? StdUom { get; set; }
    public DateTime? TransDate { get; set; }
    public string? PoNo { get; set; }
    public string? Remarks { get; set; }
    public decimal? Cost { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public IvStockMaster StockMaster { get; set; } = null!;
    public IvWarehouse Warehouse { get; set; } = null!;
    public IvLot? Lot { get; set; }
}
