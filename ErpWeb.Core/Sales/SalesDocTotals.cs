namespace ErpWeb.Core.Sales;

/// <summary>
/// R7 — the single projection for a sales document's money columns.
/// <para>
/// The three buyers of document totals must not disagree:
/// <list type="bullet">
/// <item><b>GrossExTax</b> — sum of line net amounts, <i>excluding</i> tax.</item>
/// <item><b>Tax</b> — sum of line tax amounts.</item>
/// <item><b>TotalIncTax</b> — <c>GrossExTax + Tax</c>; what the customer actually pays.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="TotalIncTax"/> is authoritative and equal to the legacy <c>TotAmnt</c> column.
/// After R2 (D2) exclusive and inclusive lines both persist an ex-tax <c>GrossAmnt</c>, so the
/// identity <c>GrossExTax + Tax == TotalIncTax</c> holds for every newly written document.
/// </para>
/// </summary>
public readonly record struct SalesDocTotals
{
    public decimal GrossExTax { get; private init; }
    public decimal Tax { get; private init; }
    public decimal TotalIncTax { get; private init; }

    /// <summary>True when the stored gross/tax reconcile to the inclusive total.</summary>
    public bool IsReconciled => GrossExTax + Tax == TotalIncTax;

    /// <summary>
    /// Invoice-like documents (INV, CN, DN, SO). Post-R2 rows persist an ex-tax <c>GrossAmnt</c> and
    /// satisfy the identity. If a row does not reconcile, <c>TotAmnt</c> stays authoritative and the
    /// ex-tax gross is derived from it (defensive; historical pre-R2 inclusive rows may still carry a
    /// tax-inclusive gross and are accepted as a known historical inaccuracy — see D12).
    /// </summary>
    public static SalesDocTotals FromInvoiceLike(decimal grossAmnt, decimal taxes, decimal totAmnt)
    {
        var gross = SaInvoiceCalc.Money(grossAmnt);
        var tax = SaInvoiceCalc.Money(taxes);
        var total = SaInvoiceCalc.Money(totAmnt);
        if (gross + tax != total)
        {
            gross = SaInvoiceCalc.Money(total - tax);
        }

        return new SalesDocTotals { GrossExTax = gross, Tax = tax, TotalIncTax = total };
    }

    /// <summary>
    /// Delivery orders. <c>TotAmnt</c> is authoritative and the ex-tax gross is always derived from
    /// it, because DO rows are the one place legacy pre-R2 inclusive values can survive (an inclusive
    /// line stored the tax-inclusive amount in <c>GrossAmnt</c> with <c>Taxes = 0</c>).
    /// </summary>
    public static SalesDocTotals FromDeliveryOrder(decimal grossAmnt, decimal taxes, decimal totAmnt)
    {
        _ = grossAmnt;
        var tax = SaInvoiceCalc.Money(taxes);
        var total = SaInvoiceCalc.Money(totAmnt);
        return new SalesDocTotals
        {
            GrossExTax = SaInvoiceCalc.Money(total - tax),
            Tax = tax,
            TotalIncTax = total
        };
    }

    /// <summary>Projects the totals straight from calculated lines via <see cref="SaInvoiceCalc.CalculateHeader"/>.</summary>
    public static SalesDocTotals FromLines(IReadOnlyList<SaInvoiceLineCalcState> lines, bool decPoint)
    {
        var header = SaInvoiceCalc.CalculateHeader(lines, decPoint);
        return FromInvoiceLike(header.GrossAmnt, header.Taxes, header.TotAmnt);
    }

    public override string ToString() => $"{GrossExTax:0.00} + {Tax:0.00} = {TotalIncTax:0.00}";
}
