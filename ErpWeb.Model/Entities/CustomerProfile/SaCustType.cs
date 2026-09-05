namespace ErpWeb.Model.Entities.CustomerProfile;

public class SaCustType
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustTypeCode { get; set; } = string.Empty;
    public string? CustTypeDesc { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
