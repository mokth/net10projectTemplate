namespace ErpWeb.Model.Entities.Planning;

/// <summary>Shift group membership (<c>dbo.PrShiftGroup</c>).</summary>
public class PrShiftGroup
{
    public string ShfGrpCd { get; set; } = string.Empty;
    public string ShfGrpDes { get; set; } = string.Empty;
    public string ShiftCd { get; set; } = string.Empty;
    public int? TotalTime { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
    public bool? DefaultGrp { get; set; }
    public string? ShiftColor { get; set; }
}
