using ErpWeb.Core.Security;
using ErpWeb.Model.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ErpWeb.Authentication;

public interface ICookieSignInService
{
    Task SignInAsync(UserLogin user, bool rememberMe = false, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public class CookieSignInService : ICookieSignInService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan PersistentLifetime = TimeSpan.FromDays(30);

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ErpWeb.Core.Services.IAuthService _authService;

    public CookieSignInService(IHttpContextAccessor httpContextAccessor, ErpWeb.Core.Services.IAuthService authService)
    {
        _httpContextAccessor = httpContextAccessor;
        _authService = authService;
    }

    public async Task SignInAsync(UserLogin user, bool rememberMe = false, CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext is required to sign in.");

        var principal = _authService.CreatePrincipal(user);

        // On reissue (self password change) preserve the current authentication properties so
        // Remember-Me persistence and the existing ExpiresUtc survive; only mint fresh properties
        // for a brand-new sign-in.
        var existing = await httpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        AuthenticationProperties properties;
        if (existing.Succeeded && existing.Properties is not null)
        {
            properties = existing.Properties;
            properties.AllowRefresh = true;
        }
        else
        {
            var lifetime = rememberMe ? PersistentLifetime : SessionLifetime;
            properties = new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(lifetime),
                AllowRefresh = true
            };
        }

        // Reset the security revalidation timestamp: the freshly-issued principal is known-good now,
        // so cookie revalidation will skip the DB check for the configured interval.
        properties.Items[SecurityRevalidationConstants.ValidatedUtcKey] = DateTimeOffset.UtcNow.ToString("O");

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            properties);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("HttpContext is required to sign out.");

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
