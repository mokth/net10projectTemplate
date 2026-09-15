namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Shipping lead-time reference master. <c>Days</c> is a typed int (NULL = "not specified").
/// <c>Type</c> stores only INTERNAL/EXTERNAL; blank/whitespace normalises to NULL (D-5).
/// Level A concurrency (RowVersion).
/// </summary>
public class SaShippingLeadTime
{
    public string CompanyCode { get; set; } = string.Empty;
    public string LeadTimeCode { get; set; } = string.Empty;
    public string? LeadTimeDesc { get; set; }
    public int? Days { get; set; }
    public string? Type { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
