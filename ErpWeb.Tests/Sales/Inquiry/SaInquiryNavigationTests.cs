using ErpWeb.UI.Sales.Inquiry;
using ErpWeb.UI.Services;

namespace ErpWeb.Tests;

/// <summary>
/// Sales inquiry → document drill-down: the view-route resolver and the safe <c>returnUrl</c> that makes
/// a document's Close button come back to the inquiry (and keeps the sidebar from expanding
/// Transactions while it is open).
/// </summary>
[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesShared)]
public class SaInquiryNavigationTests
{
    [Fact]
    public void TryResolveQt_OpensTheDisplayedRevision()
    {
        Assert.True(SaInquiryNavigation.TryResolveQt("QT-1", 3, out var withRev));
        Assert.Equal("/sales/quotations/view/QT-1/3", withRev);

        Assert.True(SaInquiryNavigation.TryResolveQt("QT-1", null, out var latest));
        Assert.Equal("/sales/quotations/view/QT-1", latest);

        Assert.False(SaInquiryNavigation.TryResolveQt("  ", 3, out _));
    }

    [Fact]
    public void TryResolveSo_OpensTheDisplayedRevision()
    {
        Assert.True(SaInquiryNavigation.TryResolveSo("SO-9", 2, out var withRev));
        Assert.Equal("/sales/sales-orders/view/SO-9/2", withRev);

        Assert.True(SaInquiryNavigation.TryResolveSo("SO-9", null, out var latest));
        Assert.Equal("/sales/sales-orders/view/SO-9", latest);

        Assert.False(SaInquiryNavigation.TryResolveSo(null, 1, out _));
    }

    [Fact]
    public void TryResolveDoAndInvoice_UseTheViewRoute()
    {
        Assert.True(SaInquiryNavigation.TryResolveDo("DO-1", out var d));
        Assert.Equal("/sales/delivery-orders/view/DO-1", d);

        Assert.True(SaInquiryNavigation.TryResolveInvoice("INV-1", out var i));
        Assert.Equal("/sales/invoices/view/INV-1", i);

        Assert.False(SaInquiryNavigation.TryResolveDo(" ", out _));
        Assert.False(SaInquiryNavigation.TryResolveInvoice(null, out _));
    }

    [Fact]
    public void TryResolveCdn_TypeDecidesTheMenu()
    {
        Assert.True(SaInquiryNavigation.TryResolveCdn("CN-1", "CN", out var cn));
        Assert.Equal("/sales/credit-notes/view/CN-1", cn);

        Assert.True(SaInquiryNavigation.TryResolveCdn("DN-1", "DN", out var dn));
        Assert.Equal("/sales/debit-notes/view/DN-1", dn);

        // A blank type defaults to the credit-note path; an unknown type is refused, never guessed.
        Assert.True(SaInquiryNavigation.TryResolveCdn("X-1", null, out var blank));
        Assert.Equal("/sales/credit-notes/view/X-1", blank);
        Assert.False(SaInquiryNavigation.TryResolveCdn("X-1", "XYZ", out _));
        Assert.False(SaInquiryNavigation.TryResolveCdn(null, "CN", out _));
    }

    [Fact]
    public void TryResolveByDocType_CoversEveryInquiryDocumentType()
    {
        Assert.True(SaInquiryNavigation.TryResolveByDocType("QT", "QT-1", 1, out var qt));
        Assert.Equal("/sales/quotations/view/QT-1/1", qt);

        Assert.True(SaInquiryNavigation.TryResolveByDocType("SO", "SO-1", null, out var so));
        Assert.Equal("/sales/sales-orders/view/SO-1", so);

        Assert.True(SaInquiryNavigation.TryResolveByDocType("DO", "DO-1", null, out var d));
        Assert.Equal("/sales/delivery-orders/view/DO-1", d);

        Assert.True(SaInquiryNavigation.TryResolveByDocType("INV", "INV-1", null, out var i));
        Assert.Equal("/sales/invoices/view/INV-1", i);

        Assert.True(SaInquiryNavigation.TryResolveByDocType("CN", "CN-1", null, out var cn));
        Assert.Equal("/sales/credit-notes/view/CN-1", cn);

        Assert.True(SaInquiryNavigation.TryResolveByDocType("DN", "DN-1", null, out var dn));
        Assert.Equal("/sales/debit-notes/view/DN-1", dn);

        // Lower case is tolerated; an unknown type is not resolved.
        Assert.True(SaInquiryNavigation.TryResolveByDocType("inv", "INV-1", null, out _));
        Assert.False(SaInquiryNavigation.TryResolveByDocType("XYZ", "DOC1", null, out _));
        Assert.False(SaInquiryNavigation.TryResolveByDocType("INV", "  ", null, out _));
    }

