namespace ErpWeb.Model.Entities.Inventory;

public class IvTrxHistory
{
    // Exact FG value is independent of rounded unit prices. Null preserves legacy semantics.
    public decimal? ExactTransferredValue { get; set; }
    public string? ValuationStatus { get; set; }
    public decimal? EvidenceBaseQty { get; set; }
    public string? EvidenceBaseUom { get; set; }
    public string? PriceEvidence { get; set; }
    public int Id { get; set; }
    public int? FromBalLocId { get; set; }
    public int? ToBalLocId { get; set; }
    public int? FromLotId { get; set; }
    public int? ToLotId { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public int BatchNo { get; set; }
    public short TrxLineNo { get; set; }
    public DateTime TrxDtTime { get; set; }
    public string TrxType { get; set; } = string.Empty;
    public string BatchStatus { get; set; } = string.Empty;
    public string? RefNo { get; set; }
    public string? ProdCode { get; set; }
    public string? ProdDesc { get; set; }
    public string ICode { get; set; } = string.Empty;
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
    public decimal? AsNowCost { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? BaseUnitPrices { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }

    // V2 rows are append-only; these fields are null on legacy history.
    public byte? LedgerVersion { get; set; }
    public long? LedgerEpochId { get; set; }
    public long? StockPostingId { get; set; }
    public int? PostingLineNo { get; set; }
    public int? DocumentRevision { get; set; }
    public string? EntryRole { get; set; }
    public int? ReversesHistoryId { get; set; }
    public IvTrxHistory? ReversesHistory { get; set; }

    public IvBalLoc? FromBalLoc { get; set; }
    public IvBalLoc? ToBalLoc { get; set; }
    public IvLot? FromLot { get; set; }
    public IvLot? ToLot { get; set; }
}
