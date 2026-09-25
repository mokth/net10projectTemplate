using System.Linq;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Layout;

public partial class NavMenu
{
    [Inject]
    private INavigationService NavigationService { get; set; } = default!;

    [Inject]
    private AppNavigation Navigation { get; set; } = default!;

    private IReadOnlyList<MenuNavItem>? _items;

    /// <summary>Relative on purpose: it resolves against <c>&lt;base href&gt;</c>, so it stays inside
    /// the deployment's base path.</summary>
    private const string ChangePasswordRoute = "change-password";

    protected override async Task OnInitializedAsync()
    {
        _items = PrefixRoutes(await NavigationService.GetSidebarAsync());
    }

    /// <summary>
    /// Prefixes every menu route with the deployment's app base path. The routes come from menus.xml /
    /// dbo.Menu as root-absolute app paths ("/inventory/items") and DevExpress renders them as plain
    /// hrefs, which the browser resolves against the ORIGIN - so the sidebar would leave a
    /// sub-application on every click. They are prefixed rather than made relative so DxTreeView's
    /// UrlMatchMode still matches them against the full NavigationManager.Uri.
    /// </summary>
    private IReadOnlyList<MenuNavItem> PrefixRoutes(IReadOnlyList<MenuNavItem> items) =>
        items.Select(PrefixRoutes).ToArray();

    private MenuNavItem PrefixRoutes(MenuNavItem item) => item with
    {
        Route = string.IsNullOrWhiteSpace(item.Route) ? item.Route : Navigation.Resolve(item.Route),
        Children = item.Children.Count == 0 ? item.Children : PrefixRoutes(item.Children),
    };
}