using System.Linq;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Components.Layout;

public partial class NavMenu : IDisposable
{
    [Inject]
    private INavigationService NavigationService { get; set; } = default!;

    [Inject]
    private AppNavigation Navigation { get; set; } = default!;

    private DxTreeView? _treeView;
    private IReadOnlyList<NavTreeItem>? _allItems;
    private IReadOnlyList<NavTreeItem> _visibleItems = Array.Empty<NavTreeItem>();
    private NavigationUrlMatchMode _urlMatchMode = NavigationUrlMatchMode.Prefix;
    private string _searchText = string.Empty;
    private bool _allExpanded;
    private bool _userPreferredAllExpanded;
    private bool _suppressExpandSync;
    private PendingTreeAction _pendingTreeAction;

    /// <summary>Relative on purpose: it resolves against <c>&lt;base href&gt;</c>, so it stays inside
    /// the deployment's base path.</summary>
    private const string ChangePasswordRoute = "change-password";

    private enum PendingTreeAction
    {
        None,
        SyncState,
        ExpandAll,
        RestoreAfterSearch
    }

    private bool CanToggleExpandCollapse =>
        _allItems is not null && NavMenuTreeFilter.HasExpandableNodes(_visibleItems);

    private string ExpandCollapseIconCss =>
        _allExpanded ? "fa-solid fa-angles-up" : "fa-solid fa-angles-down";

    private string ExpandCollapseLabel =>
        _allExpanded ? "Collapse all" : "Expand all";

    private string ExpandCollapseButtonCss =>
        _allExpanded ? "nav-expand-btn is-expanded" : "nav-expand-btn";

    protected override async Task OnInitializedAsync()
    {
        _allItems = MapItems(await NavigationService.GetSidebarAsync());
        _visibleItems = _allItems;
        _pendingTreeAction = PendingTreeAction.SyncState;
        RefreshUrlMatchMode();
        Navigation.LocationChanged += OnLocationChanged;
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingTreeAction == PendingTreeAction.None || _treeView is null)
        {
            return Task.CompletedTask;
        }

        var action = _pendingTreeAction;
        _pendingTreeAction = PendingTreeAction.None;

        var changed = false;
        _suppressExpandSync = true;
        try
        {
            switch (action)
            {
                case PendingTreeAction.ExpandAll:
                    _treeView.ExpandAll();
                    changed = UpdateAllExpandedState(syncUserPreference: false);
                    break;

                case PendingTreeAction.RestoreAfterSearch:
                    if (_userPreferredAllExpanded)
                    {
                        _treeView.ExpandAll();
                    }
                    else
                    {
                        _treeView.CollapseAll();
                        if (_urlMatchMode == NavigationUrlMatchMode.Prefix)
                        {
                            _treeView.ExpandToNode(IsNodeMatchingCurrentUrl);
                        }
                    }

                    changed = UpdateAllExpandedState(syncUserPreference: false);
                    break;

                case PendingTreeAction.SyncState:
                    changed = UpdateAllExpandedState(syncUserPreference: false);
                    break;
            }
        }
        finally
        {
            _suppressExpandSync = false;
        }

        if (changed)
        {
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    private async void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        RefreshUrlMatchMode();
        // Permission cache may have been refreshed by circuit revalidation since the menu was built;
        // rebuild the authorized sidebar on the next navigation so it reflects the current grants.
        var latest = MapItems(await NavigationService.GetSidebarAsync());
        if (!NavMenuTreeFilter.AreTreesEqual(_allItems, latest))
        {
            _allItems = latest;
            _visibleItems = NavMenuTreeFilter.Filter(_allItems, _searchText);
            _pendingTreeAction = !string.IsNullOrWhiteSpace(_searchText) && _visibleItems.Count > 0
                ? PendingTreeAction.ExpandAll
                : PendingTreeAction.SyncState;
        }
        else if (string.IsNullOrWhiteSpace(_searchText))
        {
            _pendingTreeAction = PendingTreeAction.SyncState;
        }

        _ = InvokeAsync(StateHasChanged);
    }

    private void OnSearchTextChanged(string? value)
    {
        if (_allItems is null)
        {
            _searchText = value ?? string.Empty;
            return;
        }

        var wasFiltering = !string.IsNullOrWhiteSpace(_searchText);
        _searchText = value ?? string.Empty;
        var isFiltering = !string.IsNullOrWhiteSpace(_searchText);

        _visibleItems = NavMenuTreeFilter.Filter(_allItems, _searchText);

        if (isFiltering)
        {
            _pendingTreeAction = _visibleItems.Count > 0
                ? PendingTreeAction.ExpandAll
                : PendingTreeAction.None;
            if (_visibleItems.Count == 0)
            {
                _allExpanded = false;
            }
        }
        else if (wasFiltering)
        {
            _pendingTreeAction = PendingTreeAction.RestoreAfterSearch;
        }
    }

    private Task OnSearchKeyDown(KeyboardEventArgs args)
    {
        if (string.Equals(args.Key, "Escape", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(_searchText))
        {
            ClearSearch();
        }

        return Task.CompletedTask;
    }

    private void ClearSearch() => OnSearchTextChanged(string.Empty);

    private void ToggleExpandCollapseAll()
    {
        if (_treeView is null || !CanToggleExpandCollapse)
        {
            return;
        }

        _suppressExpandSync = true;
        try
        {
            if (_allExpanded)
            {
                _treeView.CollapseAll();
                _allExpanded = false;
            }
            else
            {
                _treeView.ExpandAll();
                _allExpanded = true;
            }

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                _userPreferredAllExpanded = _allExpanded;
            }
        }
        finally
        {
            _suppressExpandSync = false;
        }
    }

    private void OnTreeExpansionChanged(TreeViewNodeEventArgs _)
    {
        if (_suppressExpandSync)
        {
            return;
        }

        UpdateAllExpandedState(syncUserPreference: string.IsNullOrWhiteSpace(_searchText));
    }

    private bool UpdateAllExpandedState(bool syncUserPreference)
    {
        if (_treeView is null || !CanToggleExpandCollapse)
        {
            var changedEmpty = _allExpanded;
            _allExpanded = false;
            return changedEmpty;
        }

        var anyExpandable = false;
        var allOpen = true;
        foreach (var node in _treeView.GetNodesInfo(node => !node.IsLeaf))
        {
            anyExpandable = true;
            if (!node.Expanded)
            {
                allOpen = false;
                break;
            }
        }

        var next = anyExpandable && allOpen;
        var changed = next != _allExpanded;
        _allExpanded = next;

        if (syncUserPreference)
        {
            _userPreferredAllExpanded = next;
        }

        return changed;
    }

    private bool IsNodeMatchingCurrentUrl(ITreeViewNodeInfo node)
    {
        if (string.IsNullOrWhiteSpace(node.NavigateUrl))
        {
            return false;
        }

        var currentPath = new Uri(Navigation.Uri).AbsolutePath.TrimEnd('/');
        var nodePath = node.NavigateUrl.TrimEnd('/');
        if (string.IsNullOrEmpty(nodePath))
        {
            return string.IsNullOrEmpty(currentPath);
        }

        return string.Equals(currentPath, nodePath, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase);
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
}
