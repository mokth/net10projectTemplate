using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionOutputMaterialFactsTests
{
    [Fact]
    public void Exact_consume_does_not_require_a_reason()
    {
        Assert.Null(ProductionOutputMaterialFacts.ValidateConsume(100m, 100m, 110m, null, null, interactive: true));
    }

    [Fact]
    public void Under_and_over_within_document_max_require_a_user_reason()
    {
        Assert.Contains("reason", ProductionOutputMaterialFacts.ValidateConsume(95m, 100m, 110m, null, null, true), StringComparison.OrdinalIgnoreCase);
        Assert.Null(ProductionOutputMaterialFacts.ValidateConsume(95m, 100m, 110m, ProductionMaterialVarianceReasonCodes.Yield, null, true));
        Assert.Null(ProductionOutputMaterialFacts.ValidateConsume(105m, 100m, 110m, ProductionMaterialVarianceReasonCodes.Damage, null, true));
    }

    [Fact]
    public void Over_document_max_is_rejected_even_with_a_reason()
    {
        Assert.Contains("maximum", ProductionOutputMaterialFacts.ValidateConsume(111m, 100m, 110m, ProductionMaterialVarianceReasonCodes.Yield, null, true), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Other_requires_text_and_legacy_is_interactive_only_rejected()
    {
        Assert.Contains("detail", ProductionOutputMaterialFacts.ValidateConsume(90m, 100m, 110m, ProductionMaterialVarianceReasonCodes.Other, null, true), StringComparison.OrdinalIgnoreCase);
        Assert.Null(ProductionOutputMaterialFacts.ValidateConsume(90m, 100m, 110m, ProductionMaterialVarianceReasonCodes.Other, "spillage", true));
        Assert.Contains("system-only", ProductionOutputMaterialFacts.ValidateConsume(90m, 100m, 110m, ProductionMaterialVarianceReasonCodes.LegacyUnclassified, null, true), StringComparison.OrdinalIgnoreCase);
        Assert.Null(ProductionOutputMaterialFacts.ValidateConsume(90m, 100m, 110m, ProductionMaterialVarianceReasonCodes.LegacyUnclassified, null, interactive: false));
        Assert.Contains("Unknown", ProductionOutputMaterialFacts.ValidateConsume(90m, 100m, 110m, "MADE_UP", null, true), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PrMaterialIssueMethods.Backflush, PrMaterialSupplySources.Purchased)]
    [InlineData(PrMaterialIssueMethods.PickList, PrMaterialSupplySources.Purchased)]
    [InlineData(PrMaterialIssueMethods.Manual, PrMaterialSupplySources.SeparateProductDefinition)]
    public void Unsupported_modes_share_the_same_blocking_reason(string issueMethod, string supplySource)
    {
        var material = new ProductionWorkOrderMaterial
        {
            IssueMethod = issueMethod,
            SupplySource = supplySource,
        };
        var reason = ProductionOutputMaterialSupport.BlockingReason(material);
        Assert.Equal(reason, ProductionOutputMaterialSupport.Validate(material));
        Assert.False(ProductionOutputMaterialSupport.IsSupported(material));
        Assert.Contains("not supported", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manual_purchased_and_internal_wip_with_producer_are_supported()
    {
        Assert.True(ProductionOutputMaterialSupport.IsSupported(new ProductionWorkOrderMaterial
        {
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased,
        }));
        Assert.True(ProductionOutputMaterialSupport.IsSupported(new ProductionWorkOrderMaterial
        {
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
            ProducingRouteStepId = 9,
        }));
        Assert.Contains("producing route", ProductionOutputMaterialSupport.BlockingReason(new ProductionWorkOrderMaterial
        {
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
        })!, StringComparison.OrdinalIgnoreCase);
    }
}
