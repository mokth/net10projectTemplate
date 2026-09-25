namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// One row per 7-part stock slice with a <b>non-zero closing quantity</b> for a closed period (D13).
///
/// <para>
/// This is a derived <b>ending-stock snapshot</b>, not a movement ledger: a slice that opened at 100,
/// received 50 in and issued 150 out closes at 0 and is deliberately absent. The full movement picture
/// stays in <c>IvTrxHistory</c>. The rows are deleted when their period is reopened (D8) and regenerated
/// on the next close, so a superseded snapshot can never be read as the current balance.
/// </para>
/// </summary>
public class IvPeriodCloseBal
{
    public int Id { get; set; }

    public int PeriodCloseId { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;

    public string ICode { get; set; } = string.Empty;
    public string WhCode { get; set; } = string.Empty;

    /// <summary>Empty string when unused — never null (matches the slice-key convention).</summary>
    public string LocCode { get; set; } = string.Empty;

    /// <summary>Empty string when unused — never null.</summary>
    public string LotNo { get; set; } = string.Empty;

    /// <summary>Empty string when unused — never null.</summary>
    public string IStatus { get; set; } = string.Empty;

    // ── Ledger replay + the first-close plug (all 4 dp via IvQty.Round) ─────────────────────────

    public decimal OpeningQty { get; set; }

    /// <summary>Signed first-close plug = AnchorClosing − LedgerClosing. Forced to 0 after the first close.</summary>
    public decimal OpeningAdjustQty { get; set; }

    public decimal InQty { get; set; }
    public decimal OutQty { get; set; }

    /// <summary>Subset view of In − Out (ADJ legs only). Never added to the closing equation.</summary>
    public decimal AdjustNetQty { get; set; }

    public decimal ClosingQty { get; set; }

    public string? StdUom { get; set; }

    public decimal UnitPrice { get; set; }
    public decimal ClosingValue { get; set; }

    /// <summary>How many history legs contributed to this slice's period movement.</summary>
    public int LegCount { get; set; }

    /// <summary>true iff OpeningQty_N equals the prior closed period's ClosingQty for this slice.</summary>
    public bool CarryForwardOk { get; set; }

    /// <summary>LedgerClosing − live StdQty; NULL when the D11 check did not apply (PostLegs != 0).</summary>
    public decimal? CurrentBalanceDelta { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }

    public IvPeriodCloseHdr Header { get; set; } = null!;
}
