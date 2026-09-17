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
        bool recoveryRequired = false) =>
        new()
        {
            Succeeded = false,
            ErrorKind = kind,
            ErrorMessage = message,
            ErrorCode = errorCode,
            DocumentType = key.DocumentType,
            DocumentNo = key.DocumentNo,
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
