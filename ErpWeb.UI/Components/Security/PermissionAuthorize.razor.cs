using ErpWeb.Core.Menus;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Security;

public partial class PermissionAuthorize
{
    [Inject]
    private IAccessRightService AccessRights { get; set; } = default!;

    [Parameter, EditorRequired]
    public string MenuCode { get; set; } = string.Empty;

    [Parameter]
    public string Permission { get; set; } = PermissionCodes.Access;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    private bool _allowed;

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(MenuCode) || string.IsNullOrWhiteSpace(Permission))
        {
            _allowed = false;
            return;
        }

        _allowed = await AccessRights.CanAsync(MenuCode, Permission);
    }
}
