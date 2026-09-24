namespace ErpWeb.Core.EInvoice;

/// <summary>
/// Operational e-Invoice settings. These live with the environment-level <c>Einvoice:*</c>
/// configuration section but are deliberately separate from the credential/URL keys handled by
/// <c>IClientSecretStore</c>.
/// </summary>
public sealed class EInvoiceOptions
{
    public const string SectionName = "Einvoice";

    /// <summary>
    /// How long a row may stay <c>SUBMITTING</c> before <c>Recover</c> is allowed to reconcile it.
    /// A crashed process leaves rows in this state; they must NEVER be silently reset to NEW.
    /// </summary>
    public int SubmittingStuckMinutes { get; set; } = 15;

    /// <summary>
    /// How long after validation (else submission) a document may still be cancelled at MyInvois.
    /// <para>
    /// Verified against the LHDN MyInvois API documentation (Cancel Document) and the LHDN e-Invoice
    /// general FAQ: cancellation is allowed only within <b>72 hours from the document's validation
    /// timestamp</b>; after that the adjustment has to be a credit note / debit note / refund note.
    /// The API reports this as <c>OperationPeriodOver</c> when the window has run out, and the limit
    /// is document-type specific (returned by the Get Document Type workflow parameters), so the
    /// window stays configurable rather than hard-coded in the call path.
    /// </para>
    /// </summary>
    public int CancelWindowHours { get; set; } = 72;

    /// <summary>Search window (days back) used by Recover when looking a document up by number.</summary>
    public int RecoverySearchDays { get; set; } = 30;

    /// <summary>
    /// How long the E-UUID click path may spend on the repair's <b>persistence</b> work.
    /// <para>
    /// Named for what it actually bounds: the MyInvois HTTP call is deliberately <b>not</b> covered,
    /// because <c>ISubmitDocumentHelper.GetDocumentDetail(string)</c> takes no cancellation token and
    /// <c>E_InvoiceRepository</c> awaits a bare <c>HttpClient</c> call. A transport hang therefore still
    /// waits for that client's own timeout. Adding a token to the helper signature is logged tech debt;
    /// until then this option only stops the database work that follows the call.
    /// </para>
    /// </summary>
    public int RepairPersistenceTimeoutSeconds { get; set; } = 25;
}

/// <summary>Identifies one ERP sales document for e-Invoice operations.</summary>
public sealed class SaEInvoiceDocumentKey
{
    /// <summary><see cref="EInvoiceDocumentTypes"/>: INV, CN or DN.</summary>
    public string DocumentType { get; init; } = string.Empty;

    public string DocumentNo { get; init; } = string.Empty;

    public override string ToString() => $"{DocumentType}:{DocumentNo}";
}

public enum SaEInvoiceErrorKind
{
    None = 0,

    /// <summary>ERP-side data validation failed. Nothing was sent to MyInvois.</summary>
    Validation,

    /// <summary>Optimistic concurrency conflict - the document changed under us.</summary>
    Concurrency,

    NotFound,

    /// <summary>Caller lacks the SUBMIT / CANCEL permission.</summary>
    Authorization,

    /// <summary>The company has no usable e-Invoice profile / credentials.</summary>
    NotConfigured,

    /// <summary>The current lifecycle status forbids the requested action.</summary>
    StateRule,

    /// <summary>MyInvois (or the transport to it) failed.</summary>
    MyInvois,

    Unexpected
}

/// <summary>
/// What the e-Invoice submission registry (<c>dbo.EInvDocSubmission</c>) write actually did during one
/// operation.
///
/// <para>
/// This is the <b>public</b> counterpart of <c>EInvoiceSubmissionWriter</c>'s internal
/// <c>EInvoiceHistoryWrite</c>: the persistence implementation stays internal and
/// <c>SaEInvoiceService</c> maps the writer's four outcomes onto this enum, so the implementation does
/// not leak through <see cref="ISaEInvoiceService"/>.
/// </para>
///
/// <para>
/// Callers must read this rather than <see cref="SaEInvoiceResult.Succeeded"/> to decide whether the
/// registry got written: a Refresh can report success (a status was applied) while the registry write
/// was skipped or failed.
/// </para>
/// </summary>
public enum EInvoiceHistoryWriteResult
{
    /// <summary>No registry write was attempted — an earlier step returned, or the operation never touches the registry.</summary>
    NotAttempted,

    /// <summary>A new history row was inserted: the row was genuinely missing.</summary>
    Inserted,

    /// <summary>An existing history row was refreshed in place.</summary>
    Updated,

    /// <summary>The document carries no submission id, so there was no key to write a row under.</summary>
    Skipped,

    /// <summary>The write was attempted and failed. Logged as a warning; the business action is unaffected.</summary>
    Failed
}

