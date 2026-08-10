using ErpWeb.UI.Services.Theme;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Layout.ThemeSwitcher;

public partial class ThemeSwitcher
{
    [Inject]
    private ThemeService ThemeService { get; set; } = default!;

    private string ShortName => ThemeService.ActiveThemeName switch
    {
        AppThemeName.FluentLight => "Fluent Light",
        AppThemeName.FluentDark => "Fluent Dark",
        AppThemeName.Classic => "Classic",
        AppThemeName.Bootstrap => "Bootstrap",
        _ => "Theme"
    };

    private async Task SetThemeAsync(AppThemeName theme)
    {
        await ThemeService.SetActiveThemeAsync(theme);
        await InvokeAsync(StateHasChanged);
    }
}
