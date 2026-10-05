using ErpWeb.Core.Production;

namespace ErpWeb.Tests.Production.Transaction;
[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionMaterialExecutionCalcTests
{
    [Fact]
    public void Outstanding_supports_partial_and_repeated_issue()
    {
        Assert.Equal(60m, ProductionMaterialExecutionCalc.Outstanding(100m, 40m, 0m));
        Assert.Equal(30m, ProductionMaterialExecutionCalc.Outstanding(100m, 70m, 0m));
    }

    [Fact]
    public void Returned_quantity_reopens_the_requirement()
    {
        Assert.Equal(40m, ProductionMaterialExecutionCalc.Outstanding(100m, 70m, 10m));
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 5, 105)]
    [InlineData(1.23456, 0, 1.2346)]
    public void Maximum_issue_applies_tolerance_and_quantity_rounding(
        decimal required, decimal tolerance, decimal expected)
    {
        Assert.Equal(expected, ProductionMaterialExecutionCalc.MaxAllowedNetIssue(required, tolerance));
    }

    [Theory]
    [InlineData(100, 100, 20, 20)]
    [InlineData(100, 100, 100, 100)]
    [InlineData(50, 100, 40, 20)]
    public void Requested_for_production_scales_standard_bom(
        decimal required, decimal planned, decimal desired, decimal expected)
    {
        Assert.Equal(expected,
            ProductionMaterialExecutionCalc.RequestedForProductionQty(required, planned, desired));
    }

    [Fact]
    public void Max_for_production_applies_tolerance_to_standard_requested()
    {
        var requested = ProductionMaterialExecutionCalc.RequestedForProductionQty(100m, 100m, 20m);
        Assert.Equal(20m, requested);
        Assert.Equal(21m, ProductionMaterialExecutionCalc.MaxForProductionQty(requested, 5m));
    }

    [Fact]
    public void Effective_issue_subtracts_reversal_facts()
    {
        Assert.Equal(35m, ProductionMaterialExecutionCalc.MovementEffectiveIssue(40m, 5m));
    }

    [Theory]
    [InlineData(12.5, 2, 25)]
    [InlineData(12.5, 0.25, 3.125)]
    public void Conversion_to_base_supports_whole_and_fractional_factors(
        decimal issueQty, decimal conversion, decimal expectedBaseQty)
    {
        Assert.Equal(expectedBaseQty,
            ProductionMaterialExecutionCalc.BaseQtyForIssueQty(issueQty, conversion));
        Assert.Equal(issueQty,
            ProductionMaterialExecutionCalc.IssueQtyForBaseQty(expectedBaseQty, conversion));
    }

    [Fact]
    public void Final_allocation_receives_rounding_remainder()
    {
        var quantities = ProductionMaterialExecutionCalc.AllocateIssueQty(
            1m,
            [1m, 1m, 1m],
            3m);

        Assert.Equal([0.3333m, 0.3333m, 0.3334m], quantities);
        Assert.Equal(1m, quantities.Sum());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Conversion_factor_must_be_positive(decimal conversion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductionMaterialExecutionCalc.BaseQtyForIssueQty(1m, conversion));
    }

    [Fact]
    public void Daily_production_processed_is_good_scrap_reject_hold()
    {
        Assert.Equal(160m, ProductionMaterialExecutionCalc.ProcessedThisPost(100m, 20m, 30m, 10m));
    }

    [Fact]
    public void Daily_production_standard_prorates_wo_bom_for_this_post_only()
    {
        Assert.Equal(100m, ProductionMaterialExecutionCalc.DailyProductionStandardQty(1000m, 1000m, 100m));
        Assert.Equal(0m, ProductionMaterialExecutionCalc.DailyProductionStandardQty(1000m, 1000m, 0m));
        Assert.Equal(0m, ProductionMaterialExecutionCalc.DailyProductionStandardQty(1000m, 0m, 100m));
    }

    [Fact]
    public void Daily_production_max_is_document_standard_times_tolerance_not_full_wo()
    {
        var standard = ProductionMaterialExecutionCalc.DailyProductionStandardQty(1000m, 1000m, 100m);
        Assert.Equal(110m, ProductionMaterialExecutionCalc.DailyProductionMaxQty(standard, 10m));
    }

    [Fact]
    public void Daily_production_variance_is_signed_and_rounded()
    {
        Assert.Equal(5m, ProductionMaterialExecutionCalc.DailyProductionVarianceQty(105m, 100m));
        Assert.Equal(-5m, ProductionMaterialExecutionCalc.DailyProductionVarianceQty(95m, 100m));
        Assert.Equal(0m, ProductionMaterialExecutionCalc.DailyProductionVarianceQty(100m, 100m));
    }
}
