using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace ErpWeb.UI.Services;

/// <summary>
/// The single navigation seam for pages: it wraps <see cref="NavigationManager"/> and prefixes the
/// deployment's app base path onto root-absolute app URLs, so a page that navigates to
/// <c>"/sales/invoices"</c> works both when the app is hosted at the site root and when it is hosted
/// below it (an IIS sub-application, for example <c>https://host/erpweb</c>).
///
/// <para>
/// Why this exists: a URL that starts with <c>/</c> is resolved against the ORIGIN, not against the
/// app's base path, so <c>NavigationManager.NavigateTo("/home")</c> silently leaves the app whenever
/// the app lives at <c>/erpweb</c> — the browser is sent to <c>https://host/home</c>, which the
/// sub-application never sees. Microsoft's guidance is to write every link either relative or
/// prefixed; prefixing in one place keeps every existing call site correct and layout-independent.
/// See https://learn.microsoft.com/aspnet/core/blazor/host-and-deploy/app-base-path
/// </para>
///
/// <para>
/// The prefix comes from <see cref="NavigationManager.BaseUri"/>, which already reflects the rendered
/// <c>&lt;base href&gt;</c> (and therefore any request path base). This needs no configuration of its
/// own and is a pure pass-through when the app is hosted at the site root.
/// </para>
/// </summary>
public sealed class AppNavigation
{
    private readonly NavigationManager _inner;
    private readonly string _prefix;

    public AppNavigation(NavigationManager inner)
    {
        _inner = inner;

        // "/erpweb/" -> "/erpweb"; "/" -> "".
        _prefix = new Uri(inner.BaseUri).AbsolutePath.TrimEnd('/');
    }

    /// <summary>Absolute URI of the current location.</summary>
    public string Uri => _inner.Uri;

    /// <summary>Absolute base URI, including the app base path.</summary>
    public string BaseUri => _inner.BaseUri;

    /// <summary>The app base path (for example <c>"/erpweb"</c>); empty when hosted at the site root.</summary>
    public string BasePath => _prefix;

    /// <summary>The current location relative to the app base path, for example <c>"sales/invoices"</c>.</summary>
    public string RelativePath => _inner.ToBaseRelativePath(_inner.Uri).TrimStart('/');

    public event EventHandler<LocationChangedEventArgs>? LocationChanged
    {
        add => _inner.LocationChanged += value;
        remove => _inner.LocationChanged -= value;
    }

    public void NavigateTo(string uri, bool forceLoad = false, bool replace = false) =>
        _inner.NavigateTo(Resolve(uri), forceLoad, replace);

    public void NavigateTo(string uri, NavigationOptions options) =>
        _inner.NavigateTo(Resolve(uri), options);

    public Uri ToAbsoluteUri(string relativeUri) => _inner.ToAbsoluteUri(Resolve(relativeUri));

    public string ToBaseRelativePath(string uri) => _inner.ToBaseRelativePath(uri);

    /// <summary>
    /// Makes an app URL safe to hand to the browser: a URL that starts with <c>"/"</c> is prefixed with
    /// the app base path. Relative URLs, external URLs and URLs that already carry the prefix come back
    /// unchanged, so this is safe to call unconditionally.
    /// </summary>
    public string Resolve(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || uri[0] != '/')
        {
            // Relative ("sales/invoices") or absolute ("https://host/x"): the browser and
            // NavigationManager both resolve these correctly, so leave them alone.
            return uri ?? string.Empty;
        }

        if (uri.StartsWith("//", StringComparison.Ordinal))
        {
            // Protocol-relative -> external host.
            return uri;
        }

        if (_prefix.Length == 0
            || uri.Equals(_prefix, StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith(_prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return _prefix + uri;
    }
}
