using ErpWeb.Core.Sales;
using ErpWeb.UI.Sales.Transactions;

namespace ErpWeb.Tests.Sales.Transaction;

[Trait(TestCategories.Name, TestCategories.Sales)]
public sealed class SaPriceInfoPresentationTests
{
    [Theory]
    [InlineData(SaPriceSourceTokens.CustomerItem, "Customer Special")]
    [InlineData(SaPriceSourceTokens.CustomerPriceList, "Customer Price List")]
    [InlineData(SaPriceSourceTokens.CustomerGroupPriceList, "Customer Group Price List")]
    [InlineData(SaPriceSourceTokens.ItemDefault, "Item Default")]
    public void SourceLabel_UsesBusinessFriendlyName(string token, string expected)
    {
        Assert.Equal(expected, SaPriceInfoPresentation.SourceLabel(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void SourceLabel_BlankTokenIsNotRecorded(string? token)
    {
        Assert.Equal("Not recorded", SaPriceInfoPresentation.SourceLabel(token));
    }

    [Fact]
    public void SourceLabel_UnknownTokenIsPreservedWithoutThrowing()
    {
        Assert.Equal("Recorded source (LEGACY_SOURCE)", SaPriceInfoPresentation.SourceLabel("LEGACY_SOURCE"));
    }

    [Fact]
    public void Classify_ResolvedLineIsSystemResolved()
    {
        var context = Context(SaPriceSourceTokens.CustomerItem, unitPrice: 85m, originalPrice: 85m);

        Assert.Equal(SaPriceInfoStatus.SystemResolved, SaPriceInfoPresentation.Classify(context));
    }

    [Fact]
    public void Classify_DifferentRecordedBaselineIsManualOverride()
    {
        var context = Context(SaPriceSourceTokens.CustomerItem, unitPrice: 82m, originalPrice: 85m, reason: "Approved");

        Assert.Equal(SaPriceInfoStatus.ManualOverride, SaPriceInfoPresentation.Classify(context));
        Assert.Equal(-3m, SaPriceInfoPresentation.Difference(context));
        Assert.Equal(-3m / 85m * 100m, SaPriceInfoPresentation.DifferencePercent(context));
    }

    [Fact]
    public void DifferencePercent_ZeroBaselineDoesNotDivideByZero()
    {
        var context = Context(source: null, unitPrice: 50m, originalPrice: 0m, reason: "Manual entry");

        Assert.Equal(SaPriceInfoStatus.ManualPriceWithoutSystemSource, SaPriceInfoPresentation.Classify(context));
        Assert.Equal(50m, SaPriceInfoPresentation.Difference(context));
        Assert.Null(SaPriceInfoPresentation.DifferencePercent(context));
    }

    [Fact]
    public void Classify_InheritedLineIsNotReportedAsCurrentDocumentOverride()
    {
        var context = WithSource(Context(SaPriceSourceTokens.CustomerPriceList, unitPrice: 85m, originalPrice: null), "Sales Order", "SO000123", 2);

        Assert.Equal(SaPriceInfoStatus.Inherited, SaPriceInfoPresentation.Classify(context));
        Assert.Equal("Inherited from Sales Order SO000123 · Customer Price List", SaPriceInfoPresentation.Tooltip(context));
    }

    [Fact]
    public void Tooltip_InheritedFromDeliveryOrderIncludesDocumentNo()
    {
        var context = WithSource(Context(null, unitPrice: 10m, originalPrice: null), "Delivery Order", "DO-1", 1);

        Assert.Equal(SaPriceInfoStatus.Inherited, SaPriceInfoPresentation.Classify(context));
        Assert.Equal("Inherited from Delivery Order DO-1", SaPriceInfoPresentation.Tooltip(context));
    }

    [Fact]
    public void Glance_ShowsPriceUomSourceDiscountAndInheritedState()
    {
        var context = WithSource(
            Context(SaPriceSourceTokens.CustomerPriceList, unitPrice: 85m, originalPrice: null, reference: "PL1"),
            "Invoice",
            "INV-1",
            1);
        context = WithCommercial(context, uom: "BOX", stockUom: "PCS", discount1: 10m, amount1: 1.25m);

        Assert.Equal(SaPriceInfoStatus.Inherited, SaPriceInfoPresentation.Classify(context));
        Assert.Equal("BOX (stock PCS)", SaPriceInfoPresentation.UomLabel(context));
        Assert.Equal("10% + 1.25", SaPriceInfoPresentation.DiscountLabel(context));
        Assert.Equal(
            "RM 85.0000 / BOX (stock PCS) · Customer Price List · Discount 10% + 1.25 · Inherited from source document",
            SaPriceInfoPresentation.Glance(context));
    }

    [Fact]
    public void DiscountLabel_NoSlotsIsNone()
    {
        Assert.Equal("None", SaPriceInfoPresentation.DiscountLabel(Context(null, 10m, null)));
    }

    [Fact]
    public void Tooltip_UsesOnlyRecordedTokenAndReference()
    {
        var context = Context(SaPriceSourceTokens.CustomerPriceList, unitPrice: 85m, originalPrice: null, reference: "DEALER QTY 10+");

        Assert.Equal("Customer Price List · DEALER QTY 10+", SaPriceInfoPresentation.Tooltip(context));
    }

    [Fact]
    public void Tooltip_UnknownAndLegacyLinesRemainSafe()
    {
        var legacy = Context(null, unitPrice: 50m, originalPrice: null);
        var unknown = Context("LEGACY_SOURCE", unitPrice: 50m, originalPrice: null);

        Assert.Equal("Price information", SaPriceInfoPresentation.Tooltip(legacy));
        Assert.Equal("Recorded source (LEGACY_SOURCE)", SaPriceInfoPresentation.Tooltip(unknown));
    }

    private static SaPriceInfoContext Context(
        string? source,
        decimal unitPrice,
        decimal? originalPrice,
        string? reason = null,
        string? reference = null) => new()
        {
            PricingSource = source,
            PricingRef = reference,
            UnitPrice = unitPrice,
            OriginalUnitPrice = originalPrice,
            OverrideReason = reason
        };

    private static SaPriceInfoContext WithSource(SaPriceInfoContext context, string type, string no, int line) => new()
    {
        PricingSource = context.PricingSource,
        PricingRef = context.PricingRef,
        UnitPrice = context.UnitPrice,
        OriginalUnitPrice = context.OriginalUnitPrice,
        OverrideReason = context.OverrideReason,
        SourceDocumentType = type,
        SourceDocumentNo = no,
        SourceDocumentLine = line
    };

    private static SaPriceInfoContext WithCommercial(
        SaPriceInfoContext context,
        string uom,
        string? stockUom,
        decimal discount1,
        decimal amount1) => new()
    {
        PricingSource = context.PricingSource,
        PricingRef = context.PricingRef,
        UnitPrice = context.UnitPrice,
        OriginalUnitPrice = context.OriginalUnitPrice,
        OverrideReason = context.OverrideReason,
        SourceDocumentType = context.SourceDocumentType,
        SourceDocumentNo = context.SourceDocumentNo,
        SourceDocumentLine = context.SourceDocumentLine,
        Currency = "MYR",
        Uom = uom,
        StockUom = stockUom,
        ItemDiscount = discount1,
        ItemDiscAmount = amount1
    };
}
