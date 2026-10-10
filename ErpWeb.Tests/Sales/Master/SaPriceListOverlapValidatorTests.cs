using ErpWeb.Core.Sales;

namespace ErpWeb.Tests.Sales.Master;

public sealed class SaPriceListOverlapValidatorTests
{
    [Fact]
    public void Same_item_uom_currency_band_and_window_is_conflict()
    {
        var lines = new[]
        {
            new SaPriceListOverlapLine(1, "ITEM1", "PCS", 0m, null, new DateTime(2026, 1, 1), null, "MYR"),
            new SaPriceListOverlapLine(2, "ITEM1", "PCS", 5m, 10m, new DateTime(2026, 2, 1), null, "MYR")
        };

        Assert.NotNull(SaPriceListOverlapValidator.FindConflict(lines));
    }

    [Fact]
    public void Different_currency_or_adjacent_window_is_allowed()
    {
        var differentCurrency = new[]
        {
            new SaPriceListOverlapLine(1, "ITEM1", "PCS", 0m, null, new DateTime(2026, 1, 1), null, "MYR"),
            new SaPriceListOverlapLine(2, "ITEM1", "PCS", 0m, null, new DateTime(2026, 1, 1), null, "USD")
        };
        var adjacentWindow = new[]
        {
            new SaPriceListOverlapLine(1, "ITEM1", "PCS", 0m, null, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31), "MYR"),
            new SaPriceListOverlapLine(2, "ITEM1", "PCS", 0m, null, new DateTime(2026, 2, 1), null, "MYR")
        };

        Assert.Null(SaPriceListOverlapValidator.FindConflict(differentCurrency));
        Assert.Null(SaPriceListOverlapValidator.FindConflict(adjacentWindow));
    }
}
