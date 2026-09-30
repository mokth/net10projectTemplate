namespace ErpWeb.Model.Entities.Planning;

/// <summary>Product-definition work-centre routing line (<c>dbo.PrDefWCenter</c>).</summary>
public class PrDefWcenter
{
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? Class { get; set; }
    public int? SeqNo { get; set; }
    public double? StdPackSize { get; set; }
    public string? StdUom { get; set; }
    public string? SCode { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }

    public PrDefMa? PrDefMa { get; set; }
}
