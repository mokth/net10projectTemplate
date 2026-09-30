namespace ErpWeb.Model.Entities.Planning;

/// <summary>Holiday master (<c>dbo.PrHoliday</c>).</summary>
public class PrHoliday
{
    public int Uid { get; set; }
    public DateTime? DateOff { get; set; }
    public string? Description { get; set; }
    public int? Year { get; set; }
}
