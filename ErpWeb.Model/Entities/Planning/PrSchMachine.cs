namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work-order machine snapshot line (<c>dbo.PrSchMachine</c>).</summary>
public class PrSchMachine
{
    public string ScheCode { get; set; } = string.Empty;
    public short RelNo { get; set; }
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string WciCode { get; set; } = string.Empty;
    public string ProcessCode { get; set; } = string.Empty;
    public string MachineCode { get; set; } = string.Empty;
    public string? MachineName { get; set; }
    public double? CycleTime { get; set; }
    public double? ConversionTime { get; set; }
    public double? StartupTime { get; set; }
    public double? QueueTime { get; set; }
    public int? SeqNo { get; set; }
    public bool? MacDefault { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? CompleteDate { get; set; }

    public PrSchMa? PrSchMa { get; set; }
}
