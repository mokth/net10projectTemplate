using ErpWeb.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace ErpWeb.Authentication;

/// <summary>
/// Cookie revalidation events that re-check the signed-in principal against the live database on a
/// configured interval. Revalidation is skipped while the interval has not elapsed (the last check
/// timestamp lives in the ticket properties), and a now-invalid principal is rejected so a stale
/// cookie cannot permanently restore access after a security change.
/// </summary>
public sealed class CookieSecurityRevalidationEvents : CookieAuthenticationEvents
{
    private readonly IAuthSecurityStateValidator _validator;
    private readonly ILogger<CookieSecurityRevalidationEvents> _logger;
    private readonly TimeSpan _interval;

    public CookieSecurityRevalidationEvents(
        IAuthSecurityStateValidator validator,
        IOptions<SecurityRevalidationOptions> options,
        ILogger<CookieSecurityRevalidationEvents> logger)
    {
        _validator = validator;
        _logger = logger;
        _interval = options.Value.RevalidationInterval;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var lastValidated = SecurityRevalidationConstants.ReadValidatedUtc(context.Properties.Items);
        var now = DateTimeOffset.UtcNow;
        if (lastValidated is not null && now - lastValidated.Value < _interval)
        {
            return;   // within the revalidation interval — no DB round-trip
        }

        if (await _validator.IsValidAsync(context.Principal))
        {
            // Refresh the timestamp and renew the ticket only as required to persist it, without
            // changing the existing sliding-expiration / persistence semantics.
            context.Properties.Items[SecurityRevalidationConstants.ValidatedUtcKey] = now.ToString("O");
            context.ShouldRenew = true;
            return;
        }

        _logger.LogWarning("Cookie revalidation rejected a stale principal.");
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
