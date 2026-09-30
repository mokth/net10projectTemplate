namespace ErpWeb.Model.Entities.Planning;

/// <summary>Machine sequence (<c>dbo.PrMacSeq</c>).</summary>
public class PrMacSeq
{
    public string MachineCd { get; set; } = string.Empty;
    public string ProcessCd { get; set; } = string.Empty;
    public int? SeqNo { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
}
