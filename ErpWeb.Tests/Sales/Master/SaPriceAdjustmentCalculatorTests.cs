using ErpWeb.Core.Sales;

namespace ErpWeb.Tests.Sales.Master;

public sealed class SaPriceAdjustmentCalculatorTests
{
    [Fact]
    public void Set_price_allows_null_old_price_and_rounds()
    {
        var result = SaPriceAdjustmentCalculator.Calculate(
            null, SaPriceAdjustmentMethods.SetPrice, 12.345m, 2);

        Assert.True(result.Succeeded);
        Assert.Equal(12.35m, result.NewPrice);
    }

    [Fact]
    public void Increase_percent_uses_decimal_math()
    {
        var result = SaPriceAdjustmentCalculator.Calculate(
            100m, SaPriceAdjustmentMethods.IncreasePercent, 5m, 2);

        Assert.True(result.Succeeded);
        Assert.Equal(105m, result.NewPrice);
    }

    [Fact]
    public void Decrease_percent_rejects_more_than_one_hundred()
    {
        var result = SaPriceAdjustmentCalculator.Calculate(
            100m, SaPriceAdjustmentMethods.DecreasePercent, 100.01m, 2);

        Assert.False(result.Succeeded);
        Assert.Contains("100%", result.Error);
    }

    [Fact]
    public void Decrease_amount_rejects_negative_result()
    {
        var result = SaPriceAdjustmentCalculator.Calculate(
            10m, SaPriceAdjustmentMethods.DecreaseAmount, 10.01m, 2);

        Assert.False(result.Succeeded);
        Assert.Contains("negative", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Percent_and_amount_methods_block_null_old_price()
    {
        foreach (var method in SaPriceAdjustmentMethods.All.Where(x => x != SaPriceAdjustmentMethods.SetPrice))
        {
            var result = SaPriceAdjustmentCalculator.Calculate(null, method, 1m);
            Assert.False(result.Succeeded);
            Assert.Null(result.NewPrice);
        }
    }

    [Fact]
    public void Explicit_zero_is_not_treated_as_missing()
    {
        var result = SaPriceAdjustmentCalculator.Calculate(
            10m, SaPriceAdjustmentMethods.SetPrice, 0m, 2);

        Assert.True(result.Succeeded);
        Assert.Equal(0m, result.NewPrice);
    }

    [Theory]
    [InlineData(SaPriceRoundingModes.Normal, "1.235", "1.24")]
    [InlineData(SaPriceRoundingModes.Up, "1.231", "1.24")]
    [InlineData(SaPriceRoundingModes.Down, "1.239", "1.23")]
    public void Rounding_direction_is_applied_once(string mode, string valueText, string expectedText)
    {
        var value = decimal.Parse(valueText, System.Globalization.CultureInfo.InvariantCulture);
        var expected = decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture);
        var result = SaPriceAdjustmentCalculator.Calculate(
            null, SaPriceAdjustmentMethods.SetPrice, value, 2, mode);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.NewPrice);
    }

    [Fact]
    public void Four_decimal_precision_and_midpoint_are_supported()
    {
        var result = SaPriceAdjustmentCalculator.Calculate(
            null, SaPriceAdjustmentMethods.SetPrice, 1.23445m, 4);

        Assert.True(result.Succeeded);
        Assert.Equal(1.2345m, result.NewPrice);
    }
}
