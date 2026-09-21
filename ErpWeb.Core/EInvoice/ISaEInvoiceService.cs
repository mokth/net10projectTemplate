namespace ErpWeb.Core.EInvoice;

/// <summary>
/// The single entry point for MyInvois traffic. Sales pages and <c>SaInvoiceService</c> /
/// <c>SaCdnService</c> MUST go through this contract; nothing outside this class may call
/// <c>IE_InvoiceRepository</c> or <c>ISubmitDocumentHelper</c>.
/// <para>
/// The service owns the lifecycle rules, idempotency, recovery, cancellation and audit trail. The
/// document generation / hashing / signing / submission path inside
/// <c>ErpWeb.EInvoiceLib</c> is used as-is and is never modified here.
/// </para>
/// </summary>
public interface ISaEInvoiceService
{
    /// <summary>
    /// ERP-side validation only. Never calls MyInvois. Writes a <c>Validate</c> audit row.
    /// </summary>
    Task<SaEInvoiceResult> ValidateAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submit the document to MyInvois. Persists <c>SUBMITTING</c> before the HTTP call.
    /// </summary>
    Task<SaEInvoiceResult> SubmitAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retry after a <c>ConfirmedFailure</c>, a rejection or an invalid document that has been fixed.
    /// Refused (and asks for Recover) when the previous outcome is <c>Unknown</c>.
    /// </summary>
    Task<SaEInvoiceResult> RetryAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcile a stuck <c>SUBMITTING</c> document or an <c>Unknown</c> failure against MyInvois.
    /// <b>Never</b> resubmits.
    /// </summary>
    /// <param name="operatorInitiated">
    /// True when a user pressed Recover. When false, the stuck-timeout rule must be satisfied.
    /// </param>
    Task<SaEInvoiceResult> RecoverAsync(
        SaEInvoiceDocumentKey key,
        bool operatorInitiated,
        CancellationToken cancellationToken = default);

    /// <summary>Re-read the MyInvois status of a submitted document by its UUID.</summary>
    Task<SaEInvoiceResult> RefreshAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Cancel a submitted/valid document inside the configured cancel window.</summary>
    Task<SaEInvoiceResult> CancelAsync(
        SaEInvoiceDocumentKey key,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Status snapshot plus audit history, for the UI panel.</summary>
    Task<SaEInvoiceStatusView?> GetStatusAsync(
        SaEInvoiceDocumentKey key,
        CancellationToken cancellationToken = default);

    // ─────────────────────────────── Batch operations ───────────────────────────────

    /// <summary>
    /// Submit several documents in ONE MyInvois submission (MyInvois accepts up to 100 documents per
    /// call). Ineligible documents are skipped and reported; the eligible ones still run.
    /// <para>
    /// The selection is normalized and deduplicated case-insensitively before the cap is applied, so
    /// a duplicated selection counts once. Every key is re-authorized, reloaded and re-validated here;
    /// <see cref="GetStatusManyAsync"/> is never trusted as the eligibility verdict.
    /// </para>
    /// </summary>
    Task<SaEInvoiceBatchResult> SubmitManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-read the MyInvois status of several submitted documents, one call per document, matching the
    /// single-document <see cref="RefreshAsync"/> semantics. Refresh is read-only and never destroys
    /// state, so an unrecognised or failed read leaves the current status alone.
    /// </summary>
    Task<SaEInvoiceBatchResult> RefreshManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel several submitted/valid documents using one shared reason. Each document is cancelled by
    /// its own MyInvois call and committed independently, so a later failure never rolls back an
    /// earlier success.
    /// </summary>
    Task<SaEInvoiceBatchResult> CancelManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pre-flight status snapshot for the list-page confirmation prompt, index-aligned with
    /// <paramref name="keys"/>; an element is null when the document has no e-Invoice state to read.
    /// Requires document access only, and is <b>never</b> authoritative for execution.
    /// </summary>
    Task<IReadOnlyList<SaEInvoiceStatusView?>> GetStatusManyAsync(
        IReadOnlyList<SaEInvoiceDocumentKey> keys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask MyInvois whether a TIN belongs to the holder of the given identity document.
    /// Read-only: nothing is submitted and no document state changes.
    /// </summary>
    Task<SaEInvoiceTinCheckResult> ValidateTinAsync(
        string idType,
        string idValue,
        string tin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Look up TINs by taxpayer name and/or identity document. Read-only.
    /// </summary>
    Task<SaEInvoiceTinSearchResult> SearchTinAsync(
        SaEInvoiceTinSearchQuery query,
        CancellationToken cancellationToken = default);
}
