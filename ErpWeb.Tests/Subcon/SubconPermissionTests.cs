using ErpWeb.Core.Menus;
using ErpWeb.Core.Subcon;
using Moq;

namespace ErpWeb.Tests.Subcon;

[Trait(TestCategories.Name, TestCategories.Subcon)]
public class SubconPermissionTests : IAsyncLifetime
{
    private readonly SubconTestHarness _h = new();

    public async Task InitializeAsync() => await _h.SeedMastersAsync();

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Access_is_denied_without_permission()
    {
        _h.Access.Reset();
        _h.Access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var result = await _h.CreateService().SearchAsync(new SubconListQuery());
        Assert.False(result.Succeeded);
        Assert.Equal(SubconMessages.NotAuthorized, result.ErrorMessage);
    }

    [Fact]
    public async Task Add_is_required_to_create_a_send_out_draft()
    {
        _h.Deny(
            (MenuCodes.SubconSendOut, PermissionCodes.Add),
            (MenuCodes.SubconWorkbench, PermissionCodes.Add));
        var result = await _h.CreateService().SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        Assert.False(result.Succeeded);
        Assert.Equal(SubconMessages.NotAuthorized, result.ErrorMessage);
    }

    [Fact]
    public async Task Edit_is_required_to_update_a_send_out_draft()
    {
        var svc = _h.CreateService();
        var saved = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        _h.Deny(
            (MenuCodes.SubconSendOut, PermissionCodes.Edit),
            (MenuCodes.SubconWorkbench, PermissionCodes.Edit));
        var updated = await _h.CreateService().SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            DocNo = saved.Dispatch!.DocNo,
            TrxDate = DateTime.Today,
            Mode = SubconModes.Inventory,
            SupplierCode = "SUP01",
            RowVersion = saved.Dispatch.RowVersion,
            Lines = saved.Dispatch.Lines.Select(x => new SubconDispatchLineSaveCommand
            {
                LineNo = x.LineNo,
                ItemCode = x.ItemCode,
                Qty = x.Qty,
                WarehouseCode = x.WarehouseCode,
                LocationCode = x.LocationCode,
                Uom = x.Uom
            }).ToArray()
        });
        Assert.False(updated.Succeeded);
        Assert.Equal(SubconMessages.NotAuthorized, updated.ErrorMessage);
    }

    [Fact]
    public async Task Inventory_only_user_does_not_need_planning_rights()
    {
        _h.Deny((MenuCodes.PlanningWorkOrder, PermissionCodes.Access));
        var svc = _h.CreateService();
        var search = await svc.SearchAsync(new SubconListQuery { Take = 1 });
        Assert.True(search.Succeeded, search.ErrorMessage);
        Assert.False(search.CanUseProductionMode);

        var saved = await svc.SaveDispatchDraftAsync(SubconTestHarness.GoodDispatch());
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.False(saved.CanUseProductionMode);
        Assert.Equal(SubconModes.Inventory, saved.Dispatch!.Mode);
    }

    [Fact]
    public async Task Dtos_have_no_money_or_cost_fields()
    {
        var money = new[] { "cost", "price", "amount", "value", "money" };
        foreach (var type in new[]
                 {
                     typeof(SubconDispatchDto),
                     typeof(SubconDispatchLineDto),
                     typeof(SubconReceiptDto),
                     typeof(SubconReceiptLineDto),
                     typeof(SubconListRow),
                     typeof(SubconWorkbenchPage)
                 })
        {
            var hits = type.GetProperties()
                .Where(p => money.Any(token => p.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .Select(p => $"{type.Name}.{p.Name}")
                .ToArray();
            Assert.True(hits.Length == 0, "ViewCost has nothing to hide because money leaked onto: " + string.Join(", ", hits));
        }
    }
}
