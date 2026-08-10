using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Services.Theme;

public class CookiesService
{
    private readonly IJSRuntime _jsRuntime;

    public CookiesService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public string? GetCookie(IHttpContextAccessor httpContextAccessor, string key) =>
        httpContextAccessor.HttpContext?.Request.Cookies[key];

    public async Task SetCookieAsync(string key, string value) =>
        await _jsRuntime.InvokeVoidAsync("erpSetCookie", key, value);
}
