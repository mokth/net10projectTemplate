namespace ErpWeb.Model.Entities.Purchase;

public class PoPrAttachFile
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocId { get; set; } = string.Empty;
    public string DocName { get; set; } = string.Empty;
    public string? DocKey { get; set; }
    public string? DocName2 { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
}
