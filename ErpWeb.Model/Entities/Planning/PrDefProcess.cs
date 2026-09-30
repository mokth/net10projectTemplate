namespace ErpWeb.Model.Entities.Planning;

/// <summary>Product-definition process routing line (<c>dbo.PrDefProcess</c>).</summary>
public class PrDefProcess
{
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string WciCode { get; set; } = string.Empty;
    public string ProcessCode { get; set; } = string.Empty;
    public int? SeqNo { get; set; }
    public double? SetupLostQty { get; set; }
    public double? OperationLostQty { get; set; }
    public bool? FinalProcess { get; set; }
    public string? SCode { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
    public string? Remark { get; set; }

    public PrDefMa? PrDefMa { get; set; }
}
