namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// Inventory period-close (month-end) header. One row per closed period per company+branch.
///
/// <para>
/// Lifecycle (see <c>IvPeriodCloseStatuses</c>): a period is CLOSED by <c>IIvPeriodCloseService.CloseAsync</c>
/// and REOPENED by <c>ReopenAsync</c>. A reopen never deletes the header — it stamps the reopen audit and
/// deletes only this feature's own derived snapshot rows (<see cref="IvPeriodCloseBal"/>), which are
/// re-derived on the next close. The period is identified by its dates (<c>PeriodFrom</c>/<c>PeriodTo</c>),
/// never by a document number (D5).
/// </para>
/// </summary>
public class IvPeriodCloseHdr
{
    public int Id { get; set; }

    /// <summary>Tenant company (nvarchar(5), matches TenantScopeContext.MaxCompanyLength).</summary>
    public string CompanyCode { get; set; } = string.Empty;

    public string BranchCode { get; set; } = string.Empty;

    /// <summary>First day of the closed period (inclusive, company-local date).</summary>
    public DateTime PeriodFrom { get; set; }

    /// <summary>Last day of the closed period (inclusive). May not be in the future (D7).</summary>
    public DateTime PeriodTo { get; set; }

    /// <summary>CLOSED / REOPENED.</summary>
    public string Status { get; set; } = string.Empty;

    public string? ClosedBy { get; set; }
    public DateTime? ClosedOn { get; set; }

    // ── Reopen audit (kept across a re-close so the trail survives) ────────────────────────────

    public int ReopenCount { get; set; }
    public string? ReopenedBy { get; set; }
    public DateTime? ReopenedOn { get; set; }
    public string? ReopenReason { get; set; }

    // ── Snapshot shape (evidence of what the close produced) ────────────────────────────────────

    /// <summary>Number of <see cref="IvPeriodCloseBal"/> rows written.</summary>
    public int LineCount { get; set; }

    /// <summary>How many zero-closing slices were excluded by design (D13).</summary>
    public int SkippedZeroSlices { get; set; }

    /// <summary>How many non-zero <c>OpeningAdjustQty</c> plugs were recorded (first close only).</summary>
    public int OpeningAdjustSlices { get; set; }

    /// <summary>How many slices failed the carry-forward equality against the prior close.</summary>
    public int CarryForwardMismatchSlices { get; set; }

    /// <summary>1 = the D11 pile-vs-ledger reconciliation was applicable and run; 0 = skipped (PostLegs != 0).</summary>
    public bool CurrentBalanceCheckApplies { get; set; }

    /// <summary>How many slices failed the D11 pile-vs-ledger reconciliation (when applicable).</summary>
    public int CurrentBalanceMismatchSlices { get; set; }

    // ── Value totals (4 dp, computed at close) ──────────────────────────────────────────────────

    public decimal TotalOpeningValue { get; set; }
    public decimal TotalInValue { get; set; }
    public decimal TotalOutValue { get; set; }
    public decimal TotalClosingValue { get; set; }

    /// <summary>Shape of the snapshot withdrawn by the last reopen (the count survives the deletion).</summary>
    public int LastReopenLineCount { get; set; }
    public decimal LastReopenClosingValue { get; set; }

    /// <summary>How many blocking NEW batches were reported at close time (D4).</summary>
    public int UnpostedBatchCount { get; set; }

    /// <summary>How many blocking reconciliation findings were reported at close time (D9).</summary>
    public int ReconcileFindingCount { get; set; }

    public string? Remark { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public ICollection<IvPeriodCloseBal> Lines { get; set; } = new List<IvPeriodCloseBal>();
}
