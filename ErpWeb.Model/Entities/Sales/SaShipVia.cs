namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Ship-via reference master. Company-scoped on purpose (legacy had no tenancy at all — D-2).
/// Level A concurrency (RowVersion).
/// </summary>
public class SaShipVia
{
    public string CompanyCode { get; set; } = string.Empty;
    public string ShipViaCode { get; set; } = string.Empty;
    public string? ShipViaDesc { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
