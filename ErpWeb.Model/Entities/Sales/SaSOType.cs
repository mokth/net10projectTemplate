namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Sales-order type reference master. FOCAuto is deliberately not created (D-3) — every
/// legacy consumer of it is commented out. Level A concurrency (RowVersion).
/// </summary>
public class SaSOType
{
    public string CompanyCode { get; set; } = string.Empty;
    public string SOTypeCode { get; set; } = string.Empty;
    public string? SOTypeDesc { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
