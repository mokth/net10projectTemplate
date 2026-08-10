using DevExpress.Blazor;

namespace ErpWeb.UI.Services.Theme;

public enum AppThemeName
{
    FluentLight,
    FluentDark,
    Classic,
    Bootstrap
}

public static class AppThemes
{
    public const string CookieKey = "ErpWebTheme";

    public static ITheme FluentLight => Themes.Fluent.Clone(props =>
    {
        props.Name = nameof(AppThemeName.FluentLight);
        props.Mode = ThemeMode.Light;
        props.AddFilePaths("css/site.css");
    });

    public static ITheme FluentDark => Themes.Fluent.Clone(props =>
    {
        props.Name = nameof(AppThemeName.FluentDark);
        props.Mode = ThemeMode.Dark;
        props.AddFilePaths("css/site.css");
    });

    public static ITheme Classic => Themes.OfficeWhite.Clone(props =>
    {
        props.Name = nameof(AppThemeName.Classic);
        props.AddFilePaths("css/site.css");
    });

    public static ITheme Bootstrap => Themes.BootstrapExternal.Clone(props =>
    {
        props.Name = nameof(AppThemeName.Bootstrap);
        props.AddFilePaths(
            "https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css",
            "css/site.css");
    });

    public static ITheme Resolve(string? themeName) =>
        Enum.TryParse<AppThemeName>(themeName, ignoreCase: true, out var name)
            ? Resolve(name)
            : FluentLight;

    public static ITheme Resolve(AppThemeName name) => name switch
    {
        AppThemeName.FluentDark => FluentDark,
        AppThemeName.Classic => Classic,
        AppThemeName.Bootstrap => Bootstrap,
        _ => FluentLight
    };

    public static string BootstrapColorMode(AppThemeName name) =>
        name == AppThemeName.FluentDark ? "dark" : "light";
}