    [Fact]
    public void TryResolveRelationship_ResolvesBothEnds()
    {
        Assert.True(SaInquiryNavigation.TryResolveRelationship("SO→DO", "SO-1", "DO-1", openSource: true, out var src));
        Assert.Equal("/sales/sales-orders/view/SO-1", src);

        Assert.True(SaInquiryNavigation.TryResolveRelationship("SO→DO", "SO-1", "DO-1", openSource: false, out var tgt));
        Assert.Equal("/sales/delivery-orders/view/DO-1", tgt);

        Assert.True(SaInquiryNavigation.TryResolveRelationship("DO→INV", "DO-1", "INV-1", openSource: false, out var inv));
        Assert.Equal("/sales/invoices/view/INV-1", inv);

        Assert.True(SaInquiryNavigation.TryResolveRelationship("INV→SO", "INV-1", "SO-1", openSource: false, out var so));
        Assert.Equal("/sales/sales-orders/view/SO-1", so);

        Assert.False(SaInquiryNavigation.TryResolveRelationship("INV→XX", "INV-1", "XX-1", openSource: true, out _));
        Assert.False(SaInquiryNavigation.TryResolveRelationship("SO→DO", null, "DO-1", openSource: true, out _));
    }

    [Fact]
    public void SalesInquiryPath_IsAnAcceptedReturnUrl()
    {
        var url = DocumentReturnNavigation.WithReturnUrl(
            "/sales/sales-orders/view/SO-1/2",
            "/sales/inquiry/so-outstanding");

        Assert.StartsWith("/sales/sales-orders/view/SO-1/2?", url, StringComparison.Ordinal);
        Assert.True(DocumentReturnNavigation.TryGetSafeInquiryReturn("https://x" + url, out var ret));
        Assert.Equal("/sales/inquiry/so-outstanding", ret);
        Assert.True(DocumentReturnNavigation.HasSafeInquiryReturn("https://x" + url));
    }

    [Fact]
    public void SalesInquiryPath_KeepsItsAppliedQueryString()
    {
        const string returnPath = "/sales/inquiry/qt-status?dateFrom=2026-09-01&custCode=C1";
        var url = DocumentReturnNavigation.WithReturnUrl("/sales/quotations/view/QT-1/1", returnPath);

        Assert.True(DocumentReturnNavigation.TryGetSafeInquiryReturn("https://x" + url, out var ret));
        Assert.Equal(returnPath, ret);
    }

    [Fact]
    public void NonInquiryPaths_AreStillRefused()
    {
        // A transaction list, an admin page and a cross-group path must all be refused — only the
        // /purchase/inquiry/ and /sales/inquiry/ groups may be a returnUrl.
        Assert.Equal(
            "/sales/sales-orders/view/SO-1",
            DocumentReturnNavigation.WithReturnUrl("/sales/sales-orders/view/SO-1", "/sales/sales-orders"));
        Assert.Equal(
            "/sales/sales-orders/view/SO-1",
            DocumentReturnNavigation.WithReturnUrl("/sales/sales-orders/view/SO-1", "/admin/users"));
        Assert.Equal(
            "/sales/sales-orders/view/SO-1",
            DocumentReturnNavigation.WithReturnUrl("/sales/sales-orders/view/SO-1", "/sales/inquiry/../orders"));
        Assert.False(DocumentReturnNavigation.HasSafeInquiryReturn("https://x/sales/sales-orders/view/SO-1"));
    }
}
