using System.Security.Claims;
using ErpWeb.Core.Authentication;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Library.Security;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Services;

public sealed class AuthOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public UserLogin? User { get; init; }
    public bool MustChangePassword { get; init; }

    public static AuthOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };

    public static AuthOperationResult Ok(UserLogin user) =>
        new()
        {
            Succeeded = true,
            User = user,
            MustChangePassword = user.changepass
        };
}

public interface IAuthService
{
    Task<AuthOperationResult> ValidateCredentialsAsync(string companyCode, string username, string password, CancellationToken cancellationToken = default);
    ClaimsPrincipal CreatePrincipal(UserLogin user);
    Task<AuthOperationResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}

public class AuthService : IAuthService
{
    public const string GenericLoginFailure = "Invalid company code, username, or password.";

    private readonly IUserLoginRepository _users;
    private readonly ICurrentUserService _currentUser;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly IUserRoleSyncService _userRoleSync;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserLoginRepository users,
        ICurrentUserService currentUser,
        IPasswordPolicy passwordPolicy,
        IUserRoleSyncService userRoleSync,
        ILogger<AuthService> logger)
    {
        _users = users;
        _currentUser = currentUser;
        _passwordPolicy = passwordPolicy;
        _userRoleSync = userRoleSync;
        _logger = logger;
    }

    public async Task<AuthOperationResult> ValidateCredentialsAsync(
        string companyCode,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode) ||
            string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        var user = await _users.FindByLoginAsync(companyCode, username, cancellationToken);
        if (user is null)
        {
            _logger.LogWarning("Login failed for unknown identity under company {CompanyCode}", companyCode);
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        if (user.active != true)
        {
            _logger.LogWarning("Login rejected for inactive user {LoginId} company {CompanyCode}", username, companyCode);
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        if (!PasswordHasher.Verify(password, user.password))
        {
            _logger.LogWarning("Login failed due to invalid password for {LoginId} company {CompanyCode}", username, companyCode);
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        await _userRoleSync.ReconcileFromUserLevelAsync(
            user.uid,
            user.CompanyCode,
            user.userlevel,
            cancellationToken);

        return AuthOperationResult.Ok(user);
    }

    public ClaimsPrincipal CreatePrincipal(UserLogin user)
    {
        var claims = new List<Claim>
        {
            new(AppClaimTypes.Subject, user.uid.ToString()),
            new(AppClaimTypes.Name, user.name),
            new(AppClaimTypes.LoginId, user.id),
            new(AppClaimTypes.UserId, user.UserID ?? user.id),
            new(AppClaimTypes.CompanyCode, user.CompanyCode),
            new(AppClaimTypes.BranchCode, user.BranchCode),
            new(AppClaimTypes.LocationCode, user.LocationCode),
            new(AppClaimTypes.Level, user.userlevel ?? string.Empty),
            new(AppClaimTypes.ChangePassword, user.changepass ? "true" : "false"),
            new(ClaimTypes.Name, user.name),
            new(ClaimTypes.NameIdentifier, user.uid.ToString())
        };

        if (!string.IsNullOrWhiteSpace(user.userlevel))
        {
            claims.Add(new Claim(ClaimTypes.Role, user.userlevel));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public async Task<AuthOperationResult> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(_currentUser.SubjectUid))
        {
            return AuthOperationResult.Fail("You must be signed in to change your password.");
        }

        if (string.IsNullOrWhiteSpace(_currentUser.LoginId))
        {
            _logger.LogWarning("Password change rejected due to missing LoginId for subject {SubjectUid}", _currentUser.SubjectUid);
            return AuthOperationResult.Fail("Invalid user identity.");
        }

        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            return AuthOperationResult.Fail("Current and new passwords are required.");
        }

        var policyResult = _passwordPolicy.Validate(newPassword, _currentUser.LoginId);
        if (!policyResult.IsValid)
        {
            return AuthOperationResult.Fail(string.Join(" ", policyResult.Errors));
        }

        if (!int.TryParse(_currentUser.SubjectUid, out var uid))
        {
            return AuthOperationResult.Fail("Invalid user identity.");
        }

        var existing = await _users.GetByUidAsync(uid, cancellationToken);
        if (existing is null)
        {
            return AuthOperationResult.Fail("User not found.");
        }

        if (!PasswordHasher.Verify(currentPassword, existing.password))
        {
            return AuthOperationResult.Fail("Current password is incorrect.");
        }

        var updated = await _users.UpdatePasswordAsync(uid, PasswordHasher.Hash(newPassword), _currentUser.UserId, cancellationToken);
        if (updated is null)
        {
            return AuthOperationResult.Fail("User not found.");
        }

        _logger.LogInformation("Password changed for user {UserId} company {CompanyCode}", updated.UserID, updated.CompanyCode);
        return AuthOperationResult.Ok(updated);
    }
}
