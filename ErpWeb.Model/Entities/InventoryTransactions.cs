namespace ErpWeb.Model.Entities;

public enum DocumentType
{
    OB = 1,
    GRN = 2,
    GI = 3,
    ST = 4,
    SA = 5
}

public enum DocumentStatus
{
    DRAFT = 0,
    SUBMITTED = 1,
    APPROVED = 2,
    POSTED = 3,
    CANCELLED = 4,
    REVERSED = 5
}

public enum AdjustmentDirection
{
    Increase = 1,
    Decrease = 2
}

public enum StockTakeStatus
{
    DRAFT = 0,
    COUNTING = 1,
    COMPLETED = 2,
    PENDING_APPROVAL = 3,
    APPROVED = 4,
    ADJUSTMENT_GENERATED = 5,
    POSTED = 6,
    CANCELLED = 9
}

public class InventoryDocument : BranchScopedEntity
{
    public string DocNo { get; set; } = null!;
    public DocumentType DocType { get; set; }
    public DateTime DocDate { get; set; }

    public long? WarehouseId { get; set; }
    public long? SourceWarehouseId { get; set; }
    public long? DestinationWarehouseId { get; set; }
    public long? SourceLocationId { get; set; }
    public long? DestinationLocationId { get; set; }

    public string? ReferenceNo { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.DRAFT;
    public string? Remarks { get; set; }

    public bool AllowZeroCost { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public long? ReversalOfDocumentId { get; set; }
    public long? StockTakeId { get; set; }

    public ICollection<InventoryDocumentLine> Lines { get; set; } = [];
}

public class InventoryDocumentLine : BranchScopedEntity
{
    public long DocumentId { get; set; }
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }
    public long UOMId { get; set; }

    public decimal Qty { get; set; }
    public decimal ConversionRateUsed { get; set; }
    public decimal QtyInBase { get; set; }

    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }

    public long LocationId { get; set; }

    public string? LotNo { get; set; }
    public long? LotId { get; set; }

    public AdjustmentDirection? Direction { get; set; }
    public long? ReasonCodeId { get; set; }
    public string? Remarks { get; set; }

    public ICollection<InventoryDocumentLineLotSplit> LotSplits { get; set; } = [];
}

public class StockMovementAllocation : BranchScopedEntity
{
    public long DocumentLineId { get; set; }
    public long StockLedgerId { get; set; }
    public long? SourceLotId { get; set; }
    public long? TargetLotId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
}

public class StockLedger : BranchScopedEntity
{
    public DateTime TransactionDate { get; set; }
    public long LedgerSequence { get; set; }

    public DocumentType DocType { get; set; }
    public string DocNo { get; set; } = null!;
    public int LineNo { get; set; }
    public long DocumentId { get; set; }
    public long DocumentLineId { get; set; }

    public string? ReferenceNo { get; set; }
    public string? PostedBy { get; set; }
    public DateTime PostedAtUtc { get; set; }

    public long ItemVariantId { get; set; }
    public string SKU { get; set; } = null!;
    public string ItemDescription { get; set; } = null!;

    public long UOMId { get; set; }
    public string UOMCode { get; set; } = null!;
    public decimal UnitQty { get; set; }
    public decimal ConversionRateUsed { get; set; }
    public decimal QtyInBase { get; set; }
    public decimal QtyOutBase { get; set; }

    public long WarehouseId { get; set; }
    public string WarehouseCode { get; set; } = null!;
    public long LocationId { get; set; }
    public string LocationCode { get; set; } = null!;

    public long? LotId { get; set; }
    public string? LotNo { get; set; }

    public long? ReasonCodeId { get; set; }
    public string? ReasonCodeValue { get; set; }

    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "MYR";
    public decimal ExchangeRate { get; set; } = 1m;
    public decimal BaseAmount { get; set; }

    public CostingMethod CostingMethod { get; set; } = CostingMethod.MOVING_AVG;
}

public class StockBalance : BranchScopedEntity
{
    public long WarehouseId { get; set; }
    public long LocationId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal QtyOnHand { get; set; }
    public decimal ReservedQty { get; set; }

    public DateTime LastUpdatedAtUtc { get; set; }

    public decimal AvailableQty => QtyOnHand - ReservedQty;
}

public class ItemCost : BranchScopedEntity
{
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal AverageCost { get; set; }
    public decimal LastCost { get; set; }

    public DateTime LastUpdatedAtUtc { get; set; }
    public long? LastDocumentId { get; set; }
}

public class DocumentSequence : BranchScopedEntity
{
    public string DocType { get; set; } = null!;
    public string Prefix { get; set; } = null!;
    public int YearMonth { get; set; }
    public long CurrentNumber { get; set; }
    public int NumberLength { get; set; } = 4;
}

public class LedgerSequence : BranchScopedEntity
{
    public long CurrentNumber { get; set; }
}

public class StockTake : BranchScopedEntity
{
    public string StockTakeNo { get; set; } = null!;
    public DateTime CountDate { get; set; }
    public long WarehouseId { get; set; }
    public StockTakeStatus Status { get; set; } = StockTakeStatus.DRAFT;
    public long? GeneratedAdjustmentDocumentId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? Remarks { get; set; }

    public ICollection<StockTakeLine> Lines { get; set; } = [];
}

public class StockTakeLine : BranchScopedEntity
{
    public long StockTakeId { get; set; }
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }
    public long LocationId { get; set; }
    public long? LotId { get; set; }
    public decimal SystemQty { get; set; }
    public decimal CountedQty { get; set; }
    public decimal VarianceQty { get; set; }
    public long? ReasonCodeId { get; set; }
}

/// <summary>Minimal period entity for Phase 2 posting gates. Close/snapshot is Phase 4.</summary>
public class InventoryPeriod : CompanyScopedEntity
{
    public int FiscalYear { get; set; }
    public int FiscalMonth { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsClosed { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
}

/// <summary>
/// Historical warehouse/item balances at period close.
/// Built from StockLedger ≤ EndDate — never copied from live StockBalance.
/// </summary>
public class StockSnapshot : BranchScopedEntity
{
    public long PeriodId { get; set; }
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }
    public decimal ClosingQty { get; set; }
    public decimal ClosingCost { get; set; }
    public decimal ClosingValue { get; set; }
    public DateTime SnapshotDate { get; set; }
}

public enum LotStatus
{
    ACTIVE = 1,
    EXPIRED = 2,
    QUARANTINE = 3,
    CLOSED = 4
}

public class Lot : CompanyScopedEntity
{
    public long ItemVariantId { get; set; }
    public string LotNo { get; set; } = null!;
    public string? SupplierRef { get; set; }
    public string? SourceDocType { get; set; }
    public string? SourceDocNo { get; set; }
    public int? SourceDocLineNo { get; set; }
    public DateTime ReceivedDate { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal ReceivedUnitCost { get; set; }
    public string CostCurrencyCode { get; set; } = "MYR";
    public DateTime? ManufactureDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public LotStatus Status { get; set; } = LotStatus.ACTIVE;
    public string? Remarks { get; set; }
}

public class LotBalance : BranchScopedEntity
{
    public long LotId { get; set; }
    public long WarehouseId { get; set; }
    public long LocationId { get; set; }
    public decimal QtyOnHand { get; set; }
}

/// <summary>Draft multi-lot split for a document line (Phase 3). One line → many ledger rows.</summary>
public class InventoryDocumentLineLotSplit : BranchScopedEntity
{
    public long DocumentLineId { get; set; }
    public long? LotId { get; set; }
    public string? LotNo { get; set; }
    public decimal Qty { get; set; }
}
