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
    private AppNavigation Navigation { get; set; } = default!;

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

    /// <summary>
    /// The current location RELATIVE to the app base path ("sales/invoices"). The absolute path
    /// carries the deployment prefix ("\erpweb\sales\invoices"), so comparing it against a
    /// root-relative route never matches when the app is hosted under a sub-path.
    /// </summary>
    private string LocalPath => Navigation.RelativePath;

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
            !LocalPath.StartsWith("change-password", StringComparison.OrdinalIgnoreCase) &&
            !LocalPath.StartsWith("account/logout", StringComparison.OrdinalIgnoreCase))
        {
            Navigation.NavigateTo("/change-password", forceLoad: true);
        }
    }
}
