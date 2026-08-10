using ErpWeb.Core.Services;
using ErpWeb.UI.Services.Theme;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace ErpWeb.UI.Components.Layout;

public partial class MainLayout
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject]
    private ThemeService Themes { get; set; } = default!;

    [Inject]
    private ICurrentUserService CurrentUser { get; set; } = default!;

    private bool _sidebarOpen = true;

    private string BootstrapMode => AppThemes.BootstrapColorMode(Themes.ActiveThemeName);

    private string NavShellClass => _sidebarOpen ? "nav-open" : "nav-collapsed";

    private string LocalPath => new Uri(NavigationManager.Uri).LocalPath;

    private void CloseNav() => _sidebarOpen = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        var state = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated == true &&
            CurrentUser.MustChangePassword &&
            !LocalPath.StartsWith("/change-password", StringComparison.OrdinalIgnoreCase) &&
            !LocalPath.StartsWith("/account/logout", StringComparison.OrdinalIgnoreCase))
        {
            NavigationManager.NavigateTo("/change-password", forceLoad: true);
        }
    }
}
