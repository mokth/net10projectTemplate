namespace ErpWeb.Core.EInvoice;

/// <summary>
/// Full MyInvois document detail (Get Document Details, addressed by document UUID), shaped for the
/// UI so that <c>ErpWeb.EInvoiceLib</c> types never reach a page.
///
/// <para>
/// <b>No <c>ErpWeb.EInvoiceLib</c> type may leak out of the mapper.</b> This type and the nested ones
/// below are the only shape a Blazor page sees. See <c>SaEInvoiceDetailMapper</c>.
/// </para>
/// </summary>
public sealed class SaEInvoiceDetailView
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string Uuid { get; init; } = string.Empty;
    public string? SubmissionUid { get; init; }

    /// <summary>
    /// MyInvois long id. Only returned for <b>valid</b> documents — this is the value
    /// <see cref="SaEInvoicePortalLink"/> needs, so an INVALID document legitimately has none.
    /// </summary>
    public string? LongId { get; init; }

    public string? InternalId { get; init; }
    public string? TypeName { get; init; }
    public string? TypeVersionName { get; init; }

    // ── Parties ───────────────────────────────────────────────────────────────
    public string? IssuerTin { get; init; }
    public string? IssuerName { get; init; }
    public string? ReceiverId { get; init; }
    public string? ReceiverName { get; init; }

    // ── Dates (UTC as returned by MyInvois) ───────────────────────────────────
    public DateTime? DateTimeIssued { get; init; }
    public DateTime? DateTimeReceived { get; init; }
    public DateTime? DateTimeValidated { get; init; }
    public DateTime? CancelDateTime { get; init; }
    public DateTime? RejectRequestDateTime { get; init; }

    // ── Totals ────────────────────────────────────────────────────────────────
    public decimal? TotalExcludingTax { get; init; }
    public decimal? TotalDiscount { get; init; }
    public decimal? TotalNetAmount { get; init; }
    public decimal? TotalPayableAmount { get; init; }

    /// <summary>The MyInvois status exactly as returned (Submitted / Valid / Invalid / Cancelled).</summary>
    public string? MyInvoisStatus { get; init; }

    /// <summary>
    /// <see cref="MyInvoisStatus"/> mapped onto the ERP vocabulary via
    /// <see cref="EInvoiceStatuses.Normalize"/>, so the page can style its chip like every other
    /// e-Invoice surface. The unmapped value stays available on <see cref="MyInvoisStatus"/>.
    /// </summary>
    public string Status { get; init; } = EInvoiceStatuses.New;

    /// <summary>Reason of the cancellation or rejection, when MyInvois supplied one.</summary>
    public string? DocumentStatusReason { get; init; }

    public string? CreatedByUserId { get; init; }

    // ── Validation results — the reason this screen exists ────────────────────
    /// <summary><c>validationResults.status</c> — the overall verdict (Submitted / Valid / Invalid).</summary>
    public string? ValidationStatus { get; init; }

    public IReadOnlyList<SaEInvoiceValidationStep> Steps { get; init; } = [];

    /// <summary>True when at least one validation step reported an issue.</summary>
    public bool HasIssues => IssueCount > 0;

    /// <summary>Total number of issues across every step, including nested inner errors.</summary>
    public int IssueCount => Steps.Sum(step => step.IssueCount);
}

/// <summary>
/// One entry of <c>validationResults.validationSteps[]</c>. A step is always reported, even a
/// <c>Valid</c> one with no error object — dropping empty steps would hide which validations ran.
/// </summary>
public sealed class SaEInvoiceValidationStep
{
    /// <summary>The validation's name, e.g. "Step04-Code Field Validator".</summary>
    public string? Name { get; init; }

    /// <summary>Submitted / Valid / Invalid.</summary>
    public string? Status { get; init; }

    public IReadOnlyList<SaEInvoiceValidationIssue> Issues { get; init; } = [];

    /// <summary>Issues in this step plus everything nested beneath them.</summary>
    public int IssueCount => Issues.Sum(issue => 1 + issue.InnerErrors.Count);
}

