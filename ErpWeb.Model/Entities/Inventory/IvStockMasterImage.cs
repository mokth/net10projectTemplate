namespace ErpWeb.Model.Entities.Inventory;

public sealed class IvStockMasterImage
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }

    public IvStockMaster? Item { get; set; }
}
