namespace ErpWeb.Core.Costing;

public enum CostingFindingSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public static class CostingFindingCodes
{
    public const string UnsealedPosting = "CD-001";
    public const string HistoryMissingValuation = "CD-002";
    public const string FactWithoutSealedPosting = "CD-003";
    public const string CostStateQuantityMismatch = "CD-004";
    public const string CostStateValueMismatch = "CD-005";
    public const string CostStateAverageMismatch = "CD-006";
    public const string NegativeValuation = "CD-007";
    public const string ReversalLineageBroken = "CD-010";
    public const string ZeroQuantityResidue = "CD-017";
    public const string SalesCogsUnresolved = "CD-022";
    public const string EpochCoverage = "CD-027";
    public const string SalesCogsLineageIncomplete = "CD-039";
    public const string CostStateMissing = "CD-040";
    public const string SourceDocumentMissing = "CD-041";
}

public static class CostingRepairActions
{
    public const string ReviewSourcePosting = "REVIEW_SOURCE_POSTING";
    public const string ReconcileUnsealedPosting = "RECONCILE_UNSEALED_POSTING";
    public const string TraceBrokenReversal = "TRACE_BROKEN_REVERSAL";
    public const string RebuildCostState = "REBUILD_COST_STATE";
    public const string TraceSourceDocument = "TRACE_SOURCE_DOCUMENT";
}

public static class CostingDocumentTypes
{
    public const string GoodsReceipt = "GR";
    public const string MiscReceipt = "MR";
    public const string MiscIssue = "MI";
    public const string Scrap = "SC";
    public const string VendorReturn = "VR";
    public const string Transfer = "TR";
    public const string Adjustment = "ADJ";
    public const string CustomerReturn = "CR";
    public const string SalesInvoice = "SA_INVOICE";
    public const string SalesDeliveryOrder = "SA_DO";
    public const string SalesCreditNote = "SA_CDN";
    public const string PurchaseCreditNote = "PO_CDN";
    public const string MaterialIssue = "IP";
    public const string FinishedGoodReceipt = "FG_RECEIPT";
    public const string DailyProduction = "PRODUCTION_OUTPUT";
}

public static class CostingEpochCoverage
{
    public const string V2 = "V2";
    public const string HistoryBeforeCutover = "HISTORY_BEFORE_CUTOVER";
    public const string NoActiveEpoch = "NO_ACTIVE_EPOCH";
    public const string UnresolvedCrossEpoch = "UNRESOLVED_CROSS_EPOCH";
}

public sealed record CostingFinding(
    string Code,
    CostingFindingSeverity Severity,
    bool IsBlocking,
    string? ItemCode,
    string? WarehouseCode,
    string? LocationCode,
    string? LotNo,
    DateTime? EffectiveAt,
    string? SourceDocumentType,
    string? SourceDocumentNo,
    long? StockPostingId,
    long? ValuationFactId,
    string Summary,
    string Explanation,
    decimal? ExpectedQty,
    decimal? ActualQty,
    decimal? ExpectedValue,
    decimal? ActualValue,
    string RecommendedAction,
    string? CostMethod = null,
    CostingRepairTargetKind RepairTargetKind = CostingRepairTargetKind.DiagnosticOnly,
    string? RepairAction = null);

public sealed record CostingHealthQuery(
    string? ItemCode = null,
    string? WarehouseCode = null,
    DateTime? From = null,
    DateTime? To = null,
    string? FindingCode = null,
    string? SourceDocumentNo = null,
    string? SourceDocumentType = null,
    CostingFindingSeverity? Severity = null);

public sealed class CostingHealthPage
{
    public bool Denied { get; init; }
    public string? Error { get; init; }
    public bool MonetaryValuesVisible { get; init; }
    public string EpochCoverage { get; init; } = CostingEpochCoverage.NoActiveEpoch;
    public IReadOnlyList<CostingFinding> Findings { get; init; } = [];
}

public sealed record CostingTraceQuery(
    string ItemCode,
    DateTime? From = null,
    DateTime? To = null,
    string? WarehouseCode = null);

public sealed record CostingTraceAnchor(
    decimal OpeningQty,
    decimal OpeningValue,
    decimal OpeningAverage);

public sealed record CostingTraceLine(
    long StockPostingId,
    long PostingSequence,
    int PostingLineNo,
    int SplitOrdinal,
    DateTime EffectiveAt,
    string SourceDocumentType,
    string SourceDocumentNo,
    string MovementCode,
    string? WarehouseCode,
    decimal QtyIn,
    decimal QtyOut,
    decimal QuantityAfter,
    decimal? ValueIn,
    decimal? ValueOut,
    decimal? InventoryValueAfter,
    decimal? UnitCost,
    decimal? AverageAfter,
    string ValuationSource,
    string ValuationStatus,
    bool IsReversal);

public sealed class CostingTracePage
{
    public bool Denied { get; init; }
    public string? Error { get; init; }
    public bool MonetaryValuesVisible { get; init; }
    public string EpochCoverage { get; init; } = CostingEpochCoverage.NoActiveEpoch;
    public CostingTraceAnchor Anchor { get; init; } = new(0m, 0m, 0m);
    public IReadOnlyList<CostingTraceLine> Lines { get; init; } = [];
}

public sealed record CostingCogsProofLine(
    int InvoiceLine,
    string ItemCode,
    string OwnerType,
    string OwnerNo,
    string? OwnerLine,
    decimal ExpectedQty,
    decimal ActualQty,
    decimal? Cogs,
    bool QuantityProven,
    string Lineage);

public sealed class CostingCogsProof
{
    public bool Denied { get; init; }
    public string? Error { get; init; }
    public bool MonetaryValuesVisible { get; init; }
    public string InvoiceNo { get; init; } = string.Empty;
    public bool IsFullyProven { get; init; }
    public IReadOnlyList<CostingCogsProofLine> Lines { get; init; } = [];
    public IReadOnlyList<CostingFinding> Findings { get; init; } = [];
}

public interface ICostingDiagnosticService
{
    Task<CostingHealthPage> SearchAsync(CostingHealthQuery? query = null, CancellationToken cancellationToken = default);

    Task<CostingCogsProof> ProveInvoiceCogsAsync(string invoiceNo, CancellationToken cancellationToken = default);
}

public interface ICostingTraceService
{
    Task<CostingTracePage> GetItemTimelineAsync(CostingTraceQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists sealed cost postings for an item that are later than <paramref name="asOf"/>,
    /// newest first (rollback order for a backdated post).
    /// </summary>
    Task<CostingBackdateImpactPage> GetBackdateImpactAsync(
        string itemCode,
        DateTime asOf,
        CancellationToken cancellationToken = default);
}

public sealed record CostingBackdateBlocker(
    long StockPostingId,
    DateTime EffectiveAt,
    string SourceDocumentType,
    string SourceDocumentNo,
    string DocumentLabel,
    string PostingRole,
    bool AlreadyReversed);

public sealed class CostingBackdateImpactPage
{
    public bool Denied { get; init; }
    public string? Error { get; init; }
    public string Guidance { get; init; } = string.Empty;
    public DateTime AsOf { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public IReadOnlyList<CostingBackdateBlocker> Blockers { get; init; } = [];
}