/// <summary>Result of every <see cref="ISaEInvoiceService"/> operation.</summary>
public sealed class SaEInvoiceResult
{
    public bool Succeeded { get; init; }
    public SaEInvoiceErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }

    public string DocumentType { get; init; } = string.Empty;
    public string DocumentNo { get; init; } = string.Empty;

    /// <summary>Resulting <c>IRBMStatus</c>.</summary>
    public string? Status { get; init; }

    /// <summary><c>IRBMOutcome</c> when the status is FAILED.</summary>
    public string? Outcome { get; init; }

    public string? Uuid { get; init; }
    public string? SubmissionId { get; init; }
    public int AttemptNo { get; init; }
    public Guid CorrelationId { get; init; }

    /// <summary>ERP validation issues, when <see cref="ErrorKind"/> is Validation.</summary>
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the operation needs the operator to run Recover before retrying.</summary>
    public bool RecoveryRequired { get; init; }

    /// <summary>
    /// What the submission registry write did. <see cref="EInvoiceHistoryWriteResult.NotAttempted"/> for
    /// every operation that does not touch the registry.
    /// </summary>
    public EInvoiceHistoryWriteResult HistoryWrite { get; init; }

    /// <summary>
    /// True when the submission id was <b>recovered from the MyInvois response</b> because the ERP
    /// document had none. A deliberate, narrow exception to the "the key always comes from the ERP
    /// document" rule: it only runs when there is no ERP value to disagree with.
    /// </summary>
    public bool SubmissionIdRecovered { get; init; }

    public static SaEInvoiceResult Ok(SaEInvoiceDocumentKey key) =>
        new() { Succeeded = true, ErrorKind = SaEInvoiceErrorKind.None, DocumentType = key.DocumentType, DocumentNo = key.DocumentNo };

    public static SaEInvoiceResult Fail(
        SaEInvoiceDocumentKey key,
        string message,
        SaEInvoiceErrorKind kind = SaEInvoiceErrorKind.StateRule,
        string? errorCode = null,
        bool recoveryRequired = false,
        string? status = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = kind,
            ErrorMessage = message,
            ErrorCode = errorCode,
            DocumentType = key.DocumentType,
            DocumentNo = key.DocumentNo,
            Status = status,
            RecoveryRequired = recoveryRequired
        };

    public static SaEInvoiceResult FailValidation(
        SaEInvoiceDocumentKey key,
        string message,
        IReadOnlyDictionary<string, string>? errors = null) =>
        new()
        {
            Succeeded = false,
            ErrorKind = SaEInvoiceErrorKind.Validation,
            ErrorMessage = message,
            DocumentType = key.DocumentType,
            DocumentNo = key.DocumentNo,
            ValidationErrors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
}

/// <summary>
/// Outcome of one UUID-addressed submission-history repair
/// (<see cref="ISaEInvoiceService.RepairSubmissionAsync"/>).
///
/// <para>
/// Unlike <see cref="SaEInvoiceResult"/> this type can express "there was nothing to repair":
/// <see cref="Attempted"/> is false when the UUID is not linked to any ERP document, or when the caller
/// lacks SUBMIT rights. That is a normal answer for the E-UUID click — the portal tab still opens — so
/// the UI must NOT render it as an error.
/// </para>
///
/// <para>
/// <see cref="Succeeded"/> mirrors <c>RefreshAsync</c>'s semantics (a recognised MyInvois status was
/// applied) and is <b>not</b> the "the row was repaired" signal. <see cref="HistoryWrite"/> is.
/// </para>
/// </summary>
public sealed class SaEInvoiceSubmissionRepairResult
{
    /// <summary>False when nothing was repaired because there was nothing to repair, or no permission.</summary>
    public bool Attempted { get; init; }

    /// <summary>Mirrors the underlying refresh: a recognised MyInvois status was applied to the document.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Always set, ready to display. Never an exception message.</summary>
    public string Message { get; init; } = string.Empty;

    public string? DocumentType { get; init; }
    public string? DocumentNo { get; init; }

    /// <summary>Resulting <c>IRBMStatus</c>, when a document was resolved.</summary>
    public string? Status { get; init; }

    public string? SubmissionId { get; init; }

    /// <summary>True when the submission id had to be taken from the MyInvois response.</summary>
    public bool SubmissionIdRecovered { get; init; }

    /// <summary>What the registry write did — the authoritative "was the row repaired" signal.</summary>
    public EInvoiceHistoryWriteResult HistoryWrite { get; init; }

    public SaEInvoiceErrorKind ErrorKind { get; init; }

    /// <summary>Nothing to repair. Not an error: the caller keeps the portal link behaviour unchanged.</summary>
    public static SaEInvoiceSubmissionRepairResult NotApplicable(string message) =>
        new() { Attempted = false, Message = message, HistoryWrite = EInvoiceHistoryWriteResult.NotAttempted };
}

