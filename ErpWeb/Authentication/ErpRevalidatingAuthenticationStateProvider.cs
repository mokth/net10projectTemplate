using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Options;

namespace ErpWeb.Authentication;

/// <summary>
/// Blazor Interactive Server authentication state provider that periodically revalidates the signed-in
/// principal against the live database (shared validator) and refreshes the scoped permission cache on
/// every successful validation, so role-menu permission changes take effect without a full page reload.
/// </summary>
public sealed class ErpRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IAuthSecurityStateValidator _validator;
    private readonly IAccessRightService _accessRights;
    private readonly TimeSpan _interval;

    public ErpRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IAuthSecurityStateValidator validator,
        IAccessRightService accessRights,
        IOptions<SecurityRevalidationOptions> options)
        : base(loggerFactory)
    {
        _validator = validator;
        _accessRights = accessRights;
        _interval = options.Value.RevalidationInterval;
    }

    protected override TimeSpan RevalidationInterval => _interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (!await _validator.IsValidAsync(user, cancellationToken))
        {
            return false;
        }

        // Still valid: drop the cached effective permissions so the next authorization check reads
        // the current DB grants (see AccessRightService's scoped _cache).
        await _accessRights.RefreshPermissionsAsync(cancellationToken);
        return true;
    }
}
