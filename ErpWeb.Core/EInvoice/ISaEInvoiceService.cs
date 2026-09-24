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

    /// <summary>
    /// The LHDN MyInvois portal share link for one submitted document, addressed by its MyInvois
    /// document UUID.
    /// <para>
    /// Deliberately <b>document-type agnostic</b>: it resolves the submission row from
    /// <c>dbo.EInvDocSubmission</c> for the caller's company, so the invoice, credit/debit note and
    /// self-billed screens all call this one method. Returns null when there is no submission row, the
    /// row's status is not <c>SUBMITTED</c>/<c>VALID</c>/<c>CANCELLED</c> (see
    /// <see cref="SaEInvoicePortalLink.IsPortalViewable"/>), the long id is blank, or
    /// <c>Einvoice:EInv_portal</c> is not configured. Never calls MyInvois and writes no audit row.
    /// </para>
    /// </summary>
    Task<SaEInvoicePortalLink?> GetPortalLinkAsync(
        string uuid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Repair the e-Invoice submission history for one MyInvois document UUID: re-read the document from
    /// MyInvois, write the result back onto the owning ERP document, and <b>create-or-update</b> its
    /// <c>dbo.EInvDocSubmission</c> row.
    ///
    /// <para>
    /// <b>UUID-addressed</b> because a UUID is all the E-UUID grid cell has. The document is resolved
    /// from the UUID itself — first through the registry, so a row that exists is repaired in place, then
    /// through the invoice / credit-note tables, so a row that was never written can still be created.
    /// </para>
    ///
    /// <para>
    /// <b>Best-effort by contract.</b> It runs behind a click whose primary job is opening the LHDN
    /// portal, so it never throws and never blocks that link: every failure comes back as a message on
    /// <see cref="SaEInvoiceSubmissionRepairResult"/>. It requires <c>SUBMIT</c> rights — the same right
    /// the status Refresh needs — and reports "not repaired" instead of escalating when they are absent.
    /// </para>
    ///
    /// <para>
    /// <b>At most one MyInvois call, ever.</b> The work is delegated to <see cref="RefreshAsync"/> once.
    /// Callers must not loop on this method — the E-UUID click retries the portal <i>link</i>, never the
    /// repair.
    /// </para>
    /// </summary>
    Task<SaEInvoiceSubmissionRepairResult> RepairSubmissionAsync(
        string uuid,
        CancellationToken cancellationToken = default);

    // ───────────────────────── Batch operations ─────────────────────────

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
    /// Refresh <b>every</b> <c>SUBMITTED</c> invoice the grid is currently showing, in one operator
    /// action — the no-selection counterpart of the list page's E-STATUS button (legacy
    /// <c>GetEStatus()</c>).
    ///
    /// <para>
    /// <b>The candidate set is the grid's own query.</b> <paramref name="scope"/> is the
    /// <see cref="ErpWeb.Core.Sales.SaInvoiceListQuery"/> the page is displaying and it is resolved
    /// through the SAME repository search the grid uses, so "what the operator sees" and "what gets
    /// refreshed" cannot drift apart. Only the e-Invoice status (pinned to <c>SUBMITTED</c>) and the
    /// paging are set here, because those define the action rather than filter it.
    /// </para>
    ///
    /// <para>
    /// <b>The cap is pre-flight.</b> Candidates are counted before the first MyInvois call; a count
    /// over <see cref="ErpWeb.Core.Sales.SaInvoiceLimits.MaxEInvoiceRefreshAllRun"/> is refused with
    /// <b>zero</b> calls. A candidate with no <c>IRBMUUID</c> is reported as <c>Skipped</c> with reason
    /// <c>Missing IRBMUUID</c> and is never sent to MyInvois — it is not escalated to Recover.
    /// </para>
    ///
    /// <para>
    /// <b>Read-only, so cancellation is safe:</b> the token is honoured between chunks only, an
    /// in-flight chunk always finishes, and every result already produced is returned rather than
    /// discarded.
    /// </para>
    /// </summary>
    /// <param name="progress">Completed-of-total candidates, reported once before the first chunk.</param>
    Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(
        ErpWeb.Core.Sales.SaInvoiceListQuery? scope,
        IProgress<SaEInvoiceRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The Credit/Debit Note counterpart of
    /// <see cref="RefreshSubmittedAsync(ErpWeb.Core.Sales.SaInvoiceListQuery?, IProgress{SaEInvoiceRefreshProgress}?, CancellationToken)"/>:
    /// refresh every <c>SUBMITTED</c> note the CN or DN grid is currently showing, in one operator action
    /// (the no-selection E-STATUS mode).
    ///
    /// <para>
    /// <b>The family comes from the scope.</b> <see cref="ErpWeb.Core.Sales.SaCdnListQuery.Type"/> selects
    /// both the document family (<c>CN</c> / <c>DN</c>) and the menu the caller must hold
    /// (<c>SA_CN</c> / <c>SA_DN</c>) — a credit-note run can never touch a debit note. An unknown or blank
    /// <c>Type</c> is refused as a validation failure, never thrown.
    /// </para>
    ///
    /// <para>
    /// <b>The candidate set is the grid's own query.</b> The scope is resolved through
    /// <see cref="ErpWeb.Core.Sales.SaCdnQueryMapper"/> and the SAME repository search the grid uses, so
    /// "what the operator sees" and "what gets refreshed" cannot drift apart. Only the type, the e-Invoice
    /// status (pinned to <c>SUBMITTED</c>) and the paging are set here, because those define the action
    /// rather than filter it.
    /// </para>
    ///
    /// <para>
    /// The cap, the blank-<c>IRBMUUID</c> skip rule, the chunking, the progress reporting and the
    /// chunk-boundary cancellation are identical to the invoice overload — both delegate to the same
    /// driver.
    /// </para>
    /// </summary>
    /// <param name="progress">Completed-of-total candidates, reported once before the first chunk.</param>
    Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(
        ErpWeb.Core.Sales.SaCdnListQuery? scope,
        IProgress<SaEInvoiceRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The <b>self-billed</b> counterpart of the invoice / credit-note overloads: refresh every
    /// <c>SUBMITTED</c> document the self-billed list is currently showing, in one operator action (the
    /// no-selection E-STATUS mode).
    ///
    /// <para>
    /// <b>The family is explicit.</b> <paramref name="documentType"/> is one of
    /// <see cref="EInvoiceDocumentTypes.SelfBilledInvoice"/> /
    /// <see cref="EInvoiceDocumentTypes.SelfBilledCreditNote"/> /
    /// <see cref="EInvoiceDocumentTypes.SelfBilledDebitNote"/>, and it selects the source table AND the
    /// menu the caller must hold (<c>SBI</c> -&gt; <c>PurchaseSbInvoice</c>,
    /// <c>SBC</c> -&gt; <c>PurchaseSbCreditNote</c>, <c>SBD</c> -&gt; <c>PurchaseSbDebitNote</c>). A
    /// credit-note run can never touch a debit note, and no self-billed run can touch a sales document.
    /// Anything else is refused as a validation failure, never thrown.
    /// </para>
    ///
    /// <para>
    /// It is passed in rather than derived from <paramref name="scope"/> deliberately: the self-billed
    /// invoice list's grid query carries no type (the page IS the family), so deriving it there would mean
    /// treating a blank <c>Type</c> as "invoice" — an implicit rule that a future third family would
    /// silently break. The note list's <c>PoSbQuery.Type</c> stays the ERP <c>CN</c>/<c>DN</c> token it has
    /// always been; it is not reused as an e-Invoice family token.
    /// </para>
    ///
    /// <para>
    /// <b>The candidate set is the grid's own query.</b> The scope is resolved through
    /// <see cref="ErpWeb.Core.Purchase.PoSbQueryApplier"/> — the SAME filter definition the self-billed
    /// list services use — so "what the operator sees" and "what gets refreshed" cannot drift apart. Only
    /// the e-Invoice status (pinned to <c>SUBMITTED</c>) and the paging are set here, because those
    /// define the action rather than filter it.
    /// </para>
    ///
    /// <para>
    /// The cap, the blank-<c>IRBMUUID</c> skip rule, the chunking, the progress reporting and the
    /// chunk-boundary cancellation are identical to the other two overloads — all three delegate to the
    /// same driver.
    /// </para>
    ///
    /// <para>
    /// Gated on the family's menu plus <c>Submit</c>. It deliberately does <b>not</b> require <c>Access</c>:
    /// a submit-capable operator is a refresh-capable operator, and reading the list already requires
    /// <c>Access</c>.
    /// </para>
    /// </summary>
    /// <param name="documentType">
    /// <c>SBI</c> / <c>SBC</c> / <c>SBD</c>. Case-insensitive; blank or unknown is a validation refusal.
    /// </param>
    /// <param name="progress">Completed-of-total candidates, reported once before the first chunk.</param>
    Task<SaEInvoiceBatchResult> RefreshSubmittedAsync(
        string documentType,
        ErpWeb.Core.Purchase.PoSbQuery? scope,
        IProgress<SaEInvoiceRefreshProgress>? progress = null,
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

    /// <summary>
    /// The full MyInvois document detail for one document UUID, including
    /// <c>validationResults</c> — the reason an INVALID document is invalid.
    ///
    /// <para>
    /// <b>UUID-addressed</b> on purpose: an invoice, credit note or debit note grid only holds an
    /// <c>IRBMUUID</c>, so this is addressed the same way
    /// <see cref="GetPortalLinkAsync"/> and <see cref="RepairSubmissionAsync"/> are, and works for
    /// every document family without the caller knowing which one it has.
    /// </para>
    ///
    /// <para>
    /// <b>Read-only.</b> Unlike <see cref="RefreshAsync"/> it never changes the document's e-Invoice
    /// status, never writes the submission registry and writes no <c>SaEInvoiceLog</c> audit row:
    /// merely looking at why a document failed must not alter its state.
    /// </para>
    ///
    /// <para>
    /// <b>Authorized with <c>SA_EINVOICE_TIN</c> ACCESS</b>, reusing the read-only MyInvois inquiry
    /// family rather than introducing a menu of its own, and scoped to the caller's company (its
    /// credentials are applied before the call).
    /// </para>
    ///
    /// <para>
    /// <b>Do not poll this.</b> MyInvois directs that Get Document Details be used only to retrieve
    /// error details for invalid documents — for status, use <see cref="RefreshAsync"/>. Repeat calls
    /// for the same document risk throttling (the API allows 125 requests/minute per client ID), so
    /// this must be driven by explicit operator action only.
    /// </para>
    /// </summary>
    Task<SaEInvoiceDetailResult> GetDocumentDetailAsync(
        string uuid,
        CancellationToken cancellationToken = default);
}
