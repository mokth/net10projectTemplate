namespace ErpWeb.Model.Entities.Planning;

/// <summary>Manufacturing calendar day (<c>dbo.PrCalendar</c>).</summary>
public class PrCalendar
{
    public DateTime Dt { get; set; }
    public string? DateCd { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
}
