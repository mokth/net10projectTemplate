namespace ErpWeb.Model.Entities.Planning;

/// <summary>Shift master (<c>dbo.PrShift</c>).</summary>
public class PrShift
{
    public string ShiftCd { get; set; } = string.Empty;
    public string? ShiftDes { get; set; }
    public DateTime? StartTm { get; set; }
    public DateTime? EndTm { get; set; }
    public DateTime? BreakTm1From { get; set; }
    public DateTime? BreakTm1To { get; set; }
    public DateTime? BreakTm2From { get; set; }
    public DateTime? BreakTm2To { get; set; }
    public DateTime? BreakTm3From { get; set; }
    public DateTime? BreakTm3To { get; set; }
    public DateTime? BreakTm4From { get; set; }
    public DateTime? BreakTm4To { get; set; }
    public DateTime? BreakTm5From { get; set; }
    public DateTime? BreakTm5To { get; set; }
    public DateTime? OtStartTime { get; set; }
    public string? OverrideMrpPlan { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
    public double? TotalTime { get; set; }
}
