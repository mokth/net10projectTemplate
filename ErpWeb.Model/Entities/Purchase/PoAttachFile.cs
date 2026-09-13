namespace ErpWeb.Model.Entities.Purchase;

public class PoAttachFile
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocId { get; set; } = string.Empty;
    public string DocName { get; set; } = string.Empty;
    public string DocKey { get; set; } = string.Empty;
    public string? DocPath { get; set; }
    public string? RevNo { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
}
