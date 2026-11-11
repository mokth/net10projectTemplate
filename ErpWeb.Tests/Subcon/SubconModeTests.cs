using ErpWeb.Core.Menus;
using ErpWeb.Core.Subcon;

namespace ErpWeb.Tests.Subcon;

[Trait(TestCategories.Name, TestCategories.Subcon)]
public class SubconModeTests : IAsyncLifetime
{
    private readonly SubconTestHarness _h = new();

    public async Task InitializeAsync() => await _h.SeedMastersAsync();

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Production_mode_cannot_be_saved()
    {
        var result = await _h.CreateService().SaveDispatchDraftAsync(
            SubconTestHarness.GoodDispatch(mode: SubconModes.Production));
        Assert.False(result.Succeeded);
        Assert.Equal(SubconBlockedReasons.NoProductionCustody, result.ErrorMessage);
    }

    [Fact]
    public async Task Work_order_line_cannot_mix_onto_inventory_send_out()
    {
        var result = await _h.CreateService().SaveDispatchDraftAsync(
            SubconTestHarness.GoodDispatch(workOrderNo: "WO-100"));
        Assert.False(result.Succeeded);
        Assert.Equal(SubconBlockedReasons.NoProductionCustody, result.ErrorMessage);
    }

    [Fact]
    public async Task Lot_controlled_item_is_rejected()
    {
        var result = await _h.CreateService().SaveDispatchDraftAsync(
            SubconTestHarness.GoodDispatch(item: "LOT1"));
        Assert.False(result.Succeeded);
        Assert.Equal(SubconBlockedReasons.LotControlled, result.ErrorMessage);
    }

    [Fact]
    public async Task Non_stock_item_is_rejected()
    {
        var result = await _h.CreateService().SaveDispatchDraftAsync(
            SubconTestHarness.GoodDispatch(item: "NS1"));
        Assert.False(result.Succeeded);
        Assert.Equal(SubconMessages.StockControlRequired, result.ErrorMessage);
    }

    [Fact]
    public async Task Planning_access_shows_production_capability_but_does_not_enable_save()
    {
        var withPlanning = await _h.CreateService().SearchAsync(new SubconListQuery { Take = 1 });
        Assert.True(withPlanning.CanUseProductionMode);

        _h.Deny((MenuCodes.PlanningWorkOrder, PermissionCodes.Access));
        var inventoryOnly = await _h.CreateService().SearchAsync(new SubconListQuery { Take = 1 });
        Assert.False(inventoryOnly.CanUseProductionMode);
    }
}
