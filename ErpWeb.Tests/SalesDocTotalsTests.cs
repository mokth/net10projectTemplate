using ErpWeb.Core.Sales;

namespace ErpWeb.Tests;

/// <summary>R7 — the <see cref="SalesDocTotals"/> projection must agree across document kinds.</summary>
public class SalesDocTotalsTests
{
    [Fact]
    public void InvoiceLike_reconciled_row_is_passed_through()
    {
        // Post-R2: GrossAmnt is ex-tax and the identity holds.
        var totals = SalesDocTotals.FromInvoiceLike(grossAmnt: 100m, taxes: 10m, totAmnt: 110m);
        Assert.Equal(100m, totals.GrossExTax);
        Assert.Equal(10m, totals.Tax);
        Assert.Equal(110m, totals.TotalIncTax);
        Assert.True(totals.IsReconciled);
    }

    [Fact]
    public void InvoiceLike_mismatch_derives_gross_from_authoritative_total()
    {
        // Historical / inconsistent row: TotAmnt stays authoritative.
        var totals = SalesDocTotals.FromInvoiceLike(grossAmnt: 110m, taxes: 10m, totAmnt: 110m);
        Assert.Equal(100m, totals.GrossExTax);
        Assert.Equal(10m, totals.Tax);
        Assert.Equal(110m, totals.TotalIncTax);
        Assert.True(totals.IsReconciled);
    }

    [Fact]
    public void DeliveryOrder_always_derives_gross_from_total()
    {
        // Legacy inclusive DO: the tax-inclusive amount was stored in GrossAmnt with Taxes = 0.
        var totals = SalesDocTotals.FromDeliveryOrder(grossAmnt: 111m, taxes: 11m, totAmnt: 111m);
        Assert.Equal(100m, totals.GrossExTax);
        Assert.Equal(11m, totals.Tax);
        Assert.Equal(111m, totals.TotalIncTax);
        Assert.True(totals.IsReconciled);
    }

    [Fact]
    public void Do_totals_projection_matches_invoice_convention()
    {
        // Same economic document expressed both ways must project identically.
        var fromInvoice = SalesDocTotals.FromInvoiceLike(100m, 10m, 110m);
        var fromDo = SalesDocTotals.FromDeliveryOrder(100m, 10m, 110m);
        Assert.Equal(fromInvoice, fromDo);
    }

    [Fact]
    public void FromLines_matches_the_locked_inclusive_matrix_row()
    {
        var line = new SaInvoiceLineCalcState
        {
            Line = 1,
            Qty = 1m,
            UnitPrice = 110m,
            ItemDiscAmount = 11m,
            IsInclusive = true
        };
        SaInvoiceCalc.CalculateLine(line, taxPercent: 10m, decPoint: false, discMethod: null);

        var totals = SalesDocTotals.FromLines([line], decPoint: false);
        Assert.Equal(90m, totals.GrossExTax);
        Assert.Equal(9m, totals.Tax);
        Assert.Equal(99m, totals.TotalIncTax);
        Assert.True(totals.IsReconciled);
    }
}
