namespace ErpWeb.Core.Services;

/// <summary>
/// Structured failure category a producer supplies when the plain <c>StockLedgerErrorCodes</c> value
/// alone cannot describe the operator-visible reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> One code covers several unrelated situations. <c>INSUFFICIENT_BASE_QTY</c>
/// is thrown for a historical replay block, a FIFO layer shortage, a production contribution
/// shortage and a production balance going negative. Mapping by code alone would tell an operator the
/// wrong thing, so classification is <em>code + action + structured family</em>.
/// </para>
/// <para>
/// This is a <b>presentation</b> category. It never participates in a posting, rollback, costing or
/// quantity decision, and it never replaces the persisted <c>StockLedgerErrorCodes</c> value.
/// </para>
/// </remarks>
public enum ErpErrorFamily
{
    /// <summary>Not enough stock right now for a stock-out style action.</summary>
    StockShortageNow,

    /// <summary>Removing or replaying a historical event would make an intermediate balance negative.</summary>
    HistoricalNegativeStock,

    /// <summary>The cost of the movement cannot be proven (missing opening/receipt layer, missing revision).</summary>
    MissingReceiptCost,

    /// <summary>Stock quantity and cost records disagree; needs a costing review, not a quantity edit.</summary>
    CostStateNeedsReview,

    /// <summary>Backdated/older transaction shape is not supported by the current costing setup.</summary>
    UnsupportedReplay,

    /// <summary>A later document depends on this record.</summary>
    DependentDocument,

    /// <summary>Inventory period is closed for the date.</summary>
    ClosedPeriod,

    /// <summary>The document date is in the future for a stock movement.</summary>
    FutureDocumentDate,

    /// <summary>An approved unit conversion is missing.</summary>
    UomConversion,

    /// <summary>No approved exchange rate covers the date.</summary>
    CurrencyRate,

    /// <summary>The document or its stock balance changed while the operator was working.</summary>
    StaleDocument,

    /// <summary>Already posted / already reversed / request reused.</summary>
    AlreadyProcessed,

    /// <summary>Stock changes are blocked by a stock count scope.</summary>
    CountLocked,

    /// <summary>The operator (or session) is not allowed to perform the action.</summary>
    PermissionDenied,

    /// <summary>No valid costing method / policy covers the date.</summary>
    CostMethodSetup,

    /// <summary>Records need reconciliation by an authorised user.</summary>
    ReconciliationRequired,

    /// <summary>Another stock transaction holds the branch; the action was not applied.</summary>
    StockBusy,

    /// <summary>The stock identity or the company/branch write scope could not be verified.</summary>
    StockIdentityUnverified,

    /// <summary>Anything not classified. Always truthful, always generic.</summary>
    UnknownFailure
}

/// <summary>The operation the operator attempted. Used for the headline verb only.</summary>
public enum ErpErrorAction
{
    Unknown,
    Post,
    Rollback,
    Cancel,
    Save,
    Delete,
    StockCount,
    Reconcile
}

/// <summary>The module that raised the failure. Used for wording, never for permissions.</summary>
public enum ErpErrorOrigin
{
    Unknown,
    Inventory,
    Sales,
    Purchase,
    Production
}

/// <summary>Document families a stock/costing failure can name in a headline.</summary>
public enum ErpDocumentKind
{
    Unknown,
    GoodsReceipt,
    NonStockGoodsReceipt,
    MiscellaneousIssue,
    MiscellaneousReceipt,
    StockTransfer,
    StockAdjustment,
    StockReturn,
    VendorReturn,
    Scrap,
    StockCount,
    DeliveryOrder,
    SalesInvoice,
    SalesCreditDebitNote,
    PurchaseInvoice,
    PurchaseCreditDebitNote,
    PurchaseOrder,
    WorkOrder,
    WorkOrderTransfer,
    MaterialIssue,
    DailyProduction,
    FinishedGoodReceipt,
    ProductionStockReturn,
    ProductionStockAdjustment
}

/// <summary>
/// The phase a replay refusal happened in. Supplied only where a producer already knows it from its
/// own structured data — never parsed out of an error string.
/// </summary>
public enum ErpRejectionPhase
{
    /// <summary>Not supplied; the presenter omits phase-specific wording.</summary>
    NotSpecified,

    /// <summary>The removal of the operator's own document is the problem.</summary>
    Removing,

    /// <summary>A later event cannot be unwound against the current balance.</summary>
    Rewinding,

    /// <summary>Re-applying a later event would go negative.</summary>
    Replaying
}

/// <summary>A safe, pre-formatted label/value row. Monetary rows are hidden without cost rights.</summary>
public sealed record ErpOperatorErrorDetail(string Label, string Value, bool IsMonetary = false);

/// <summary>
/// A related document the operator may open. <see cref="TargetRoute"/> is only set when the producer
/// verified ownership; the UI must still check access before navigating.
/// </summary>
public sealed record ErpRelatedDocumentHint(
    string DocumentType,
    string DocumentNumber,
    string? TargetRoute = null);

