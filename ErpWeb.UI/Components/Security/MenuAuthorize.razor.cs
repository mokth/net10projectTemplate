using ErpWeb.Core.Menus;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Security;

public partial class MenuAuthorize
{
    [Inject]
    private IAccessRightService AccessRights { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Parameter, EditorRequired]
    public string MenuCode { get; set; } = string.Empty;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    private bool _authorized;
    private string? _checkedMenuCode;

    /// <summary>
    /// Re-evaluated whenever the menu code CHANGES, not only when the component is created: a page that
    /// serves two families (the purchase and sales credit/debit-note pairs) keeps the same component
    /// instance when the router reuses it, so an OnInitialized-only check would keep the previous
    /// family's decision — including granting the new family's screen on the strength of the right the
    /// operator held for the family they left.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (string.Equals(_checkedMenuCode, MenuCode, StringComparison.Ordinal))
        {
            return;
        }

        _checkedMenuCode = MenuCode;
        _authorized = false;

        if (string.IsNullOrWhiteSpace(MenuCode))
        {
            Navigation.NavigateTo("/unauthorized");
            return;
        }

        if (await AccessRights.CanAccessAsync(MenuCode))
        {
            _authorized = true;
            return;
        }

        Navigation.NavigateTo("/unauthorized");
    }
}
