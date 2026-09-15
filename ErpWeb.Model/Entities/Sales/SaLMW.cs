namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// LMW (Malaysia Licensed Manufacturing Warehouse) licence register, keyed
/// <c>(CompanyCode, LicenseNo, CustCode)</c>. The four window columns are SQL <c>date</c>
/// (day-granular, NOT NULL) so the inclusive-endpoint overlap rule in the plan §9.3 is total.
/// No <c>Active</c> column — legacy has none. Level A concurrency (RowVersion).
/// </summary>
public class SaLMW
{
    public string CompanyCode { get; set; } = string.Empty;
    public string LicenseNo { get; set; } = string.Empty;
    public string CustCode { get; set; } = string.Empty;
    public string? LicenseID { get; set; }
    public string? LicenseType { get; set; }
    public DateTime LicenseStartDate { get; set; }
    public DateTime LicenseEndDate { get; set; }
    public DateTime SystemStartDate { get; set; }
    public DateTime SystemEndDate { get; set; }
    public string? Name { get; set; }
    public string? IC { get; set; }
    public string? Position { get; set; }
    public string? CustName { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
