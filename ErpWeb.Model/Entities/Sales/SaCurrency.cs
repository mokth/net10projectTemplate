namespace ErpWeb.Model.Entities.Sales;

public class SaCurrency
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CurrCode { get; set; } = string.Empty;
    public string? CurrDesc { get; set; }
    public bool? IsActive { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
