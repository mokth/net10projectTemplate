using System.Linq;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace ErpWeb.UI.Components.Layout;

public partial class NavMenu : IDisposable
{
    [Inject]
    private INavigationService NavigationService { get; set; } = default!;

    [Inject]
    private AppNavigation Navigation { get; set; } = default!;

    private IReadOnlyList<NavTreeItem>? _items;
    private NavigationUrlMatchMode _urlMatchMode = NavigationUrlMatchMode.Prefix;

    /// <summary>Relative on purpose: it resolves against <c>&lt;base href&gt;</c>, so it stays inside
    /// the deployment's base path.</summary>
    private const string ChangePasswordRoute = "change-password";

    protected override async Task OnInitializedAsync()
    {
        _items = MapItems(await NavigationService.GetSidebarAsync());
        RefreshUrlMatchMode();
        Navigation.LocationChanged += OnLocationChanged;
    }

    private async void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        RefreshUrlMatchMode();
        // Permission cache may have been refreshed by circuit revalidation since the menu was built;
        // rebuild the authorized sidebar on the next navigation so it reflects the current grants.
        _items = MapItems(await NavigationService.GetSidebarAsync());
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshUrlMatchMode()
    {
        // Inquiry-originated document views carry ?returnUrl=/purchase/inquiry/…
        // Prefix-matching the transaction route would expand Transactions and steal focus.
        _urlMatchMode = DocumentReturnNavigation.HasSafeInquiryReturn(Navigation.Uri)
            ? NavigationUrlMatchMode.None
            : NavigationUrlMatchMode.Prefix;
    }

    public void Dispose() => Navigation.LocationChanged -= OnLocationChanged;

    /// <summary>
    /// Prefixes every menu route with the deployment's app base path. The routes come from menus.xml /
    /// dbo.Menu as root-absolute app paths ("/inventory/items") and DevExpress renders them as plain
    /// hrefs, which the browser resolves against the ORIGIN - so the sidebar would leave a
    /// sub-application on every click. They are prefixed rather than made relative so DxTreeView's
    /// UrlMatchMode still matches them against the full NavigationManager.Uri.
    /// </summary>
    private IReadOnlyList<NavTreeItem> MapItems(IReadOnlyList<MenuNavItem> items) =>
        items.Select(MapItem).ToArray();

    private NavTreeItem MapItem(MenuNavItem item) => new()
    {
        Code = item.Code,
        Name = item.Name,
        Route = string.IsNullOrWhiteSpace(item.Route) ? item.Route : Navigation.Resolve(item.Route),
        Icon = item.Icon,
        Attributes = new Dictionary<string, object> { ["title"] = item.Name },
        Children = item.Children.Count == 0 ? Array.Empty<NavTreeItem>() : MapItems(item.Children),
    };

    private sealed record NavTreeItem
    {
        public required string Code { get; init; }
        public required string Name { get; init; }
        public string? Route { get; init; }
        public string? Icon { get; init; }
        public Dictionary<string, object> Attributes { get; init; } = new();
        public IReadOnlyList<NavTreeItem> Children { get; init; } = Array.Empty<NavTreeItem>();
    }
}
