using ErpWeb.Core.Sales;
using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Tests.Sales.Transaction;

[Trait(TestCategories.Name, TestCategories.Sales)]
public sealed class SaProductionDemandRulesTests
{
    [Theory]
    [InlineData(PrMfgTypes.Buy, 0, SaProductionDemandRules.StockOrPurchased)]
    [InlineData(PrMfgTypes.Buy, 2, SaProductionDemandRules.StockOrPurchased)]
    [InlineData(PrMfgTypes.Make, 1, SaProductionDemandRules.AutoProduction)]
    [InlineData(PrMfgTypes.Make, 0, SaProductionDemandRules.SetupRequired)]
    [InlineData(PrMfgTypes.Phantom, 0, SaProductionDemandRules.InternalPhantom)]
    public void DetermineState_maps_supply_method_and_setup(
        string mfgType,
        int activeDefinitionCount,
        string expected)
    {
        Assert.Equal(expected, SaProductionDemandRules.DetermineState(mfgType, activeDefinitionCount));
    }

    [Theory]
    [InlineData(SaSoStatuses.New, true)]
    [InlineData(SaSoStatuses.Shipped, true)]
    [InlineData(SaSoStatuses.Closed, false)]
    [InlineData(SaSoStatuses.Superseded, false)]
    public void IsDirectProductionCandidate_respects_status_and_setup(string status, bool expected)
    {
        Assert.Equal(
            expected,
            SaProductionDemandRules.IsDirectProductionCandidate(status, PrMfgTypes.Make, 1));
    }

    [Fact]
    public void Frozen_factor_prefers_positive_snapshot_pack_size()
    {
        Assert.Equal(2.5m, SaProductionDemandRules.ResolveFrozenStdFactor(2.5m, 10m, 99m));
    }

    [Fact]
    public void Frozen_factor_derives_legacy_snapshot_ratio()
    {
        Assert.Equal(2.5m, SaProductionDemandRules.ResolveFrozenStdFactor(0m, 10m, 25m));
    }

    [Fact]
    public void Frozen_factor_is_unresolved_without_safe_snapshot()
    {
        Assert.Null(SaProductionDemandRules.ResolveFrozenStdFactor(0m, 0m, 25m));
        Assert.Null(SaProductionDemandRules.ResolveFrozenStdFactor(0m, 10m, 0m));
    }

    [Fact]
    public void Conversion_rounds_in_production_and_sales_directions()
    {
        Assert.Equal(3.7037m, SaProductionDemandRules.ToProductionQty(1.23456m, 3m));
        Assert.Equal(1.2346m, SaProductionDemandRules.ToSalesQty(3.7037m, 3m));
    }

    [Fact]
    public void Outstanding_source_quantity_is_clamped_before_aggregation()
    {
        Assert.Equal(0m, SaSoDeliveryRequestCapacity.OutstandingForSource(10m, 12m));
        Assert.Equal(4m, SaSoDeliveryRequestCapacity.OutstandingForSource(10m, 6m));
    }
}
