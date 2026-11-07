namespace ErpWeb.Core.StockLedger;

public static class StockLedgerErrorCodes
{
    public const string LedgerDisabled = "LEDGER_DISABLED";
    public const string StockBusy = "STOCK_BUSY";
    public const string RequestIdReused = "REQUEST_ID_REUSED";
    public const string DocumentAlreadyPosted = "DOCUMENT_ALREADY_POSTED";
    public const string StaleDocument = "STALE_DOCUMENT";
    public const string ClosedPeriod = "CLOSED_PERIOD";
    public const string BackdatedStockEvent = "BACKDATED_STOCK_EVENT";
    public const string CountScopeFrozen = "COUNT_SCOPE_FROZEN";
    public const string InvalidStockIdentity = "INVALID_STOCK_IDENTITY";
    public const string InsufficientBaseQty = "INSUFFICIENT_BASE_QTY";
    public const string InvalidUomConversion = "INVALID_UOM_CONVERSION";
    public const string ReversalDependency = "REVERSAL_DEPENDENCY";
    public const string ReversalAlreadyExists = "REVERSAL_ALREADY_EXISTS";
    public const string LedgerMismatch = "LEDGER_MISMATCH";
    public const string LegacyLineageUnverified = "LEGACY_LINEAGE_UNVERIFIED";
    public const string HistoryBeforeCutover = "HISTORY_BEFORE_CUTOVER";
    public const string ValuationRequired = "VALUATION_REQUIRED";
    public const string CountSnapshotChanged = "COUNT_SNAPSHOT_CHANGED";
    public const string CostPolicyMissing = "COST_POLICY_MISSING";
    public const string CostPolicyInvalid = "COST_POLICY_INVALID";
    public const string CostMethodNotImplemented = "COST_METHOD_NOT_IMPLEMENTED";
}

public sealed record StockLedgerError(string Code, string Message, bool Retryable = false)
{
    /// <summary>
    /// Which replay step refused the write. Supplied only by producers that already know it from their
    /// own structured input; it never changes <see cref="Code"/>, <see cref="Message"/> or the decision
    /// to throw, and it exists so the operator message can distinguish "this document" from
    /// "a later transaction".
    /// </summary>
    public StockLedgerErrorPhase Phase { get; init; } = StockLedgerErrorPhase.Unspecified;

    /// <summary>
    /// The replay event that would have gone negative, for server-log correlation only. Never shown to
    /// an operator and never used to infer a document by string parsing.
    /// </summary>
    public long? EventId { get; init; }
}

/// <summary>Narrow replay-step classification carried as optional metadata. No arithmetic effect.</summary>
public enum StockLedgerErrorPhase
{
    Unspecified,
    Removing,
    Rewinding,
    Replaying
}


public sealed class StockLedgerException : Exception
{
    public StockLedgerException(StockLedgerError error)
        : base(error.Message) => Error = error;

    public StockLedgerError Error { get; }
}
