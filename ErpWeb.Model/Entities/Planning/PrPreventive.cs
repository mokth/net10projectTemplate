namespace ErpWeb.Model.Entities.Planning;

/// <summary>Preventive downtime (<c>dbo.PrPreventive</c>).</summary>
public class PrPreventive
{
    public int Uid { get; set; }
    public string MachineCd { get; set; } = string.Empty;
    public DateTime DownDt { get; set; }
    public DateTime StartTm { get; set; }
    public DateTime EndTm { get; set; }
    public string? ReasonCd { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? Remark { get; set; }
}
