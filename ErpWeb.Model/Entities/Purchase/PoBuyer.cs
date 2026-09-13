namespace ErpWeb.Model.Entities.Purchase;

public class PoBuyer
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BuyerCode { get; set; } = string.Empty;
    public string? BuyerName { get; set; }
    public string? BuyerDesc { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
