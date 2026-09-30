namespace ErpWeb.Model.Entities.Planning;

/// <summary>Machine shift calendar day (<c>dbo.PrShiftCalendar</c>).</summary>
public class PrShiftCalendar
{
    public DateTime Dt { get; set; }
    public string? DateCd { get; set; }
    public string? ShfGrpCd { get; set; }
    public string MachineCode { get; set; } = string.Empty;
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
}
