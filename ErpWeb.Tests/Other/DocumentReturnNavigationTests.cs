using ErpWeb.UI.Services;

namespace ErpWeb.Tests.Other;
public class DocumentReturnNavigationTests
{
    [Fact]
    public void WithReturnUrl_AppendsSafeInquiryPath()
    {
        var url = DocumentReturnNavigation.WithReturnUrl(
            "/purchase/orders/view/PO1/2",
            "/purchase/inquiry/po-outstanding");
        Assert.StartsWith("/purchase/orders/view/PO1/2?", url);
        Assert.Contains("returnUrl=", url, StringComparison.Ordinal);
        Assert.True(DocumentReturnNavigation.TryGetSafeInquiryReturn("https://x" + url, out var ret));
        Assert.Equal("/purchase/inquiry/po-outstanding", ret);
    }

    [Fact]
    public void WithReturnUrl_RejectsOpenRedirectAndNonInquiry()
    {
        Assert.Equal(
            "/purchase/orders/view/PO1",
            DocumentReturnNavigation.WithReturnUrl("/purchase/orders/view/PO1", "https://evil.example/"));
        Assert.Equal(
            "/purchase/orders/view/PO1",
            DocumentReturnNavigation.WithReturnUrl("/purchase/orders/view/PO1", "/purchase/orders"));
        Assert.Equal(
            "/purchase/orders/view/PO1",
            DocumentReturnNavigation.WithReturnUrl("/purchase/orders/view/PO1", "/purchase/inquiry/../orders"));
    }

    [Fact]
    public void PreserveReturnUrl_CopiesQueryOntoEdit()
    {
        var current = "https://host/purchase/orders/view/PO1?returnUrl=%2Fpurchase%2Finquiry%2Fpr-status";
        var next = DocumentReturnNavigation.PreserveReturnUrl(current, "/purchase/orders/edit/PO1");
        Assert.StartsWith("/purchase/orders/edit/PO1?", next);
        Assert.True(DocumentReturnNavigation.TryGetSafeInquiryReturn("https://host" + next, out var ret));
        Assert.Equal("/purchase/inquiry/pr-status", ret);
    }

    [Fact]
    public void HasSafeInquiryReturn_DetectsQuery()
    {
        Assert.True(DocumentReturnNavigation.HasSafeInquiryReturn(
            "https://host/purchase/orders/view/PO1?returnUrl=%2Fpurchase%2Finquiry%2Fpo-outstanding"));
        Assert.False(DocumentReturnNavigation.HasSafeInquiryReturn(
            "https://host/purchase/orders/view/PO1"));
    }

    [Fact]
    public void WithReturnUrl_AllowsCostingCenter()
    {
        var url = DocumentReturnNavigation.WithReturnUrl(
            "/inventory/goods-receipts/view/80021",
            "/inventory/costing-center");
        Assert.Contains("returnUrl=", url, StringComparison.Ordinal);
        Assert.True(DocumentReturnNavigation.TryGetSafeInquiryReturn("https://x" + url, out var ret));
        Assert.Equal("/inventory/costing-center", ret);
    }
}
