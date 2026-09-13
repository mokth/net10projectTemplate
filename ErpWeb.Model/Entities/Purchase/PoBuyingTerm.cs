namespace ErpWeb.Model.Entities.Purchase;

public class PoBuyingTerm
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BuyingTerm { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
