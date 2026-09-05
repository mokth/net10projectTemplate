namespace ErpWeb.Model.Entities;

public class AdSmNum
{
    public string CompanyCode { get; set; } = "";
    public string BranchCode { get; set; } = "";
    public string? LocationCode { get; set; }
    public string NumCd { get; set; } = "";
    public string? NumDes { get; set; }
    public short TotLength { get; set; }
    public string? Prefix { get; set; }
    public long Seq { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserID { get; set; }
    public string? UpdatedUID { get; set; }
}
