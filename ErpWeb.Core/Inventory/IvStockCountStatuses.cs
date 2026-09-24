namespace ErpWeb.Core.Inventory;

/// <summary>
/// Lifecycle states of a stock-count document. Deliberately NOT <c>IvBatchStatuses</c>
/// (NEW/POSTED/CANCELLED) — the count document has its own richer lifecycle.
///
///   —            → DRAFT        Generate + Save
///   DRAFT        → COUNTED      Count-entry Save (at least one physical quantity)
///   COUNTED      → POSTED       Post
///   POSTED       → ROLLED_BACK  Rollback
///   ROLLED_BACK  → COUNTED      Re-count
///   ROLLED_BACK  → POSTED       Re-post (allocates a NEW batch number)
///   DRAFT/COUNTED/ROLLED_BACK → CANCELLED
///   DRAFT        → (row deleted)
/// </summary>
public static class IvStockCountStatuses
{
    public const string Draft = "DRAFT";
    public const string Counted = "COUNTED";
    public const string Posted = "POSTED";
    public const string RolledBack = "ROLLED_BACK";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [Draft, Counted, Posted, RolledBack, Cancelled];
}

/// <summary>
/// Hard limits for the stock-count document, in the shape of <see cref="IvPostingLimits"/>.
/// These live in code (not the settings registry) because they exist to protect the server and
/// the Blazor circuit, not to express a business preference.
/// </summary>
public static class IvStockCountLimits
{
    /// <summary>
    /// Generate refuses to create a sheet with more candidate lines than this, naming the count and
    /// suggesting a narrower scope. An IncludeZeroQty warehouse-wide generate must not push an
    /// unbounded result into the circuit.
    /// </summary>
    public const int MaxCountLines = 20_000;

    /// <summary>
    /// Posting is refused when <c>CountDate</c> is older than this many days. The ADJ batch carries
    /// <c>TrxDtTime = CountDate</c>, and the stock-move helpers overwrite <c>IvBalLoc.TransDate</c>
    /// with it, so a back-dated count re-dates the pile and can move it in FIFO order. Set to 1 to
    /// make a count effectively "today only".
    /// </summary>
    public const int MaxBackdateDays = 7;
}
