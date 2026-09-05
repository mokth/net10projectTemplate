namespace ErpWeb.Model.Entities.Sales;

public class SaTaxGroup
{
    public string CompanyCode { get; set; } = string.Empty;
    public string TaxGrCode { get; set; } = string.Empty;
    public string? TaxGrDesc { get; set; }
    public decimal Percentage { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
