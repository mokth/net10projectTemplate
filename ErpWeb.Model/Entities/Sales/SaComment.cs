namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Canned comment reference master. The legacy <c>Module</c> column and the legacy identity
/// <c>ID</c> column are deliberately not created (D-4) — Module is written but read by no consumer.
/// Level A concurrency (RowVersion).
/// </summary>
public class SaComment
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CommID { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
