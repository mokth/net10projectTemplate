namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work-order process snapshot line (<c>dbo.PrSchProcess</c>).</summary>
public class PrSchProcess
{
    public string ScheCode { get; set; } = string.Empty;
    public short RelNo { get; set; }
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string WciCode { get; set; } = string.Empty;
    public string ProcessCode { get; set; } = string.Empty;
    public int? SeqNo { get; set; }
    public double? SetupLostQty { get; set; }
    public double? OperationLostQty { get; set; }
    public bool? FinalProcess { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool? Completed { get; set; }
    public string? Remark { get; set; }

    public PrSchMa? PrSchMa { get; set; }
}
