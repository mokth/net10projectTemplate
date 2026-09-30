namespace ErpWeb.Model.Entities.Planning;

/// <summary>Product-definition machine routing line (<c>dbo.PrDefMachine</c>).</summary>
public class PrDefMachine
{
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string WciCode { get; set; } = string.Empty;
    public string ProcessCode { get; set; } = string.Empty;
    public string MachineCode { get; set; } = string.Empty;
    public string? MachineName { get; set; }
    public double CycleTime { get; set; }
    public double? ConversionTime { get; set; }
    public double? StartupTime { get; set; }
    public double? QueueTime { get; set; }
    public int? SeqNo { get; set; }
    public bool? MacDefault { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
    public double? CycleTimeCal { get; set; }
    public double? CycleTimeInvd { get; set; }
    public int? NoMachine { get; set; }

    public PrDefMa? PrDefMa { get; set; }
}
