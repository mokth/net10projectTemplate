namespace ErpWeb.Model.Entities.Production;

/// <summary>Daily production / completion posting row (<c>dbo.PrSchDailyProd</c>).</summary>
public class PrSchDailyProd
{
    public int Id { get; set; }
    public string? ScheCode { get; set; }
    public string? ProdCode { get; set; }
    public short? RelNo { get; set; }
    public string WcCode { get; set; } = string.Empty;
    public string? WcICode { get; set; }
    public string ProcessCode { get; set; } = string.Empty;
    public int? LineNo { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime? CompletedTime { get; set; }
    public double? GoodQty { get; set; }
    public string? GoodUom { get; set; }
    public double? OnHoldQty { get; set; }
    public string? OnHoldUom { get; set; }
    public string? Remarks { get; set; }
    public string? Shift { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? LotNo { get; set; }
    public short? RevNo { get; set; }
    public string? ToWarehouse { get; set; }
    public string? ToLocation { get; set; }
    public string? IStatus { get; set; }
    public bool? FinalProcess { get; set; }
    public string? ReasonCode { get; set; }
    public string? TrxType { get; set; }
    public string? ICode { get; set; }
    public string? RefNo { get; set; }
    public bool? LotCompleted { get; set; }
    public int? BatchNo { get; set; }
    public string? PreWCenter { get; set; }
    public string? PreProcess { get; set; }
    public string? PreICode { get; set; }
    public double? PreQty { get; set; }
    public string? PreQtyUom { get; set; }
    public double? PreWt { get; set; }
    public string? PreWtUom { get; set; }
    public short? PreRevNo { get; set; }
    public DateTime? PreDate { get; set; }
    public string? PreLotNo { get; set; }
    public string? PreScheCode { get; set; }
    public double? ScrapStdQty { get; set; }
    public string? ScrapStdQtyUom { get; set; }
    public double? ScrapWtQty { get; set; }
    public string? ScrapWtQtyUom { get; set; }
    public string? MachineCode { get; set; }
    public string? PreTrxType { get; set; }
    public string? Operator { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public double? GoodScrapQty { get; set; }
    public double? GoodRejectQty { get; set; }
    public decimal? UnitPrice { get; set; }
    public int? NumOperator { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
