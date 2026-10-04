using ErpWeb.Core.Production;

namespace ErpWeb.Tests.Planning.Transaction;
[Trait(TestCategories.Name, TestCategories.Planning)]
public class ProductionWorkOrderCalcTests
{
    [Theory]
    [InlineData("1", "5", "2", "0", "0.4")]
    [InlineData("10", "5", "2", "10", "4.4")]
    [InlineData("0.2", "1", "0.25", "0", "0.05")]
    public void Required_qty_preserves_bom_ratio_and_scrap(
        string planned,
        string baseQty,
        string componentQty,
        string scrap,
        string expected)
    {
        var actual = ProductionWorkOrderCalc.RequiredQty(
            decimal.Parse(planned),
            decimal.Parse(baseQty),
            decimal.Parse(componentQty),
            decimal.Parse(scrap));

        Assert.Equal(decimal.Parse(expected), actual);
    }

    [Fact]
    public void Open_requirement_accounts_for_returns_and_never_goes_negative()
    {
        Assert.Equal(7m, ProductionWorkOrderCalc.OpenRequirementQty(10m, 5m, 2m));
        Assert.Equal(0m, ProductionWorkOrderCalc.OpenRequirementQty(10m, 15m, 1m));
    }

    [Fact]
    public void Operation_dispositions_reconcile_without_double_counting()
    {
        var processed = ProductionWorkOrderCalc.OperationProcessedQty(
            goodQty: 7m,
            scrapQty: 1m,
            rejectQty: 0.5m,
            holdQty: 1m,
            reworkQty: 0.5m);

        Assert.Equal(10m, processed);
        Assert.Equal(2m, ProductionWorkOrderCalc.OperationRemainingQty(12m, processed));
        Assert.Equal(8.5m, ProductionWorkOrderCalc.AvailableForNextOperation(
            goodQty: 7m,
            releasedHoldQty: 1m,
            acceptedReworkOutputQty: 0.5m,
            transferredQty: 0m));
    }

    [Theory]
    [InlineData("0", "0", "0")]
    [InlineData("5", "10", "50")]
    [InlineData("15", "10", "100")]
    public void Progress_is_zero_safe_and_capped(string numerator, string denominator, string expected)
    {
        Assert.Equal(decimal.Parse(expected), ProductionWorkOrderCalc.ProgressPercent(
            decimal.Parse(numerator), decimal.Parse(denominator)));
    }
}