/// <summary>Current e-Invoice state of one document, for the UI status panel.</summary>
public sealed class SaEInvoiceStatusView
{
    public string DocumentType { get; init; } = string.Empty;
    public string DocumentNo { get; init; } = string.Empty;

    public string Status { get; init; } = EInvoiceStatuses.New;
    public string? Outcome { get; init; }
    public string? Uuid { get; init; }
    public string? OriginUuid { get; init; }
    public string? SubmissionId { get; init; }
    public DateTime? SentOn { get; init; }
    public DateTime? ValidOn { get; init; }
    public DateTime? CancelledOn { get; init; }
    public string? Error { get; init; }

    public bool IsLocked => EInvoiceStatuses.IsLocked(Status);

    /// <summary>Mirrors the service pre-submit gate: FAILED is only submittable when the outcome is
    /// a confirmed failure; Unknown must Recover first.</summary>
    public bool CanSubmit => Status is EInvoiceStatuses.New or EInvoiceStatuses.Rejected or EInvoiceStatuses.Invalid or EInvoiceStatuses.Cancelled
                             || (Status == EInvoiceStatuses.Failed && Outcome == EInvoiceOutcomes.ConfirmedFailure);
    public bool CanRetry => Status == EInvoiceStatuses.Failed && Outcome == EInvoiceOutcomes.ConfirmedFailure;
    public bool CanRecover => Status == EInvoiceStatuses.Submitting
                              || (Status == EInvoiceStatuses.Failed && Outcome == EInvoiceOutcomes.Unknown);
    public bool CanRefresh => Status is EInvoiceStatuses.Submitted or EInvoiceStatuses.Valid && !string.IsNullOrWhiteSpace(Uuid);
    public bool CanCancel => Status is EInvoiceStatuses.Submitted or EInvoiceStatuses.Valid;

    public IReadOnlyList<SaEInvoiceLogRow> History { get; init; } = [];
}

/// <summary>
/// The LHDN MyInvois <b>portal share link</b> for one submitted e-Invoice document.
/// <para>
/// Every screen that shows a MyInvois UUID (sales invoice, credit/debit note, and later the
/// self-billed families) resolves its link through this type, so the URL shape and the
/// "may this document be opened at all?" rule exist in exactly one place.
/// </para>
/// </summary>
public sealed class SaEInvoicePortalLink
{
    /// <summary>Path segment between the document UUID and the long id, per MyInvois' share URL.</summary>
    private const string ShareSegment = "share";

    /// <summary>MyInvois document UUID (<c>dbo.EInvDocSubmission.uuid</c>).</summary>
    public string Uuid { get; init; } = string.Empty;

    /// <summary>MyInvois document long id (<c>dbo.EInvDocSubmission.longId</c>).</summary>
    public string LongId { get; init; } = string.Empty;

    /// <summary>Normalized <c>dbo.EInvDocSubmission.status</c> the link was built from.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Absolute portal URL: <c>{portal}/{uuid}/share/{longId}</c>.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// A <b>SUBMITTED</b>, <b>VALID</b> or <b>CANCELLED</b> document has a page at the MyInvois portal.
    /// The test is case-insensitive on purpose: ErpWeb writes the uppercase
    /// <see cref="EInvoiceStatuses"/> values while legacy rows in the table hold title-case literals
    /// such as <c>"Valid"</c>.
    ///
    /// <para>
    /// <b>SUBMITTED is deliberate.</b> MyInvois can leave an accepted document in that state for a
    /// while before it validates it, and the operator still has to be able to reach it at the portal.
    /// A viewable status is not sufficient on its own: the link also needs a non-blank <c>longId</c>
    /// (see <see cref="Create"/>), which MyInvois only returns once the document is valid - so a
    /// genuinely pending document still resolves to no link, and the E-UUID click reports that
    /// instead of opening a broken page.
    /// </para>
    /// </summary>
    public static bool IsPortalViewable(string? status) =>
        EInvoiceStatuses.Normalize(status) is
            EInvoiceStatuses.Submitted or EInvoiceStatuses.Valid or EInvoiceStatuses.Cancelled;

    /// <summary>
    /// Builds the link, or returns null when the document is not viewable, its MyInvois long id is
    /// missing, or the portal base URL (<c>Einvoice:EInv_portal</c>) has not been configured.
    /// </summary>
    public static SaEInvoicePortalLink? Create(
        string? uuid,
        string? longId,
        string? status,
        string? portalBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(uuid)
            || string.IsNullOrWhiteSpace(longId)
            || !IsPortalViewable(status))
        {
            return null;
        }

        var baseUrl = (portalBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            return null;
        }

