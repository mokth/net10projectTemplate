namespace ErpWeb.Core.EInvoice;

/// <summary>
/// The e-Invoice limits that are a property of the MyInvois integration rather than of one document
/// family. They live here, not in <c>SaInvoiceLimits</c>, so the credit/debit-note path does not have to
/// reach into an invoice-named type to learn how many documents it may send.
///
/// <para>
/// <c>SaInvoiceLimits.MaxEInvoiceBatchSelection</c> and <c>SaInvoiceLimits.MaxEInvoiceRefreshAllRun</c>
/// forward to these constants, so the invoice call sites and their tests are untouched while there is
/// still exactly ONE definition of each number. The forwarding is pinned by a test — if the two ever
/// drift, the suite fails rather than the two families silently behaving differently.
/// </para>
/// </summary>
public static class SaEInvoiceLimits
{
    /// <summary>
    /// Hard cap on how many documents one interactive batch e-Invoice action (Submit / E-Status /
    /// Cancel) may carry. Keeps the request inside the MyInvois rate limits and the grid responsive;
    /// it is applied AFTER case-insensitive deduplication.
    /// </summary>
    public const int MaxBatchSelection = 10;

    /// <summary>
    /// Hard cap on one "refresh every submitted document" run (the E-STATUS button with nothing
    /// selected). Each document costs its own MyInvois round trip, so the run is bounded and the
    /// operator narrows the filter instead. Enforced BEFORE the first MyInvois call: an over-cap run
    /// makes zero calls.
    /// </summary>
    public const int MaxRefreshAllRun = 200;
}
