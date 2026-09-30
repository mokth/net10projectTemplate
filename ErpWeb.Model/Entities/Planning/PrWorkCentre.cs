namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work centre master (<c>dbo.PrWorkCentre</c>).</summary>
public class PrWorkCentre
{
    public string WrkCtrCd { get; set; } = string.Empty;
    public string? WrkCtrDes { get; set; }
    public string? Class { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
}
