namespace ErpWeb.Model.Entities.Planning;

/// <summary>Operator master (<c>dbo.PrOperator</c>).</summary>
public class PrOperator
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool? Active { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompanyCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }
}
