namespace ErpWeb.Model.Entities.Planning;

/// <summary>Maintenance reason master (<c>dbo.PrMaintenanceReason</c>).</summary>
public class PrMaintenanceReason
{
    public string ReasonCd { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReasonType { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public string CompCode { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
}
