namespace ErpWeb.Model.Entities.Purchase;

public class PoVendorByItem
{
    public int Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public string? Vendor { get; set; }
    public string? VendorPartNo { get; set; }
    public int? LeadTime { get; set; }
    public string? Country { get; set; }
    public string? Currency { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? PurUom { get; set; }
    public decimal PackSize { get; set; }
    public decimal Tolerance { get; set; }
    public decimal PriceTolerance { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? Buyer { get; set; }
    public decimal OrdLevel { get; set; }
    public decimal SafetyStock { get; set; }
    public string? Status { get; set; }
    public string? PayCode { get; set; }
    public string? MaterialType { get; set; }
    public string? PoDesc { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
