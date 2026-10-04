using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Sales;

namespace ErpWeb.Tests.Sales.Transaction;
/// <summary>
/// The e-Invoice caps have ONE definition — <see cref="SaEInvoiceLimits"/> — and the invoice-named
/// constants forward to it, so the invoice and credit/debit-note paths can never silently behave
/// differently.
///
/// <para>
/// Pinned by a test rather than by a comment (and not merely by the forwarding syntax): a future edit that
/// changes one constant without the other fails here, instead of shipping two different caps that only
/// show up as "the CN list refuses 11 notes but the invoice list accepts 11". Both are <c>const</c>, so the
/// assertion is the only thing that can catch the drift at build/test time.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.EInvoice)]
public class SaEInvoiceLimitsTests
{
    [Fact]
    public void Invoice_limits_forward_to_the_shared_e_invoice_limits()
    {
        Assert.Equal(SaEInvoiceLimits.MaxBatchSelection, SaInvoiceLimits.MaxEInvoiceBatchSelection);
        Assert.Equal(SaEInvoiceLimits.MaxRefreshAllRun, SaInvoiceLimits.MaxEInvoiceRefreshAllRun);
    }

    [Fact]
    public void Shared_limits_keep_the_values_the_batch_surface_was_designed_around()
    {
        // Not decoration: the interactive cap is what keeps one action inside the MyInvois rate limits, and
        // the run cap is what bounds a no-selection refresh. A change here is a deliberate, reviewed act.
        Assert.Equal(10, SaEInvoiceLimits.MaxBatchSelection);
        Assert.Equal(200, SaEInvoiceLimits.MaxRefreshAllRun);
    }
}
