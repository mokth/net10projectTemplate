namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// Physical stock-count (cycle count) document header.
///
/// Lifecycle (see <c>IvStockCountStatuses</c>):
/// DRAFT → COUNTED → POSTED → ROLLED_BACK (→ COUNTED → POSTED), plus CANCELLED from any
/// non-POSTED state. A count posts as a normal <c>ADJ</c> <see cref="IvTrxBatch"/> whose
/// <c>RefNo</c> is this document's <see cref="CountNo"/> — there is deliberately no separate
/// StockCount transaction type.
///
/// The document is evidence: once POSTED, the frozen count columns are never mutated again
/// (rollback and <c>Recover</c> only move the status/stamps and the linked batch).
/// </summary>
public class IvStockCountHdr
{
    public int Id { get; set; }

    /// <summary>Tenant company (nvarchar(5), matches TenantScopeContext.MaxCompanyLength).</summary>
    public string CompanyCode { get; set; } = string.Empty;

    public string BranchCode { get; set; } = string.Empty;

    /// <summary>Human-readable sheet number, <c>CC000001</c>. Prefix is CC, never SC.</summary>
    public string CountNo { get; set; } = string.Empty;

    /// <summary>
    /// The physical-count business date (legacy <c>StartCount</c>). Becomes the ADJ batch
    /// <c>TrxDtTime</c> at post, so it is also the date written into <c>IvBalLoc.TransDate</c>
    /// by the stock-move helpers. Interpreted in company-local time and bounded by
    /// <c>IvStockCountLimits.MaxBackdateDays</c> at post time.
    /// </summary>
    public DateTime CountDate { get; set; }

    /// <summary>DRAFT / COUNTED / POSTED / ROLLED_BACK / CANCELLED.</summary>
    public string Status { get; set; } = string.Empty;

    // ── Generate scope stamps (kept so a sheet can be re-edited and re-printed) ──────────────────

    public string? WHCode { get; set; }
    public string? LocCode { get; set; }
    public string? IClassCode { get; set; }
    public string? ISubClassCode { get; set; }
    public string? IType { get; set; }

    /// <summary>Comma-separated item-status scope. SCRAPS is excluded unless added here explicitly.</summary>
    public string? IStatus { get; set; }

    /// <summary>Comma-separated optional item IN-list.</summary>
    public string? ICodeList { get; set; }

    /// <summary>Snapshot of the Generate option that produced the sheet.</summary>
    public bool IncludeZeroQty { get; set; }

    public string? CountedBy { get; set; }
    public string? Remark { get; set; }

    // ── Posting audit ────────────────────────────────────────────────────────────────────────────

    /// <summary>The generated ADJ batch number. NULL only for the all-zero-variance post.</summary>
    public int? PostedBatchNo { get; set; }

    public string? PostedBy { get; set; }
    public DateTime? PostedOn { get; set; }

    /// <summary>How many lines were stale (live qty != SystemQty) when the sheet was posted. 0 = none.</summary>
    public int? PostedStaleLines { get; set; }

    // ── Rollback audit (kept across a re-post so the trail survives) ───────────────────────────────

    public string? RolledBackBy { get; set; }
    public DateTime? RolledBackOn { get; set; }
    public string? RollbackReason { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<IvStockCountLine> Lines { get; set; } = new List<IvStockCountLine>();
}
