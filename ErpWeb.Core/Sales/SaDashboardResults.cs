namespace ErpWeb.Core.Sales;

/// <summary>
/// The Sales Dashboard payload (plan-salesReportsAndInquiries.prompt.md Phase 3). KPI chips are
/// bounded server-side aggregates over the current company; the chart payloads reuse the existing
/// analysis result shapes (<see cref="SaSalesSummaryRow"/>, <see cref="SaSalesDetailRow"/>).
/// </summary>
public sealed class SaDashboardResult
{
    // ── Sales KPI chips (POSTED invoices only) ──────────────────────────────
    public decimal SalesToday { get; set; }
    public decimal SalesMonth { get; set; }
    public decimal SalesYear { get; set; }

    // ── Open quotation chip ─────────────────────────────────────────────────
    public int OpenQtCount { get; set; }
    public decimal OpenQtValue { get; set; }

    // ── Open sales order chips ──────────────────────────────────────────────
    public int OpenSoCount { get; set; }

    /// <summary>Sum of the open SO headers' <c>TotAmnt</c> (original value).</summary>
    public decimal OpenSoValue { get; set; }

    /// <summary><c>OpenSoValue −</c> the invoiced line value linked by <c>SaInvoiceDetail.SoNo</c>.</summary>
    public decimal OpenSoOutstandingValue { get; set; }

    // ── Delivery chips ──────────────────────────────────────────────────────
    /// <summary>Sum of <c>SaSoDetail.BalanceQty</c> over open SOs — never add open DO qty on top.</summary>
    public decimal PendingDeliveryQty { get; set; }

    /// <summary>Delivered-but-not-fully-billed DO quantity (a separate chip, never part of pending delivery).</summary>
    public decimal OpenDoQty { get; set; }

    // ── CN/DN chips (POSTED, stored positive, current month) ────────────────
    public decimal CreditNoteTotal { get; set; }
    public decimal DebitNoteTotal { get; set; }

    // ── Chart payloads (reuse the analysis result shapes) ───────────────────
    public IReadOnlyList<SaSalesSummaryRow> ByCustomer { get; set; } = [];
    public IReadOnlyList<SaSalesSummaryRow> BySalesperson { get; set; } = [];
    public IReadOnlyList<SaSalesDetailRow> ByCategory { get; set; } = [];
    public IReadOnlyList<SaSalesDetailRow> TopItems { get; set; } = [];
}
