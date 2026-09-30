namespace ErpWeb.Model.Entities.Planning;

/// <summary>Machine maintenance record (<c>dbo.PrMacMaintenance</c>).</summary>
public class PrMacMaintenance
{
    public int Id { get; set; }
    public DateTime? TrxDate { get; set; }
    public string? MacCode { get; set; }
    public string? Description { get; set; }
    public string? ActionTaken { get; set; }
    public string? Status { get; set; }
    public string? ReportBy { get; set; }
    public string? ActionBy { get; set; }
    public DateTime? ActionOn { get; set; }
    public string? RefCode { get; set; }
    public string? RepType { get; set; }
    public string? MType { get; set; }
    public string? Name { get; set; }
    public string? Reminder { get; set; }
}
