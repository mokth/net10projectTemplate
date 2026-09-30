namespace ErpWeb.Model.Entities.Planning;

/// <summary>Normalized shift break rows (<c>dbo.PrShiftBreak</c>).</summary>
public class PrShiftBreak
{
    public string CompCode { get; set; } = string.Empty;
    public string ShiftCd { get; set; } = string.Empty;
    public byte BreakSeq { get; set; }
    public DateTime BreakFrom { get; set; }
    public DateTime BreakTo { get; set; }
    public DateTime? Created { get; set; }
    public string? UserId { get; set; }
}
