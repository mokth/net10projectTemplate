using ErpWeb.Core.Services;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Layout.Header;

public partial class Header
{
    [Inject]
    private ICurrentUserService CurrentUser { get; set; } = default!;

    [Parameter]
    public bool SidebarOpen { get; set; }

    [Parameter]
    public EventCallback<bool> SidebarOpenChanged { get; set; }

    [Parameter]
    public string? CenterTitle { get; set; }

    [Parameter]
    public RenderFragment? CenterContent { get; set; }

    [Parameter]
    public RenderFragment? TrailingContent { get; set; }

    private string UserInitials
    {
        get
        {
            var name = CurrentUser.FullName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                var login = CurrentUser.LoginId?.Trim();
                if (string.IsNullOrEmpty(login))
                {
                    return "?";
                }

                return login[..Math.Min(2, login.Length)].ToUpperInvariant();
            }

            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
            }

            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
        }
    }

    private Task ToggleAsync() => SidebarOpenChanged.InvokeAsync(!SidebarOpen);
}
