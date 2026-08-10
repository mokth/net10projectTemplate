using ErpWeb.Core.Authentication;
using Microsoft.AspNetCore.Http;

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
    string? SubjectUid { get; }
    bool IsInRole(string role);
}

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private System.Security.Claims.ClaimsPrincipal? User =>
        _httpContextAccessor.HttpContext?.User;

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

    public bool IsInRole(string role) =>
        User?.IsInRole(role) == true;

    private string? Find(string type) =>
        User?.FindFirst(type)?.Value;
}
