namespace ErpWeb.Model.Entities;

public class AdSmNumDate
{
    public int Uid { get; set; }
    public string CompanyCode { get; set; } = "";
    public string BranchCode { get; set; } = "";
    public string? LocationCode { get; set; }
    public short? Year { get; set; }
    public short? Month { get; set; }
    public string? NumCd { get; set; }
    public string? NumDes { get; set; }
    public short? TotLength { get; set; }
    public string? Prefix { get; set; }
    public long? Seq { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserID { get; set; }
    public string? NumberingDelimeter { get; set; }
    public byte[]? RowVersion { get; set; }
    public string? NumberingFormat { get; set; }
}
