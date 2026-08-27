using ErpWeb.Core.Services;
using ErpWeb.UI.Services;
using ErpWeb.UI.Services.Theme;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;

namespace ErpWeb.UI.Components.Layout;

public partial class MainLayout : IDisposable
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject]
    private ThemeService Themes { get; set; } = default!;

    [Inject]
    private ICurrentUserService CurrentUser { get; set; } = default!;

    [Inject]
    private PageNavigationGuard Guard { get; set; } = default!;

    private bool _sidebarOpen = true;
    private string? _leaveBlockedToast;

    private string BootstrapMode => AppThemes.BootstrapColorMode(Themes.ActiveThemeName);

    private string NavShellClass => _sidebarOpen ? "nav-open" : "nav-collapsed";

    private string LocalPath => new Uri(NavigationManager.Uri).LocalPath;

    private void CloseNav() => _sidebarOpen = false;

    protected override void OnInitialized()
    {
        Guard.Changed += OnGuardChanged;
    }

    private void OnGuardChanged()
    {
        if (!Guard.IsBlocking)
        {
            _leaveBlockedToast = null;
        }

        _ = InvokeAsync(StateHasChanged);
    }

    private void OnBeforeInternalNav(LocationChangingContext context)
    {
        if (!Guard.IsBlocking)
        {
            return;
        }

        context.PreventNavigation();
        _leaveBlockedToast = string.IsNullOrWhiteSpace(Guard.Message)
            ? "Please wait. This action is still running."
            : Guard.Message;
        _ = InvokeAsync(StateHasChanged);
    }

    private void DismissLeaveBlockedToast() => _leaveBlockedToast = null;

    public void Dispose()
    {
        Guard.Changed -= OnGuardChanged;
    }

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
