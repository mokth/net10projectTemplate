using System.Security.Claims;
using ErpWeb.Core.Authentication;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
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
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUser;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly IUserRoleSyncService _userRoleSync;
    private readonly ISecurityAuditWriter _audit;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserLoginRepository users,
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUser,
        IPasswordPolicy passwordPolicy,
        IUserRoleSyncService userRoleSync,
        ISecurityAuditWriter audit,
        ILogger<AuthService> logger)
    {
        _users = users;
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _passwordPolicy = passwordPolicy;
        _userRoleSync = userRoleSync;
        _audit = audit;
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

        companyCode = companyCode.Trim().ToUpperInvariant();
        username = username.Trim();

        if (!await IsCompanyActiveAsync(companyCode, cancellationToken))
        {
            _logger.LogWarning("Login rejected for inactive or missing company {CompanyCode}", companyCode);
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

        var sync = await _userRoleSync.ReconcileFromUserLevelAsync(
            user.uid,
            user.CompanyCode,
            user.userlevel,
            cancellationToken);

        if (!sync.Succeeded)
        {
            _logger.LogWarning(
                "Login rejected for {LoginId} company {CompanyCode}: role reconciliation failed ({Status}: {Detail})",
                username,
                companyCode,
                sync.Status,
                sync.Detail);
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        // Final live-state recheck: the password verification and role sync above may take long
        // enough that the row changed underneath us (reset / deactivate / role change). Claims must
        // be minted from a FINAL live read, and any drift fails generically so no stale cookie is issued.
        var final = await _users.GetActiveByUidAsync(user.uid, user.CompanyCode, cancellationToken);
        if (final is null)
        {
            _logger.LogWarning(
                "Login rejected for {LoginId} company {CompanyCode}: user became inactive/deleted during login",
                username,
                companyCode);
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        if (!string.Equals(final.password, user.password, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Login rejected for {LoginId} company {CompanyCode}: password changed during login",
                username,
                companyCode);
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        if (!string.Equals(final.userlevel, user.userlevel, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Login rejected for {LoginId} company {CompanyCode}: role changed during login",
                username,
                companyCode);
            return AuthOperationResult.Fail(GenericLoginFailure);
        }

        return AuthOperationResult.Ok(final);
    }

    private async Task<bool> IsCompanyActiveAsync(string companyCode, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = await db.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyCode == companyCode, cancellationToken);

        // Legacy installs may have users before a Company row exists — allow login only when
        // the company row is missing; block when it exists and is inactive.
        return company is null || company.IsActive;
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
            new(AppClaimTypes.CanViewPrice, user.CanViewPrice ? "true" : "false"),
            new(ClaimTypes.Name, user.name),
            new(ClaimTypes.NameIdentifier, user.uid.ToString())
        };

        if (!string.IsNullOrWhiteSpace(user.userlevel))
        {
            claims.Add(new Claim(ClaimTypes.Role, user.userlevel.Trim()));
        }

        // Explicit name/role claim types so IsInRole("SYSTEM_ADMIN") survives cookie serialize
        // under IIS sub-path (/erpweb) hosting.
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
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

        if (string.IsNullOrWhiteSpace(_currentUser.CompanyCode))
        {
            _logger.LogWarning("Password change rejected due to missing CompanyCode for subject {SubjectUid}", _currentUser.SubjectUid);
            return AuthOperationResult.Fail("Invalid user identity.");
        }

        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            return AuthOperationResult.Fail("Current and new passwords are required.");
        }

        if (!int.TryParse(_currentUser.SubjectUid, out var uid))
        {
            return AuthOperationResult.Fail("Invalid user identity.");
        }

        var policyResult = _passwordPolicy.Validate(newPassword, _currentUser.LoginId);
        if (!policyResult.IsValid)
        {
            return AuthOperationResult.Fail(string.Join(" ", policyResult.Errors));
        }

        var companyCode = _currentUser.CompanyCode.Trim();

        // Company-scoped live target: exists, belongs to the authenticated company, active, not deleted.
        var target = await _users.GetActiveByUidAsync(uid, companyCode, cancellationToken);
        if (target is null)
        {
            _logger.LogWarning(
                "Self password change rejected for subject {SubjectUid} company {CompanyCode}: target not found, inactive, or deleted",
                _currentUser.SubjectUid,
                companyCode);
            return AuthOperationResult.Fail("Unable to change password.");
        }

        if (!PasswordHasher.Verify(currentPassword, target.password))
        {
            return AuthOperationResult.Fail("Current password is incorrect.");
        }

        if (PasswordHasher.Verify(newPassword, target.password))
        {
            return AuthOperationResult.Fail("New password must be different from the current password.");
        }

        var newHash = PasswordHasher.Hash(newPassword);

        // Compare-and-set: guard on the exact hash + changepass state that was verified, so a
        // concurrent reset/force-change cannot be silently overwritten.
        var changed = await _users.TryChangeOwnPasswordAsync(
            uid,
            companyCode,
            target.password,
            target.changepass,
            newHash,
            _currentUser.UserId,
            cancellationToken);

        if (!changed)
        {
            _logger.LogWarning(
                "Self password change conflicted for subject {SubjectUid} company {CompanyCode}",
                _currentUser.SubjectUid,
                companyCode);
            return AuthOperationResult.Fail("Your password was changed by another session. Please try again.");
        }

        var updated = await _users.GetActiveByUidAsync(uid, companyCode, cancellationToken);
        if (updated is null)
        {
            // CAS succeeded, so the row exists; fall back to the target with the new state.
            updated = target;
            updated.password = newHash;
            updated.changepass = false;
        }

        _logger.LogInformation("Password changed for user {UserId} company {CompanyCode}", updated.UserID, updated.CompanyCode);
        await _audit.WriteAsync(new SecurityAuditEvent
        {
            CompanyCode = updated.CompanyCode,
            ActorUid = int.TryParse(_currentUser.SubjectUid, out var actorUid) ? actorUid : null,
            ActorUserId = _currentUser.UserId,
            TargetUid = updated.uid,
            EventType = SecurityAuditEventTypes.PasswordChangeSelf,
            Outcome = "SUCCESS",
            Details = "Self-service password change."
        }, cancellationToken);
        return AuthOperationResult.Ok(updated);
    }
}
