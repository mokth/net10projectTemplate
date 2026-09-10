namespace ErpWeb.Model.Entities.Sales;

public class SaDocApplication
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string SourceDocType { get; set; } = string.Empty;
    public string SourceDocId { get; set; } = string.Empty;
    public short SourceCustRel { get; set; }
    public short SourceLineId { get; set; }
    public string TargetDocType { get; set; } = string.Empty;
    public string TargetDocId { get; set; } = string.Empty;
    public short TargetCustRel { get; set; }
    public short TargetLineId { get; set; }
    public string RelatedSoNo { get; set; } = string.Empty;
    public short RelatedCustRel { get; set; }
    public short RelatedSoLine { get; set; }
    public decimal AppliedQty { get; set; }
    public decimal? AppliedAmount { get; set; }
    public DateTime? Created { get; set; }
    public string? CreatedUid { get; set; }
}