/// <summary>
/// The immutable operator-facing error. Deliberately free of exceptions, tenant ids, posting ids,
/// raw event ids and SQL fragments.
/// </summary>
/// <remarks>
/// Nothing here may change a posting decision, a status transition or a stored quantity/cost. It is
/// the outward text of a failure that has already been decided by the existing business code.
/// </remarks>
public sealed record ErpOperatorError
{
    /// <summary>Stable, localisation-ready reason key (for example <c>INVENTORY.STOCK_SHORTAGE_NOW</c>).</summary>
    public required string PresentationCode { get; init; }

    /// <summary>What failed, naming the document when it is proven — "Cannot roll back Goods Receipt 1."</summary>
    public required string Title { get; init; }

    /// <summary>Why it failed, in business language.</summary>
    public required string Explanation { get; init; }

    /// <summary>What the operator can do next.</summary>
    public required string NextStep { get; init; }

    /// <summary>Support reference correlating to the server log. Blank when none was issued.</summary>
    public string? ReferenceId { get; init; }

    /// <summary>
    /// Advisory only: refresh and resubmit. Never implies the backend will retry, and never true when
    /// the outcome of the failed attempt is unknown.
    /// </summary>
    public bool MayRetryAfterRefresh { get; init; }

    public IReadOnlyList<ErpOperatorErrorDetail> Details { get; init; } = [];

    public IReadOnlyList<ErpRelatedDocumentHint> RelatedDocuments { get; init; } = [];

    public bool HasReference => !string.IsNullOrWhiteSpace(ReferenceId);

    public bool HasDetails => Details.Count > 0 || RelatedDocuments.Count > 0;

    /// <summary>
    /// One-line form for a toast/banner: headline, reason, guidance and reference in a fixed order.
    /// </summary>
    public string ToOperatorText()
    {
        var parts = new List<string>(5) { Title, Explanation };
        foreach (var detail in Details)
        {
            parts.Add($"{detail.Label}: {detail.Value}.");
        }

        if (!string.IsNullOrWhiteSpace(NextStep))
        {
            parts.Add(NextStep);
        }

        if (HasReference)
        {
            parts.Add($"Reference: {ReferenceId}.");
        }

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Everything the presenter may use. Populated only from values the producer already proved — the
/// presenter performs no lookups, no parsing and no calculation of its own.
/// </summary>
public sealed record StockOperatorFailureContext
{
    /// <summary>The persisted <c>StockLedgerErrorCodes</c> value, if the failure had one. Never shown.</summary>
    public string? TechnicalCode { get; init; }

    /// <summary>The persisted retryability flag. Used only to decide the advisory refresh hint.</summary>
    public bool Retryable { get; init; }

    public ErpErrorAction Action { get; init; } = ErpErrorAction.Unknown;

    public ErpErrorOrigin Origin { get; init; } = ErpErrorOrigin.Unknown;

    /// <summary>Structured reason supplied by the producer. Takes precedence over the code default.</summary>
    public ErpErrorFamily? Family { get; init; }

    public ErpRejectionPhase RejectionPhase { get; init; } = ErpRejectionPhase.NotSpecified;

    public ErpDocumentKind DocumentKind { get; init; } = ErpDocumentKind.Unknown;

    public string? DocumentNumber { get; init; }

    public DateTime? DocumentDate { get; init; }

    public int? LineNo { get; init; }

    public string? ItemCode { get; init; }

    public string? ItemDescription { get; init; }

    public string? Warehouse { get; init; }

    public string? Location { get; init; }

    public string? LotNo { get; init; }

    public string? StockStatus { get; init; }

    public string? Uom { get; init; }

    public decimal? Available { get; init; }

    public decimal? Required { get; init; }

    public decimal? Shortage { get; init; }

    /// <summary>The later transaction that depends on this record, when ownership was verified.</summary>
    public ErpDocumentKind AffectedDocumentKind { get; init; } = ErpDocumentKind.Unknown;

    public string? AffectedDocumentNumber { get; init; }

    public DateTime? AffectedTransactionDate { get; init; }

    /// <summary>Last date of the closed inventory period, when the guard reported it.</summary>
    public DateTime? ClosedThrough { get; init; }

    public string? CurrencyCode { get; init; }

    public string? HomeCurrencyCode { get; init; }

    public string? FromUom { get; init; }

    public string? ToUom { get; init; }

    /// <summary>True only when the operator holds cost rights. Gates every monetary row.</summary>
    public bool CanViewCost { get; init; }

    /// <summary>Extra safe rows. Any row flagged monetary is dropped unless <see cref="CanViewCost"/>.</summary>
    public IReadOnlyList<ErpOperatorErrorDetail>? ExtraDetails { get; init; }

    public IReadOnlyList<ErpRelatedDocumentHint>? RelatedDocuments { get; init; }

    /// <summary>Support reference issued by the caller for this failed action, if any.</summary>
    public string? ReferenceId { get; init; }
}
