namespace ErpWeb.Model.Entities.Planning;

/// <summary>Shift icon path (<c>dbo.PrShiftIcon</c>).</summary>
public class PrShiftIcon
{
    public string ShiftCd { get; set; } = string.Empty;
    public string ShiftPeriod { get; set; } = string.Empty;
    public string? ImgPath { get; set; }
}
