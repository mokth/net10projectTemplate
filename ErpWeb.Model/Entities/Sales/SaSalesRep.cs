namespace ErpWeb.Model.Entities.Sales;

public class SaSalesRep
{
   
    public string SrepCode { get; set; } = string.Empty;
    public string? SrepName { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Tel { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public bool? IsActive { get; set; }
    public decimal? CommissionRate { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
