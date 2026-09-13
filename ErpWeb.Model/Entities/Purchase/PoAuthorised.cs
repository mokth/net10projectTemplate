namespace ErpWeb.Model.Entities.Purchase;

public class PoAuthorised
{
    public string CompanyCode { get; set; } = string.Empty;
    public string Authorised { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? MobileNo { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
