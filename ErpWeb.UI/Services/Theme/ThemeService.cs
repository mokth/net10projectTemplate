using DevExpress.Blazor;
using Microsoft.AspNetCore.Http;

namespace ErpWeb.UI.Services.Theme;

public class ThemeService
{
    private readonly CookiesService _cookiesService;
    private readonly IThemeChangeService _themeChangeService;

    public ThemeService(
        CookiesService cookiesService,
        IThemeChangeService themeChangeService,
        IHttpContextAccessor httpContextAccessor)
    {
        _cookiesService = cookiesService;
        _themeChangeService = themeChangeService;
        ActiveThemeName = ReadThemeName(httpContextAccessor);
        ActiveTheme = AppThemes.Resolve(ActiveThemeName);
    }

    public AppThemeName ActiveThemeName { get; private set; }

    public ITheme ActiveTheme { get; private set; }

    public AppThemeName ReadThemeName(IHttpContextAccessor httpContextAccessor)
    {
        var cookie = _cookiesService.GetCookie(httpContextAccessor, AppThemes.CookieKey);
        return Enum.TryParse<AppThemeName>(cookie, ignoreCase: true, out var name)
            ? name
            : AppThemeName.FluentLight;
    }

    public async Task SetActiveThemeAsync(AppThemeName themeName)
    {
        ActiveThemeName = themeName;
        ActiveTheme = AppThemes.Resolve(themeName);
        await _cookiesService.SetCookieAsync(AppThemes.CookieKey, themeName.ToString());
        await _themeChangeService.SetTheme(ActiveTheme);
    }
}
