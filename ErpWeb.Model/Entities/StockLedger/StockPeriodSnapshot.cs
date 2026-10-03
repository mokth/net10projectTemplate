namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>Immutable quantity-close generation captured at a sealed branch posting watermark.</summary>
public sealed class StockPeriodSnapshotHdr
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long LedgerEpochId { get; set; }
    public StockLedgerEpoch? LedgerEpoch { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public int Revision { get; set; }
    public long PostingSequenceWatermark { get; set; }
    public string SourceDataHash { get; set; } = string.Empty;
    public string QuantityStatus { get; set; } = "SEALED";
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public ICollection<StockPeriodSnapshotLine> Lines { get; set; } = [];
}

/// <summary>A versioned quantity only; valuation remains outside the P2 close contract.</summary>
public sealed class StockPeriodSnapshotLine
{
    public long Id { get; set; }
    public long HeaderId { get; set; }
    public StockPeriodSnapshotHdr? Header { get; set; }
    public string LedgerArea { get; set; } = string.Empty;
    public string StockIdentity { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string BaseUom { get; set; } = string.Empty;
    public decimal BaseQty { get; set; }
}
