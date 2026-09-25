namespace ErpWeb.Core.Inventory;

/// <summary>
/// Lifecycle states of an inventory period close.
///
///   —        → CLOSED    Close
///   CLOSED   → REOPENED  Reopen (deletes the derived snapshot lines)
///   REOPENED → CLOSED    Re-close (regenerates the snapshot on the same header row)
/// </summary>
public static class IvPeriodCloseStatuses
{
    public const string Closed = "CLOSED";
    public const string Reopened = "REOPENED";

    public static readonly IReadOnlyList<string> All = [Closed, Reopened];
}

/// <summary>
/// Hard limits for the period-close feature, in the shape of <see cref="IvPostingLimits"/>.
/// </summary>
public static class IvPeriodCloseLimits
{
    /// <summary>
    /// The guard refuses a batch/document whose stock-driving date falls on or before the
    /// closed-through date of any CLOSED period for the tenant. This constant is not a limit —
    /// it only exists so the message and the tests share one spelling of the rule.
    /// </summary>
    public const string RefusalTemplate = "The date {0:yyyy-MM-dd} falls in a closed inventory period (closed through {1:yyyy-MM-dd}). Reopen the period first.";
}
