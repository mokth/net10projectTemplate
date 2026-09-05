namespace ErpWeb.Model.Entities.CustomerProfile;

public class SaCustGroup
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CustGroupCode { get; set; } = string.Empty;
    public string? CustGroupDesc { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
