namespace ErpWeb.Model.Entities;

public class MsRunningNo
{
    public string CompanyCode { get; set; } = string.Empty;
    public string DocKey { get; set; } = string.Empty;
    public int LastNo { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