/// <summary>
/// One flattened MyInvois error (<c>DocErrorDetail</c>).
///
/// <para>
/// <b>Naming warning:</b> <see cref="ErrorMs"/> is <b>not</b> milliseconds — <c>Ms</c> is
/// <b>Bahasa Melayu</b>. <see cref="Error"/> is the English message and <see cref="ErrorMs"/> is the
/// same message in Malay. Never conflate them, and never truncate either: both can carry a full
/// sentence plus an SDK URL.
/// </para>
///
/// <para>
/// Every property is nullable by contract — MyInvois documents that <c>propertyPath</c> "might have
/// the value null if the property path cannot be resolved".
/// </para>
/// </summary>
public sealed class SaEInvoiceValidationIssue
{
    /// <summary>Name of the subject of the error, e.g. <c>State</c>. May be null.</summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// Path to the offending value, e.g.
    /// <c>/ubl:Invoice/cac:AccountingCustomerParty/cac:Party/cac:PostalAddress/cbc:CountrySubentityCode</c>.
    /// </summary>
    public string? PropertyPath { get; init; }

    /// <summary>Machine-handled code the client must handle, e.g. <c>Error04</c> or <c>CV317</c>.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable message in <b>English</b>. Primary display text.</summary>
    public string? Error { get; init; }

    /// <summary>Human-readable message in <b>Bahasa Melayu</b>. Secondary display text.</summary>
    public string? ErrorMs { get; init; }

    /// <summary>
    /// Opaque passthrough. Not part of MyInvois' documented error structure — never parse it, only
    /// render it when non-blank.
    /// </summary>
    public string? MetaData { get; init; }

    /// <summary>
    /// Child issues, which are usually the actionable ones: a failing step can carry only a summary
    /// (<c>Error04</c>, "Step04-Invalid Code Field Validator") while the per-field cause
    /// (<c>CV317</c>) lives here.
    ///
    /// <para>
    /// MyInvois documents <c>innerError</c> as recursive, but the repository model is not:
    /// <c>DocErrorDetail.InnerError</c> is <c>List&lt;InnerErrorMsg&gt;</c> and
    /// <c>InnerErrorMsg.InnerError</c> is <c>object</c>. Exactly <b>one</b> level can therefore be
    /// bound — do not expect deeper nesting to appear here.
    /// </para>
    /// </summary>
    public IReadOnlyList<SaEInvoiceValidationIssue> InnerErrors { get; init; } = [];
}

/// <summary>
/// Outcome of <see cref="ISaEInvoiceService.GetDocumentDetailAsync"/>.
///
/// <para>
/// Mirrors the read-only inquiry results (<see cref="SaEInvoiceTinSearchResult"/>): a
/// <see cref="Succeeded"/> flag, a <see cref="SaEInvoiceErrorKind"/> and a message ready to display.
/// No exceptions are thrown for expected failures.
/// </para>
/// </summary>
public sealed class SaEInvoiceDetailResult
{
    public bool Succeeded { get; init; }
    public SaEInvoiceErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Copied from <c>GeneralResult.errorCode</c> when present.
    ///
    /// <para>
    /// <b>Expect this to be null.</b> <c>E_InvoiceRepository.getDocumentDetail</c> never assigns
    /// <c>errorCode</c> on any failure path — it only sets <c>error</c>. The property is carried so the
    /// contract is honest and becomes useful if that is ever fixed; no logic may <i>require</i> it.
    /// </para>
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>The document detail, set only when <see cref="Succeeded"/> is true.</summary>
    public SaEInvoiceDetailView? Detail { get; init; }

    public static SaEInvoiceDetailResult Ok(SaEInvoiceDetailView detail) =>
        new() { Succeeded = true, ErrorKind = SaEInvoiceErrorKind.None, Detail = detail };

    public static SaEInvoiceDetailResult Fail(
        string message,
        SaEInvoiceErrorKind kind = SaEInvoiceErrorKind.MyInvois,
        string? errorCode = null) =>
        new() { Succeeded = false, ErrorKind = kind, ErrorMessage = message, ErrorCode = errorCode };
}
