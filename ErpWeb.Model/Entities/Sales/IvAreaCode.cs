namespace ErpWeb.Model.Entities.Sales;

public class IvAreaCode
{
    public string CompanyCode { get; set; } = string.Empty;
    public string AreaCode { get; set; } = string.Empty;
    public string? AreaDesc { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public string? Latitude { get; set; }
    public string? Longitude { get; set; }
}
