using ErpWeb.Core.Sales;
using ErpWeb.UI.Sales.Transactions;

namespace ErpWeb.Tests.Sales.Transaction;

[Trait(TestCategories.Name, TestCategories.Sales)]
[Trait(TestCategories.Name, TestCategories.SalesPricing)]
public sealed class SaLinePricingTriggerTests
{
    [Fact]
    public void New_direct_line_allows_live_resolve()
    {
        Assert.True(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: false,
            hasInheritedSource: false,
            isManualOverride: false));
    }

    [Fact]
    public void C19_existing_line_does_not_live_resolve()
    {
        Assert.False(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: true,
            hasInheritedSource: false,
            isManualOverride: false));
    }

    [Fact]
    public void C08_source_inherited_line_does_not_live_resolve()
    {
        Assert.True(SaLinePricingTrigger.HasInheritedSource("DO-1", (short)1));
        Assert.False(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: false,
            hasInheritedSource: true,
            isManualOverride: false));
    }

    [Fact]
    public void Authorized_manual_override_does_not_live_resolve()
    {
        Assert.False(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: false,
            hasInheritedSource: false,
            isManualOverride: true));
    }

    [Fact]
    public void C08_do_from_sales_order_is_source_inherited()
    {
        var vm = SaDoLineVm.FromSalesOrder(
            new SaSoLineDto
            {
                Line = 1,
                ICode = "A100",
                BalanceQty = 40m,
                UnitPrice = 10m
            },
            "SO-1");

        Assert.True(vm.HasSource);
        Assert.Equal("SO-1", vm.SoNo);
        Assert.Equal((short)1, vm.SoLine);
        Assert.Equal(10m, vm.UnitPrice);
        Assert.Equal(40m, vm.Qty);
        Assert.False(SaLinePricingTrigger.AllowLiveResolve(
            isEditingExistingLine: false,
            hasInheritedSource: vm.HasSource,
            isManualOverride: false));
    }

    [Theory]
    [InlineData("BOX", "PCS", "BOX")]
    [InlineData("  BOX  ", "PCS", "BOX")]
    [InlineData(null, "PCS", "PCS")]
    [InlineData("", "PCS", "PCS")]
    [InlineData("  ", "EA", "EA")]
    public void CommercialUom_prefers_selling_uom(string? selling, string? std, string expected)
    {
        Assert.Equal(expected, SaLinePricingTrigger.CommercialUom(selling, std));
    }

    [Theory]
    [InlineData(null, (short)1, false)]
    [InlineData("", (short)1, false)]
    [InlineData("  ", (short)1, false)]
    [InlineData("SO-1", (short)0, false)]
    [InlineData("SO-1", null, false)]
    [InlineData("SO-1", (short)2, true)]
    public void HasInheritedSource_uses_document_keys_not_price_tokens(string? docNo, short? line, bool expected)
    {
        Assert.Equal(expected, SaLinePricingTrigger.HasInheritedSource(docNo, line));
    }
}
