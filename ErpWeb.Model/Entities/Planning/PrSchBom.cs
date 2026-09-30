namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work-order BOM snapshot line (<c>dbo.PrSchBOM</c>).</summary>
public class PrSchBom
{
    public string ScheCode { get; set; } = string.Empty;
    public short RelNo { get; set; }
    public string ProdCode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string WciCode { get; set; } = string.Empty;
    public string ProcessCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string? IName { get; set; }
    public double? StdQty { get; set; }
    public string? StdUom { get; set; }
    public string? Warehouse { get; set; }
    public bool? BomDefault { get; set; }
    public bool? WipBomDefault { get; set; }
    public short? Tolerance { get; set; }

    public PrSchMa? PrSchMa { get; set; }
}
