namespace ErpWeb.Model.Entities.Inventory;

public class IvStatus
{
    public string CompanyCode { get; set; } = string.Empty;
    public string IStatus { get; set; } = string.Empty;
    public string? StatusDesc { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
