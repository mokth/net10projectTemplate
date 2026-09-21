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
