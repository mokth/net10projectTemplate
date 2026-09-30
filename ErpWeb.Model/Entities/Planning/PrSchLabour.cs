namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work-order labour snapshot line (<c>dbo.PrSchLabour</c>).</summary>
public class PrSchLabour
{
    public string ScheCode { get; set; } = string.Empty;
    public short RelNo { get; set; }
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string WciCode { get; set; } = string.Empty;
    public string ProcessCode { get; set; } = string.Empty;
    public string MachineCode { get; set; } = string.Empty;
    public string LabourCode { get; set; } = string.Empty;
    public double? LabourCost { get; set; }

    public PrSchMa? PrSchMa { get; set; }
}
