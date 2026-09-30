namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work-order number prefix (<c>dbo.PrWorkPefix</c>).</summary>
public class PrWorkPefix
{
    public string Prefix { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
}
