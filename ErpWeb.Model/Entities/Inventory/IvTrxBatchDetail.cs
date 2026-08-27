namespace ErpWeb.Model.Entities.Inventory;

public class IvTrxBatchDetail
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public int? FromBalLocId { get; set; }
    public int? ToBalLocId { get; set; }
    public int? FromLotId { get; set; }
    public int? ToLotId { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public int BatchNo { get; set; }
    public short TrxLineNo { get; set; }
    public string TrxType { get; set; } = string.Empty;
    public string? ProdCode { get; set; }
    public string? ProdDesc { get; set; }
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? FrWarehouse { get; set; }
    public string? FrLocation { get; set; }
    public string? FrLotNo { get; set; }
    public decimal? FrStdQty { get; set; }
    public string? FrStdUom { get; set; }
    public decimal? FrPurQty { get; set; }
    public string? FrPurUom { get; set; }
    public string? ToWarehouse { get; set; }
    public string? ToLocation { get; set; }
    public string? ToLotNo { get; set; }
    public decimal? ToStdQty { get; set; }
    public string? ToStdUom { get; set; }
    public decimal? ToPurQty { get; set; }
    public string? ToPurUom { get; set; }
    public string? IStatus { get; set; }
    public string? IClassCode { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? DoNo { get; set; }
    public string? InvNo { get; set; }
    public string? SoNo { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? SoLineNo { get; set; }
    public short? PoLineNo { get; set; }
    public string? Remarks { get; set; }
    public decimal? Cost { get; set; }
    public decimal? CostPrice { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? BaseUnitPrices { get; set; }
    public string? Currency { get; set; }
    public string? LocationCode { get; set; }

    public IvTrxBatch Batch { get; set; } = null!;
    public IvBalLoc? FromBalLoc { get; set; }
    public IvBalLoc? ToBalLoc { get; set; }
    public IvLot? FromLot { get; set; }
    public IvLot? ToLot { get; set; }
}
