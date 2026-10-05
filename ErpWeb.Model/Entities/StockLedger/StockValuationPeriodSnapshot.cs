namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>Immutable financial close captured from sealed valuation facts.</summary>
public sealed class StockValuationPeriodSnapshotHdr
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
    public string ValuationStatus { get; set; } = "SEALED";
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public ICollection<StockValuationPeriodSnapshotLine> Lines { get; set; } = [];
}

public sealed class StockValuationPeriodSnapshotLine
{
    public long Id { get; set; }
    public long HeaderId { get; set; }
    public StockValuationPeriodSnapshotHdr? Header { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string BaseUom { get; set; } = string.Empty;
    public string CostMethod { get; set; } = StockCostMethods.MovingAverage;
    public decimal OpeningQty { get; set; }
    public decimal OpeningValue { get; set; }
    public decimal InQty { get; set; }
    public decimal InValue { get; set; }
    public decimal AdjustmentQty { get; set; }
    public decimal AdjustmentValue { get; set; }
    public decimal OutQty { get; set; }
    public decimal OutValue { get; set; }
    public decimal ClosingQty { get; set; }
    public decimal ClosingValue { get; set; }
}
