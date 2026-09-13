namespace ErpWeb.Model.Entities.Purchase;

public class PoPurItem
{
    public int Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? Category { get; set; }
    public string? SubCategory { get; set; }
    public string? Dept { get; set; }
    public decimal PurQty { get; set; }
    public string? PurUom { get; set; }
    public string Vendor { get; set; } = string.Empty;
    public string? VendName { get; set; }
    public string? VendorPartNo { get; set; }
    public string? Currency { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal Moq { get; set; }
    public int? LeadTime { get; set; }
    public string? Status { get; set; }
    public string? Remarks { get; set; }
    public string? PurchaseGlCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
