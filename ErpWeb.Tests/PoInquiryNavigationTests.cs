using ErpWeb.UI.Purchase.Inquiry;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Purchase)]
public class PoInquiryNavigationTests
{
    [Fact]
    public void TryResolvePo_WithRel_BuildsViewUrl()
    {
        Assert.True(PoInquiryNavigation.TryResolvePo("PO-1", 2, out var url));
        Assert.Equal("/purchase/orders/view/PO-1/2", url);
    }

    [Fact]
    public void TryResolvePo_WithoutRel_BuildsModeViewUrl()
    {
        Assert.True(PoInquiryNavigation.TryResolvePo("PO-1", null, out var url));
        Assert.Equal("/purchase/orders/view/PO-1", url);
    }

    [Fact]
    public void TryResolvePo_Blank_Fails()
    {
        Assert.False(PoInquiryNavigation.TryResolvePo("  ", 1, out _));
        Assert.False(PoInquiryNavigation.TryResolvePo(null, 1, out _));
    }

    [Fact]
    public void TryResolvePr_Invoice_Cdn_Sb()
    {
        Assert.True(PoInquiryNavigation.TryResolvePr("PR1", out var pr));
        Assert.Equal("/purchase/requisitions/view/PR1", pr);

        Assert.True(PoInquiryNavigation.TryResolveInvoice("INV1", out var inv));
        Assert.Equal("/purchase/invoices/view/INV1", inv);

        Assert.True(PoInquiryNavigation.TryResolveCdn("CN1", "CN", out var cn));
        Assert.Equal("/purchase/credit-notes/view/CN1", cn);

        Assert.True(PoInquiryNavigation.TryResolveCdn("DN1", "DN", out var dn));
        Assert.Equal("/purchase/debit-notes/view/DN1", dn);

        Assert.True(PoInquiryNavigation.TryResolveSelfBilled("SBI", "SBI1", out var sbi));
        Assert.Equal("/purchase/self-billed-invoices/view/SBI1", sbi);

        Assert.True(PoInquiryNavigation.TryResolveSelfBilled("SBC", "SBC1", out var sbc));
        Assert.Equal("/purchase/self-billed-credit-notes/view/SBC1", sbc);

        Assert.True(PoInquiryNavigation.TryResolveSelfBilled("SBD", "SBD1", out var sbd));
        Assert.Equal("/purchase/self-billed-debit-notes/view/SBD1", sbd);
    }

    [Fact]
    public void TryResolve_UnknownType_NeverInventRoute()
    {
        Assert.False(PoInquiryNavigation.TryResolveByDocType("XYZ", "DOC1", null, out _));
        Assert.False(PoInquiryNavigation.TryResolveSelfBilled("INV", "INV1", out _));
        Assert.False(PoInquiryNavigation.TryResolveRelationship("UNKNOWN", "A", "B", openSource: true, out _));
    }

    [Fact]
    public void TryResolveRelationship_PrPo_And_GrTargetBlocked()
    {
        Assert.True(PoInquiryNavigation.TryResolveRelationship("PR→PO", "PR1", "PO1", openSource: true, out var src));
        Assert.Equal("/purchase/requisitions/view/PR1", src);

        Assert.True(PoInquiryNavigation.TryResolveRelationship("PR→PO", "PR1", "PO1", openSource: false, out var tgt));
        Assert.Equal("/purchase/orders/view/PO1", tgt);

        Assert.False(PoInquiryNavigation.TryResolveRelationship("PO→GR", "PO1", "100", openSource: false, out _));
        Assert.True(PoInquiryNavigation.TryResolveRelationship("PO→GR", "PO1", "100", openSource: true, out var po));
        Assert.Equal("/purchase/orders/view/PO1", po);
    }

    [Fact]
    public void BlankDocNo_Fails()
    {
        Assert.False(PoInquiryNavigation.TryResolveInvoice(null, out _));
        Assert.False(PoInquiryNavigation.TryResolveByDocType("INV", "  ", null, out _));
        Assert.False(PoInquiryNavigation.TryResolveRelationship("PO→INV", null, "INV1", openSource: true, out _));
    }
}
