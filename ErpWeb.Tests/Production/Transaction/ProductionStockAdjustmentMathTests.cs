using ErpWeb.Core.Production;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionStockAdjustmentMathTests
{
    [Fact]
    public void Full_depletion_removes_the_exact_remaining_pool_value()
    {
        Assert.Equal(1.000001m, ProductionStockAdjustmentMath.ProportionalTotal(3m, 1.000001m, 3m));
        Assert.Equal(0.666667m, ProductionStockAdjustmentMath.ProportionalTotal(3m, 1.000001m, 2m));
    }

    [Fact]
    public void Inbound_total_is_calculated_before_display_unit_cost()
    {
        var total = ProductionStockAdjustmentMath.InboundPoolTotal(7m, 0.000011m, 3m);
        Assert.Equal(0.000005m, total);
        Assert.Equal(0.000002m, ProductionStockAdjustmentMath.UnitCostFromTotal(3m, total));
    }

    [Fact]
    public void Manual_cost_is_total_first_and_zero_authority_remains_zero()
    {
        Assert.Equal(0.000001m, ProductionStockAdjustmentMath.TotalFromUnitCost(3m, 0.0000004m));
        Assert.Equal(0m, ProductionStockAdjustmentMath.TotalFromUnitCost(12m, 0m));
    }

    [Fact]
    public void Zero_value_pool_can_be_depleted_without_inventing_value()
    {
        Assert.Equal(0m, ProductionStockAdjustmentMath.ProportionalTotal(4m, 0m, 1m));
        Assert.Equal(0m, ProductionStockAdjustmentMath.ProportionalTotal(4m, 0m, 4m));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(10, 1, 11)]
    public void Invalid_pool_removal_fails_closed(decimal poolQty, decimal value, decimal requested)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductionStockAdjustmentMath.ProportionalTotal(poolQty, value, requested));
    }
}
