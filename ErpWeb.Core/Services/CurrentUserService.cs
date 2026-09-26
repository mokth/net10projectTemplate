using System.Security.Claims;
using ErpWeb.Core.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ErpWeb.Core.Services;

public interface ICurrentUserService
{
    bool IsAuthenticated { get; }
    string? UserId { get; }
    string? LoginId { get; }
    string? FullName { get; }
    string? CompanyCode { get; }
    string? BranchCode { get; }
    string? LocationCode { get; }
    string? UserLevel { get; }
    bool MustChangePassword { get; }
    bool CanViewPrice { get; }
    string? SubjectUid { get; }
    bool IsInRole(string role);
}

/// <summary>
/// Resolves the signed-in principal for both HTTP endpoints and Blazor Interactive Server.
/// Under IIS sub-path hosting (<c>/erpweb</c>), <see cref="IHttpContextAccessor.HttpContext"/> can
/// be missing or incomplete on the circuit while <see cref="AuthenticationStateProvider"/> still
/// has the cookie principal — so we prefer an authenticated HttpContext user, then fall back to
/// auth state. <see cref="IsInRole"/> also treats <see cref="AppClaimTypes.Level"/> as a role so
/// ADMIN / SYSTEM_ADMIN bypass still works if the Role claim type is not mapped after cookie round-trip.
/// </summary>
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceProvider _services;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor, IServiceProvider services)
    {
        _httpContextAccessor = httpContextAccessor;
        _services = services;
    }

    private ClaimsPrincipal? User
    {
        get
        {
            var httpUser = _httpContextAccessor.HttpContext?.User;
            if (httpUser?.Identity?.IsAuthenticated == true)
            {
                return httpUser;
            }

            var authState = _services.GetService<AuthenticationStateProvider>();
            if (authState is null)
            {
                return httpUser;
            }

            // ServerAuthenticationStateProvider completes synchronously from the circuit/HTTP user.
            var state = authState.GetAuthenticationStateAsync().GetAwaiter().GetResult();
            return state.User.Identity?.IsAuthenticated == true ? state.User : httpUser;
        }
    }

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public string? SubjectUid => Find(AppClaimTypes.Subject);

    public string? UserId => Find(AppClaimTypes.UserId);

    public string? LoginId => Find(AppClaimTypes.LoginId);

    public string? FullName => Find(AppClaimTypes.Name);

    public string? CompanyCode => Find(AppClaimTypes.CompanyCode);

    public string? BranchCode => Find(AppClaimTypes.BranchCode);

    public string? LocationCode => Find(AppClaimTypes.LocationCode);

    public string? UserLevel => Find(AppClaimTypes.Level);

    public bool MustChangePassword =>
        string.Equals(Find(AppClaimTypes.ChangePassword), "true", StringComparison.OrdinalIgnoreCase);

    public bool CanViewPrice =>
        string.Equals(Find(AppClaimTypes.CanViewPrice), "true", StringComparison.OrdinalIgnoreCase);

    public bool IsInRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        if (User?.IsInRole(role) == true)
        {
            return true;
        }

        // Level claim is always written at login; Role claim can fail IsInRole after cookie
        // deserialize / PathBase circuit quirks. Match case-insensitively.
        return string.Equals(UserLevel, role, StringComparison.OrdinalIgnoreCase);
    }

    private string? Find(string type) =>
        User?.FindFirst(type)?.Value;
}
