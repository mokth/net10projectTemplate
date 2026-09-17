namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Append-only history of every LHDN MyInvois e-Invoice action taken for a sales document
/// (validate, submit, recover, refresh, cancel, retry).
/// <para>
/// The document row (<c>SaInvoice</c> / <c>SaCDN</c>) keeps only the <b>current</b> snapshot in its
/// <c>IRBM*</c> columns. This table keeps the full sequence, which is what makes a chain such as
/// "submit attempt 1 -> timeout -> recover -> valid" traceable.
/// </para>
/// <para>
/// Never store access tokens, client secrets, certificate passwords or full signed payloads here.
/// </para>
/// </summary>
public class SaEInvoiceLog
{
    public long Id { get; set; }

    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>ERP document family: <c>INV</c>, <c>CN</c>, <c>DN</c> (self-bill prefixes to follow).</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>ERP document number (invoice / credit note / debit note number).</summary>
    public string DocumentNo { get; set; } = string.Empty;

    /// <summary>MyInvois submission UID when known.</summary>
    public string? SubmissionId { get; set; }

    /// <summary>MyInvois document UUID when known.</summary>
    public string? DocumentUuid { get; set; }

    /// <summary>Validate | Submit | Recover | Refresh | Cancel | Retry.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Resulting <c>IrbmStatus</c> (or the outcome for a validate-only action).</summary>
    public string? Status { get; set; }

    /// <summary>1-based position of this row in the document's e-Invoice attempt chain.</summary>
    public int AttemptNo { get; set; }

    /// <summary>Groups every action belonging to one user-initiated attempt chain.</summary>
    public Guid? CorrelationId { get; set; }

    public DateTime? RequestTime { get; set; }
    public DateTime? ResponseTime { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>HTTP status / MyInvois error code when the action failed.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Full error text (or a structured JSON snippet of the API error details).</summary>
    public string? ErrorMessage { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedOn { get; set; }
}
