namespace ErpWeb.Model.Entities.Sales;

public class SaDocApplicationBackfillSkip
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocType { get; set; } = string.Empty;
    public string DocId { get; set; } = string.Empty;
    public short Line { get; set; }
    public string ViolationCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? SourceValues { get; set; }
    public string? TargetValues { get; set; }
    public DateTime CreatedUtc { get; set; }
}
