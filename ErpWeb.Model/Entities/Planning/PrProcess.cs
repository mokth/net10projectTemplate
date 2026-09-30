namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work process master (<c>dbo.PrProcess</c>). PK = (Process_Cd, Work_Centre).</summary>
public class PrProcess
{
    public string ProcessCd { get; set; } = string.Empty;
    public string? ProcessDes { get; set; }
    public int? Sequence { get; set; }
    public string WorkCentre { get; set; } = string.Empty;
    public bool? Stock { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
}
