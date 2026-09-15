namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Sales customer sub-group reference master. Company-scoped, no Active column
/// (matches its Blazor sibling <see cref="SaCustGroup"/>). Level A concurrency (RowVersion).
/// </summary>
public class SaCustSubGroup
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustSubGroupCode { get; set; } = string.Empty;
    public string? CustSubGroupDesc { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
