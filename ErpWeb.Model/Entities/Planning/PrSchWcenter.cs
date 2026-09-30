namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work-order work-centre snapshot line (<c>dbo.PrSchWCenter</c>).</summary>
public class PrSchWcenter
{
    public string ScheCode { get; set; } = string.Empty;
    public short RelNo { get; set; }
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? Class { get; set; }
    public int? SeqNo { get; set; }
    public double? StdPackSize { get; set; }
    public string? StdUom { get; set; }
    public double? ScheQty { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? CompleteDate { get; set; }
    public bool? Completed { get; set; }

    public PrSchMa? PrSchMa { get; set; }
}
