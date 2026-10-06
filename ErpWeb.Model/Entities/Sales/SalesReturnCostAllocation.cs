namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Immutable allocation of a customer-return quantity/value to one exact outbound valuation fact.
/// A return line may have several rows when the original sale was split by financial costing.
/// Rollback appends a linked reversal row; original rows are never edited or deleted.
/// </summary>
public sealed class SalesReturnCostAllocation
{
    public long Id { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ReturnDocumentType { get; set; } = string.Empty;
    public string ReturnDocumentNo { get; set; } = string.Empty;
    public int ReturnDocumentLine { get; set; }
    public int ReturnCostingRevision { get; set; }

    public long OriginalValuationFactId { get; set; }
    public string OriginalOwnerType { get; set; } = string.Empty;
    public string OriginalOwnerDocumentNo { get; set; } = string.Empty;
    public string? OriginalOwnerDocumentLine { get; set; }

    public decimal ReturnedBaseQty { get; set; }
    public decimal ReturnedCostAmount { get; set; }
    public long StockPostingId { get; set; }
    public long? ReturnValuationFactId { get; set; }
    public long? ReversesAllocationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public StockLedger.StockValuationFact OriginalValuationFact { get; set; } = null!;
    public StockLedger.StockValuationFact? ReturnValuationFact { get; set; }
    public SalesReturnCostAllocation? ReversesAllocation { get; set; }
}
