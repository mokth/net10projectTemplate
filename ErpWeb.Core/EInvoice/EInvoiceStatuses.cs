namespace ErpWeb.Core.EInvoice;

/// <summary>
/// The ERP e-Invoice lifecycle status stored in <c>IRBMStatus</c>.
/// These values are LOCKED - the MyInvois status is mapped onto them and nothing else invents
/// extra states (see <see cref="SaEInvoiceStatusMap"/>).
/// </summary>
public static class EInvoiceStatuses
{
    /// <summary>Never submitted. Also the meaning of NULL <c>IRBMStatus</c>.</summary>
    public const string New = "NEW";

    /// <summary>Persisted immediately before the MyInvois HTTP call; proof an attempt is in flight.</summary>
    public const string Submitting = "SUBMITTING";

    /// <summary>MyInvois accepted the submission and assigned a UUID.</summary>
    public const string Submitted = "SUBMITTED";

    /// <summary>MyInvois validated the document.</summary>
    public const string Valid = "VALID";

    /// <summary>MyInvois rejected the content during validation.</summary>
    public const string Invalid = "INVALID";

    /// <summary>Transport/ERP failure. <c>IRBMOutcome</c> says whether retry is safe.</summary>
    public const string Failed = "FAILED";

    /// <summary>API rejected the whole submission before assigning a document UUID.</summary>
    public const string Rejected = "REJECTED";

    /// <summary>Document cancelled at MyInvois.</summary>
    public const string Cancelled = "CANCELLED";

    /// <summary>Every value the status column may legitimately hold.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        New, Submitting, Submitted, Valid, Invalid, Failed, Rejected, Cancelled
    };

    /// <summary>
    /// Statuses that lock the payload: structural edits are refused by the service layer (and
    /// disabled in the UI) while a document is in one of these states.
    /// </summary>
    public static IReadOnlySet<string> Locked { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Submitting, Submitted, Valid
    };

    /// <summary>NULL / unknown values read as <see cref="New"/>.</summary>
    public static string Normalize(string? status) =>
        string.IsNullOrWhiteSpace(status) || !All.Contains(status) ? New : status!.Trim().ToUpperInvariant();

    /// <summary>True when payload-changing edits must be refused.</summary>
    public static bool IsLocked(string? status) => Locked.Contains(Normalize(status));

    /// <summary>True when the document already holds (or is obtaining) a MyInvois identity.</summary>
    public static bool IsSubmittedOrBeyond(string? status)
    {
        var normalized = Normalize(status);
        return normalized is Submitting or Submitted or Valid or Invalid or Cancelled;
    }
}

/// <summary>
/// Discriminates a <see cref="EInvoiceStatuses.Failed"/> status into "definitely not accepted"
/// and "outcome unknown". This keeps the public status enum small while still telling Retry
/// whether it may submit again or must run Recover first.
/// </summary>
public static class EInvoiceOutcomes
{
    /// <summary>The submission definitely did not reach / was definitely not accepted by MyInvois.
    /// Retry may submit again after validation.</summary>
    public const string ConfirmedFailure = "ConfirmedFailure";

    /// <summary>The request may have reached MyInvois but no response was read.
    /// <b>Never</b> submit again until Recover has reconciled with MyInvois.</summary>
    public const string Unknown = "Unknown";
}

/// <summary>Audit actions written to <c>SaEInvoiceLog.Action</c>.</summary>
public static class EInvoiceActions
{
    public const string Validate = "Validate";
    public const string Submit = "Submit";
    public const string Recover = "Recover";
    public const string Refresh = "Refresh";
    public const string Cancel = "Cancel";
    public const string Retry = "Retry";
}

/// <summary>ERP document families used for the e-Invoice state columns and the audit log.</summary>
public static class EInvoiceDocumentTypes
{
    public const string Invoice = "INV";
    public const string CreditNote = "CN";
    public const string DebitNote = "DN";

    /// <summary>Self-billed families (LHDN types 11/12/13). The Purchase payload mapping is not wired yet.</summary>
    public const string SelfBilledInvoice = "SBI";
    public const string SelfBilledCreditNote = "SBC";
    public const string SelfBilledDebitNote = "SBD";

    /// <summary>True for a family this façade can load a source document for: INV / CN / DN.</summary>
    public static bool IsKnown(string? documentType) =>
        documentType is Invoice or CreditNote or DebitNote;

    /// <summary>
    /// True for the self-billed families (11/12/13). They are recognised so the caller gets an explicit
    /// "not enabled yet" answer instead of a misleading "unknown document type".
    /// </summary>
    public static bool IsSelfBilled(string? documentType) =>
        documentType is SelfBilledInvoice or SelfBilledCreditNote or SelfBilledDebitNote;
}

/// <summary>
/// The locked MyInvois -&gt; ERP status mapping. Refresh and Recover apply this table and nothing else.
/// </summary>
public static class SaEInvoiceStatusMap
{
    /// <summary>MyInvois document status (Submitted / Valid / Invalid / Cancelled) to ERP status.</summary>
    public static string FromMyInvoisDocumentStatus(string? myInvoisStatus)
    {
        if (string.IsNullOrWhiteSpace(myInvoisStatus))
        {
            return EInvoiceStatuses.New;
        }

        return myInvoisStatus.Trim().ToLowerInvariant() switch
        {
            "submitted" => EInvoiceStatuses.Submitted,
            "valid" => EInvoiceStatuses.Valid,
            "invalid" => EInvoiceStatuses.Invalid,
            "cancelled" => EInvoiceStatuses.Cancelled,
            _ => EInvoiceStatuses.New
        };
    }

    /// <summary>
    /// Whether a MyInvois status is a terminal state that clears <c>IRBMOutcome</c>. Anything the
    /// mapping table does not recognise leaves the caller to keep the current status.
    /// </summary>
    public static bool IsRecognised(string? myInvoisStatus)
    {
        if (string.IsNullOrWhiteSpace(myInvoisStatus))
        {
            return false;
        }

        return myInvoisStatus.Trim().ToLowerInvariant() is "submitted" or "valid" or "invalid" or "cancelled";
    }

    /// <summary>True when the mapped status means "the document exists at MyInvois".</summary>
    public static bool IsKnownMyInvoisStatus(string? myInvoisStatus) =>
        IsRecognised(myInvoisStatus);
}
