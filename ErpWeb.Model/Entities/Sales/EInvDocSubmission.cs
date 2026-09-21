using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// One row per <b>submitted e-Invoice document</b> (INV / CN / DN) — the "submission registry" that
/// records what was actually sent to LHDN MyInvois.
///
/// <para>
/// This is a <b>legacy physical table</b> that ErpWeb now owns. Column names are therefore preserved
/// verbatim (lower camel case, as they exist in SQL Server) and mapped explicitly. That is why this
/// entity carries the column shape as attributes while most ErpWeb entities keep it in a
/// <c>*Configuration</c> class: the attributes are a 1:1 record of the live table, verified against
/// <c>docs/einvoice-history-phase0-findings.txt</c>. Keys and indexes still live in
/// <see cref="ErpWeb.Model.Configurations.Sales.EInvDocSubmissionConfiguration"/>.
/// </para>
///
/// <para>
/// Locked rules (plan R1..R9, decisions D-1..D-17):
/// <list type="bullet">
/// <item>One row = one submitted document within one submission; a batch creates one row per document.</item>
/// <item>Only <b>accepted</b> documents are persisted (Model A). A fully rejected submission writes no row.</item>
/// <item>Key authority is <c>(companyID, submissionUUID, documentType, documentNo)</c>.</item>
/// <item>A re-submission after REJECTED/CANCELLED gets a new <c>submissionUUID</c> and therefore an
/// <b>additional</b> row. The <i>current</i> row for a document is the highest <c>ID</c> for that
/// document — do not assume one row per document number.</item>
/// <item>All timestamps written by ErpWeb are UTC.</item>
/// <item><c>document</c> (the raw signed payload) is <b>never written</b>, matching the
/// <c>SaEInvoiceLog</c> security contract.</item>
/// </list>
/// </para>
///
/// <para>
/// Live shapes that differ from the legacy designer and are easy to get wrong: the three
/// <c>dateTime*</c> columns (plus <c>cancelDateTime</c>/<c>rejectRequestDateTime</c>) are
/// <c>datetime</c> and <b>not</b> <c>datetime2</c>; <c>document</c> is the deprecated <c>text</c> LOB;
/// <c>documentType</c> is only <c>nvarchar(3)</c>; <c>status</c> is <b>NOT NULL with no default</b>, so
/// every insert must set it.
/// </para>
/// </summary>
[Table("EInvDocSubmission", Schema = "dbo")]
public class EInvDocSubmission
{
    // ── Identity ──────────────────────────────────────────────────────────────
    // Live: int IDENTITY(1,1). Phase 0 found the legacy table was a HEAP with no primary key,
    // so scripts/alter-einvdocsubmission-einvoice.sql adds PK_EInvDocSubmission.
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("ID")]
    public int Id { get; set; }

    // ── MyInvois document identity ────────────────────────────────────────────
    /// <summary>MyInvois document UUID (per document — differs between documents of one batch).</summary>
    [Column("uuid")]
    [MaxLength(50)]
    public string? Uuid { get; set; }

    /// <summary>MyInvois submission UID. Shared by every document accepted in one submission.</summary>
    [Column("submissionUUID")]
    [MaxLength(50)]
    public string? SubmissionUuid { get; set; }

    [Column("longId")]
    [MaxLength(100)]
    public string? LongId { get; set; }

    /// <summary>Supplier-side document code number returned by MyInvois for this document.</summary>
    [Column("internalId")]
    [MaxLength(30)]
    public string? InternalId { get; set; }

    [Column("typeName")]
    [MaxLength(20)]
    public string? TypeName { get; set; }

    [Column("typeVersionName")]
    [MaxLength(20)]
    public string? TypeVersionName { get; set; }

    // ── Parties ───────────────────────────────────────────────────────────────
    [Column("issuerTin")]
    [MaxLength(20)]
    public string? IssuerTin { get; set; }

    [Column("issuerName")]
    [MaxLength(250)]
    public string? IssuerName { get; set; }

    [Column("receiverId")]
    [MaxLength(30)]
    public string? ReceiverId { get; set; }

    [Column("receiverName")]
    [MaxLength(250)]
    public string? ReceiverName { get; set; }

    // ── Dates. Live type is `datetime` (NOT datetime2) — keep it. ─────────────
    /// <summary>
    /// e-Invoice business/document issue timestamp. Written provisionally from the submission instant
    /// (UTC) and superseded by the MyInvois value when a refresh/recover returns one.
    /// </summary>
    [Column("dateTimeIssued", TypeName = "datetime")]
    public DateTime? DateTimeIssued { get; set; }

    [Column("dateTimeReceived", TypeName = "datetime")]
    public DateTime? DateTimeReceived { get; set; }

    [Column("dateTimeValidated", TypeName = "datetime")]
    public DateTime? DateTimeValidated { get; set; }

    // ── Totals. Live type is decimal(18,2). ───────────────────────────────────
    [Column("totalSales", TypeName = "decimal(18,2)")]
    public decimal? TotalSales { get; set; }

    [Column("totalDiscount", TypeName = "decimal(18,2)")]
    public decimal? TotalDiscount { get; set; }

    [Column("netAmount", TypeName = "decimal(18,2)")]
    public decimal? NetAmount { get; set; }

    [Column("total", TypeName = "decimal(18,2)")]
    public decimal? Total { get; set; }

    // ── Status ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Document-level status. <b>NOT NULL with no database default</b> — every insert must set it.
    /// ErpWeb writes the <c>EInvoiceStatuses</c> constants (UPPERCASE); legacy rows hold Title-case
    /// literals such as <c>"Submitted"</c>. Read through <c>EInvoiceStatuses.Normalize</c>, which is
    /// case-insensitive. <c>REJECTED</c> never appears here (plan R3 / Model A).
    /// </summary>
    [Column("status")]
    [MaxLength(20)]
    [Required]
    public string Status { get; set; } = string.Empty;

    [Column("cancelDateTime", TypeName = "datetime")]
    public DateTime? CancelDateTime { get; set; }

    [Column("rejectRequestDateTime", TypeName = "datetime")]
    public DateTime? RejectRequestDateTime { get; set; }

    [Column("documentStatusReason")]
    [MaxLength(250)]
    public string? DocumentStatusReason { get; set; }

    [Column("createdByUserId")]
    [MaxLength(70)]
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// Raw signed payload. <b>Never written</b> (plan D-14): the table exists for submission history,
    /// and storing signed documents in it would contradict the <c>SaEInvoiceLog</c> security contract.
    /// Live type is the deprecated <c>text</c> LOB, so the store type is declared explicitly rather
    /// than relying on the <c>nvarchar</c> default.
    /// </summary>
    [Column("document", TypeName = "text")]
    public string? Document { get; set; }

    // ── ERP document identity (the tenant key is CompanyId) ───────────────────
    /// <summary>
    /// Company code — the <b>tenant key</b> (plan D-2). There is no separate TenantId in this system:
    /// tenancy is the 5-character company code against one shared database, so the unique key below
    /// already contains the tenant. Nullable to match the live column, but ErpWeb always supplies it.
    /// </summary>
    [Column("companyID")]
    [MaxLength(20)]
    public string? CompanyId { get; set; }

    /// <summary>Legacy surrogate document id. Mapped but <b>never written</b> (plan D-15) — no
    /// identity key exists on <c>SaInvoice</c>/<c>SaCdn</c>.</summary>
    [Column("documentID")]
    public int? DocumentId { get; set; }

    /// <summary>ERP document number: <c>SaInvoice.InvNo</c>, or the CN/DN number.</summary>
    [Column("documentNo")]
    [MaxLength(30)]
    public string? DocumentNo { get; set; }

    /// <summary>
    /// <c>EInvoiceDocumentTypes</c>: <c>INV</c> / <c>CN</c> / <c>DN</c>. The legacy writer hard-coded
    /// <c>"POS"</c>, which this table no longer receives. Live width is only <c>nvarchar(3)</c>, which
    /// fits all six families (INV/CN/DN and the SBI/SBC/SBD self-billed tokens).
    /// </summary>
    [Column("documentType")]
    [MaxLength(3)]
    public string? DocumentType { get; set; }

    // ── Payload capture columns present in the LIVE table ─────────────────────
    // Phase 0 found these 17 columns in the database although the legacy entity in
    // ErpWeb.EInvoiceLib never mapped them. They are mapped here so the mirror is complete and the
    // columns are visible to the model; ErpWeb does not populate them (they stay NULL), exactly like
    // `document`. Populating them is a separate decision, not a silent side effect of this feature.
    [Column("supplierTIN")]
    [MaxLength(20)]
    public string? SupplierTin { get; set; }

    [Column("supplierName")]
    [MaxLength(250)]
    public string? SupplierName { get; set; }

    [Column("buyerName")]
    [MaxLength(250)]
    public string? BuyerName { get; set; }

    [Column("buyerTIN")]
    [MaxLength(20)]
    public string? BuyerTin { get; set; }

    [Column("receiverTIN")]
    [MaxLength(20)]
    public string? ReceiverTin { get; set; }

    [Column("receiverIDType")]
    [MaxLength(50)]
    public string? ReceiverIdType { get; set; }

    [Column("submissionChannel")]
    [MaxLength(20)]
    public string? SubmissionChannel { get; set; }

    [Column("intermediaryName")]
    [MaxLength(250)]
    public string? IntermediaryName { get; set; }

    [Column("intermediaryTIN")]
    [MaxLength(20)]
    public string? IntermediaryTin { get; set; }

    [Column("intermediaryROB")]
    [MaxLength(30)]
    public string? IntermediaryRob { get; set; }

    [Column("issuerID")]
    [MaxLength(50)]
    public string? IssuerId { get; set; }

    [Column("issuerIDType")]
    [MaxLength(20)]
    public string? IssuerIdType { get; set; }

    [Column("documentCurrency")]
    [MaxLength(10)]
    public string? DocumentCurrency { get; set; }

    /// <summary>MyInvois document direction (e.g. outbound/inbound). Not written by ErpWeb.</summary>
    [Column("direction")]
    [MaxLength(10)]
    public string? Direction { get; set; }

    // ── Additive columns (the only five ErpWeb adds) ──────────────────────────
    /// <summary>Branch the submission was made from. Branch is a stamp, never part of the key.</summary>
    [Column("BranchCode")]
    [MaxLength(5)]
    public string? BranchCode { get; set; }

    /// <summary>
    /// <b>Submission-level</b> MyInvois <c>overallStatus</c> (observed: submitted / inprogress / valid /
    /// invalid / cancelled), normalised UPPERCASE and stored verbatim. This is the API's own vocabulary,
    /// so it is deliberately <b>not</b> constrained to <c>EInvoiceStatuses.All</c> — it is diagnostic
    /// and no state machine may act on it. Distinct from <see cref="Status"/>, which is the
    /// document-level status.
    /// </summary>
    [Column("OverallStatus")]
    [MaxLength(20)]
    public string? OverallStatus { get; set; }

    /// <summary>Documents in the submission. On submit this is the ERP batch size; on recover the API value wins.</summary>
    [Column("DocumentCount")]
    public int? DocumentCount { get; set; }

    /// <summary>First ErpWeb history-row creation (UTC). Distinct from the e-Invoice's own issue date.</summary>
    [Column("CreatedOn", TypeName = "datetime2")]
    public DateTime? CreatedOn { get; set; }

    /// <summary>Last successful synchronisation from MyInvois (UTC).</summary>
    [Column("LastSyncedOn", TypeName = "datetime2")]
    public DateTime? LastSyncedOn { get; set; }
}
