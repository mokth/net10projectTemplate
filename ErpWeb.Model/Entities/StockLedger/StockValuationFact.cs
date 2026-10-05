namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>
/// Immutable monetary leg for one sealed stock posting. Quantity is stored as a positive
/// magnitude; <see cref="Direction"/> supplies the accounting sign.
/// </summary>
public sealed class StockValuationFact
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;

    public long LedgerEpochId { get; set; }
    public StockLedgerEpoch? LedgerEpoch { get; set; }
    public long StockPostingId { get; set; }
    public StockPosting? StockPosting { get; set; }
    public int PostingLineNo { get; set; }
    public int SplitOrdinal { get; set; }

    public string SourceLineId { get; set; } = string.Empty;
    public string SourceDocumentType { get; set; } = string.Empty;
    public string SourceDocumentId { get; set; } = string.Empty;
    public string SourceDocumentNo { get; set; } = string.Empty;
    public string? SourceDocumentLine { get; set; }

    public DateTime EffectiveAt { get; set; }
    public DateTime BusinessDate { get; set; }
    public string PeriodKey { get; set; } = string.Empty;

    public string ItemCode { get; set; } = string.Empty;
    public string? WarehouseCode { get; set; }
    public string? LocationCode { get; set; }
    public int? LotId { get; set; }
    public string? LotNo { get; set; }
    public string? ItemStatus { get; set; }
    public string BaseUom { get; set; } = string.Empty;

    public string MovementCode { get; set; } = string.Empty;
    public int Direction { get; set; }
    public decimal BaseQty { get; set; }

    public string CostMethod { get; set; } = StockCostMethods.MovingAverage;
    public decimal UnitCost { get; set; }
    public decimal CostAmount { get; set; }

    public string? TransactionCurrency { get; set; }
    public decimal? TransactionCostAmount { get; set; }
    public decimal? ExchangeRate { get; set; }
    public string? BaseCurrency { get; set; }
    public decimal BaseCostAmount { get; set; }

    public string ValuationSource { get; set; } = string.Empty;
    public string ValuationStatus { get; set; } = StockValuationStatuses.Valued;
    public int ValuationVersion { get; set; } = 1;

    // Inventory identities intentionally match their current int primary keys.
    public int? InventoryHistoryId { get; set; }
    public Inventory.IvTrxHistory? InventoryHistory { get; set; }
    public int? FromBalLocId { get; set; }
    public Inventory.IvBalLoc? FromBalLoc { get; set; }
    public int? ToBalLocId { get; set; }
    public Inventory.IvBalLoc? ToBalLoc { get; set; }
    public Inventory.IvLot? Lot { get; set; }

    // Posting and production identities use their current long primary keys.
    public long? ProductionMovementId { get; set; }
    public long? WorkOrderId { get; set; }
    public long? WorkOrderOperationId { get; set; }
    public long? ProductionPostingLinkId { get; set; }

    public long? OriginalValuationFactId { get; set; }
    public StockValuationFact? OriginalValuationFact { get; set; }
    public long? ReversesValuationFactId { get; set; }
    public StockValuationFact? ReversesValuationFact { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public decimal SignedQty => BaseQty * Direction;
    public decimal SignedValue => CostAmount * Direction;
}

public sealed class StockCostState
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string CostMethod { get; set; } = StockCostMethods.MovingAverage;
    public decimal OnHandBaseQty { get; set; }
    public decimal InventoryValue { get; set; }
    public decimal AverageUnitCost { get; set; }
    public long? LastValuationFactId { get; set; }
    public StockValuationFact? LastValuationFact { get; set; }
    public long LastPostingSequence { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class StockCostMethods
{
    public const string MovingAverage = "MOVING_AVERAGE";
}

public static class StockValuationStatuses
{
    public const string Unvalued = "UNVALUED";
    public const string Valued = "VALUED";
    public const string Reversed = "REVERSED";
}

public static class StockValuationSources
{
    public const string ReceiptActual = "RECEIPT_ACTUAL";
    public const string MovingAverage = "MOVING_AVERAGE";
    public const string OriginalReversal = "ORIGINAL_REVERSAL";
    public const string OriginalSaleReturn = "ORIGINAL_SALE_RETURN";
    public const string ProductionActual = "PRODUCTION_ACTUAL";
    public const string PurchasePriceVariance = "PURCHASE_PRICE_VARIANCE";
    public const string LandedCost = "LANDED_COST";
    public const string ManualApproved = "MANUAL_APPROVED";
    public const string OpeningApproved = "OPENING_APPROVED";
    public const string BackfillVerified = "BACKFILL_VERIFIED";
}
