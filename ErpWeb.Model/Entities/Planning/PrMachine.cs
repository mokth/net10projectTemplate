namespace ErpWeb.Model.Entities.Planning;

/// <summary>Machine master (<c>dbo.PrMachine</c>).</summary>
public class PrMachine
{
    public string MachineCd { get; set; } = string.Empty;
    public string? MachineDes { get; set; }
    public string ProcessCd { get; set; } = string.Empty;
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
    public double? ConversionTime { get; set; }
    public double? StartupTime { get; set; }
    public double? QueueTime { get; set; }
    public bool Active { get; set; } = true;
    public string? MachineType { get; set; }
    public string? SerialNo { get; set; }
    public decimal HourlyCost { get; set; }
}
