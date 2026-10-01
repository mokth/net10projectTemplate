namespace ErpWeb.Model.Entities.Production;

/// <summary>WIP balance by item / work centre / process / lot (<c>dbo.WIPItemBalLoc</c>).</summary>
public class WipItemBalLoc
{
    public string ICode { get; set; } = string.Empty;
    public string WcCode { get; set; } = string.Empty;
    public string ProcessCode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public short RevNo { get; set; }
    public int? ProcessSeq { get; set; }
    public double? StdQty { get; set; }
    public string StdUom { get; set; } = string.Empty;
    public double? WtQty { get; set; }
    public string? WtUom { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string ScheCode { get; set; } = string.Empty;
    public short RelNo { get; set; }
    public string? WcICode { get; set; }
    public string? Remark { get; set; }
    public string? ProdCode { get; set; }
    public DateTime? TransactionDate { get; set; }
    public string? TrxType { get; set; }
    public decimal? UnitPrice { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
