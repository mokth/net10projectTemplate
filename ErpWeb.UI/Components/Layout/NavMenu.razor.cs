using ErpWeb.Core.Menus;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Layout;

public partial class NavMenu
{
    [Inject]
    private INavigationService NavigationService { get; set; } = default!;

    private IReadOnlyList<MenuNavItem>? _items;

    protected override async Task OnInitializedAsync()
    {
        _items = await NavigationService.GetSidebarAsync();
    }
}