        var cleanUuid = uuid.Trim();
        var cleanLongId = longId.Trim();

        return new SaEInvoicePortalLink
        {
            Uuid = cleanUuid,
            LongId = cleanLongId,
            Status = EInvoiceStatuses.Normalize(status),
            Url = $"{baseUrl}/{Uri.EscapeDataString(cleanUuid)}/{ShareSegment}/{Uri.EscapeDataString(cleanLongId)}"
        };
    }
}

/// <summary>
/// Outcome of one document inside a batch e-Invoice action, in the caller's selection order.
/// <para>
/// The <see cref="Succeeded"/> / <see cref="Skipped"/> / <see cref="RecoveryRequired"/> combination is
/// a fixed contract; the UI and the tests must not reinterpret it:
/// </para>
/// <list type="bullet">
/// <item><c>Succeeded</c> - MyInvois accepted it (submit), the status was applied (refresh), or the
/// cancellation was accepted (cancel).</item>
/// <item><c>Skipped</c> - never attempted because it was ineligible. Other rows still ran.</item>
/// <item><c>RecoveryRequired</c> - the operator must run Recover for this document before it can be
/// submitted again (an unresolved outcome, or a stuck <c>SUBMITTING</c> row).</item>
/// </list>
/// </summary>
public sealed class SaEInvoiceBatchItemResult
{
    public string DocumentType { get; init; } = string.Empty;
    public string DocumentNo { get; init; } = string.Empty;

    /// <summary>True only when the operation actually succeeded for this document.</summary>
    public bool Succeeded { get; init; }

    /// <summary>True when the row was not attempted because it was ineligible.</summary>
    public bool Skipped { get; init; }

    /// <summary>Resulting <c>IRBMStatus</c>, or the unchanged status when nothing was applied.</summary>
    public string? Status { get; init; }

    /// <summary><c>IRBMOutcome</c> when the status is FAILED.</summary>
    public string? Outcome { get; init; }

    public string? Uuid { get; init; }
    public string? SubmissionId { get; init; }

    /// <summary>Why this row failed or was skipped. Null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when Recover must reconcile this document before another submit is allowed.</summary>
    public bool RecoveryRequired { get; init; }
}

/// <summary>
/// Result of a batch e-Invoice action. <see cref="Items"/> holds one entry per <b>deduplicated</b>
/// selected document; <see cref="ErrorMessage"/> is set only when the whole action was refused before
/// any work was done, in which case <see cref="Items"/> is empty.
/// </summary>
public sealed class SaEInvoiceBatchResult
{
    public IReadOnlyList<SaEInvoiceBatchItemResult> Items { get; init; } = [];

    /// <summary>Set only when the action was refused up front: over the cap, not authorized, or a bad reason.</summary>
    public string? ErrorMessage { get; init; }

    public SaEInvoiceErrorKind ErrorKind { get; init; }

    /// <summary>True when nothing was attempted because the action itself was refused.</summary>
    public bool Refused => !string.IsNullOrWhiteSpace(ErrorMessage);

    public int SucceededCount => Items.Count(x => x.Succeeded);

    /// <summary>Attempted but did not succeed. Excludes skipped rows.</summary>
    public int FailedCount => Items.Count(x => !x.Succeeded && !x.Skipped);

    public int SkippedCount => Items.Count(x => x.Skipped);

    public static SaEInvoiceBatchResult From(IReadOnlyList<SaEInvoiceBatchItemResult> items) =>
        new() { Items = items };

    public static SaEInvoiceBatchResult Failed(string message, SaEInvoiceErrorKind kind) =>
        new() { ErrorMessage = message, ErrorKind = kind };
}

/// <summary>
/// Progress of one refresh-all run: <see cref="Done"/> candidate invoices have been attempted out of
/// <see cref="Total"/>. The total is reported once, before the first chunk runs, so the UI can show a
/// denominator before any MyInvois traffic starts.
/// </summary>
public sealed class SaEInvoiceRefreshProgress
{
    /// <summary>Candidate invoices completed so far (succeeded, failed or skipped alike).</summary>
    public int Done { get; init; }

    /// <summary>Candidate invoices in this run, decided before the first MyInvois call.</summary>
    public int Total { get; init; }
}

/// <summary>One row of <c>SaEInvoiceLog</c> surfaced to the UI.</summary>
public sealed class SaEInvoiceLogRow
{
    public long Id { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? Status { get; init; }
    public string? SubmissionId { get; init; }
    public string? DocumentUuid { get; init; }
    public int AttemptNo { get; init; }
    public Guid? CorrelationId { get; init; }
    public DateTime? RequestTime { get; init; }
    public DateTime? ResponseTime { get; init; }
    public long? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime CreatedOn { get; init; }
}
