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

    protected override async Task OnInitializedAsync()
    {
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
