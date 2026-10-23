using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

public sealed class UserAdminService : IUserAdminService
{
    public const string AdminRole = "ADMIN";

    private readonly IUserLoginRepository _users;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly ISecurityAuditWriter _audit;
    private readonly IPasswordRecoveryService _recovery;
    private readonly ILogger<UserAdminService> _logger;

    public UserAdminService(
        IUserLoginRepository users,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        IPasswordPolicy passwordPolicy,
        ISecurityAuditWriter audit,
        IPasswordRecoveryService recovery,
        ILogger<UserAdminService> logger)
    {
        _users = users;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _passwordPolicy = passwordPolicy;
        _audit = audit;
        _recovery = recovery;
        _logger = logger;
    }

    public async Task<UserAdminOperationResult> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return UserAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Access, cancellationToken))
        {
            return UserAdminOperationResult.Fail("Not authorized.");
        }

        var users = await _users.ListForAdminAsync(context.CompanyCode!, cancellationToken);
        return UserAdminOperationResult.Ok(users);
    }

    public async Task<UserAdminOperationResult> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return UserAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Access, cancellationToken))
        {
            return UserAdminOperationResult.Fail("Not authorized.");
        }

        var roles = await _users.ListActiveRolesAsync(context.CompanyCode!, cancellationToken);

        // Normal ADMIN must not be offered SYSTEM_ADMIN as an assignable role.
        if (!_currentUser.IsInRole(CompanyService.SystemAdminRole))
        {
            roles = roles
                .Where(r => !string.Equals(r.RoleCode, CompanyService.SystemAdminRole, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return UserAdminOperationResult.OkRoles(roles);
    }

    public async Task<UserAdminOperationResult> AddUserAsync(
        CreateUserRequest user,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return UserAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Add, cancellationToken))
        {
            return UserAdminOperationResult.Fail("Not authorized.");
        }

        var loginId = (user.LoginId ?? string.Empty).Trim();
        var name = (user.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(loginId))
        {
            return UserAdminOperationResult.Fail("User ID is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return UserAdminOperationResult.Fail("User name is required.");
        }

        if (!string.Equals(user.TemporaryPassword, user.ConfirmPassword, StringComparison.Ordinal))
        {
            return UserAdminOperationResult.Fail("Password Not Match");
        }

        var policyResult = _passwordPolicy.Validate(user.TemporaryPassword, loginId);
        if (!policyResult.IsValid)
        {
            return UserAdminOperationResult.Fail(string.Join(" ", policyResult.Errors));
        }

        if (await _users.LoginExistsAsync(context.CompanyCode!, loginId, cancellationToken: cancellationToken))
        {
            return UserAdminOperationResult.Fail("User ID already exists.");
        }

        var roleCheck = await CheckRequestedRoleAsync(context.CompanyCode!, user.RoleCode, cancellationToken);
        if (roleCheck.Error is not null)
        {
            return UserAdminOperationResult.Fail(roleCheck.Error);
        }

        var entity = new UserLogin
        {
            id = loginId,
            name = name,
            password = PasswordHasher.Hash(user.TemporaryPassword),
            email = user.Email?.Trim(),
            mobileno = user.MobileNo?.Trim(),
            active = user.Active,
            userlevel = roleCheck.RoleCode,
            Created = DateTime.Now,
            UserID = Truncate(context.UserId!, 10),
            CompanyCode = context.CompanyCode!,
            BranchCode = _currentUser.BranchCode ?? string.Empty,
            LocationCode = _currentUser.LocationCode ?? string.Empty,
            changepass = true,
            CanViewPrice = user.CanViewPrice
        };

        if (string.IsNullOrWhiteSpace(entity.BranchCode) || string.IsNullOrWhiteSpace(entity.LocationCode))
        {
            return UserAdminOperationResult.Fail("Branch and location are required.");
        }

        await _users.AddAsync(entity, cancellationToken);
        await AuditAsync(SecurityAuditEventTypes.UserCreated, entity.uid, null, entity.id, "User account created.", cancellationToken);
        _logger.LogInformation(
            "User created. AdminUserId={AdminUserId} NewLoginId={LoginId} CompanyCode={CompanyCode}",
            context.UserId,
            entity.id,
            context.CompanyCode);

        return UserAdminOperationResult.Ok();
    }

    public async Task<UserAdminOperationResult> UpdateUserAsync(
        EditUserRequest user,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return UserAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit, cancellationToken))
        {
            return UserAdminOperationResult.Fail("Not authorized.");
        }

        if (user.Uid <= 0)
        {
            return UserAdminOperationResult.Fail("Invalid user.");
        }

        var name = (user.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return UserAdminOperationResult.Fail("User name is required.");
        }

        var target = await _users.GetAdminRowAsync(user.Uid, context.CompanyCode!, cancellationToken);
        if (target is null)
        {
            return UserAdminOperationResult.Fail("No Record Selected.");
        }

        // Privilege boundary: a normal ADMIN may not mutate a SYSTEM_ADMIN target.
        if (IsSystemAdminRole(target.RoleCode) && !_currentUser.IsInRole(CompanyService.SystemAdminRole))
        {
            return UserAdminOperationResult.Fail("You cannot modify a SYSTEM_ADMIN account.");
        }

        var roleCheck = await CheckRequestedRoleAsync(context.CompanyCode!, user.RoleCode, cancellationToken);
        if (roleCheck.Error is not null)
        {
            return UserAdminOperationResult.Fail(roleCheck.Error);
        }

        var previousActive = target.Active;
        var previousRole = target.RoleCode;

        // Self-protection: no self-deactivate and no self-role-change.
        if (int.TryParse(_currentUser.SubjectUid, out var selfUid) && user.Uid == selfUid)
        {
            if (!user.Active && target.Active == true)
            {
                return UserAdminOperationResult.Fail("You cannot deactivate your own account.");
            }

            var currentRole = (target.RoleCode ?? string.Empty).Trim().ToUpperInvariant();
            if (!string.Equals(roleCheck.RoleCode, currentRole, StringComparison.Ordinal))
            {
                return UserAdminOperationResult.Fail("You cannot change your own role.");
            }
        }

        user.Name = name;
        user.Email = user.Email?.Trim();
        user.MobileNo = user.MobileNo?.Trim();
        user.RoleCode = roleCheck.RoleCode;

        var updated = await _users.UpdateProfileAsync(
            user,
            context.CompanyCode!,
            Truncate(context.UserId!, 10),
            SystemAdminProtectRole(),
            cancellationToken);
        if (!updated)
        {
            return UserAdminOperationResult.Fail("Unable to update user.");
        }

        await AuditAsync(SecurityAuditEventTypes.UserProfileUpdated, user.Uid, null, user.LoginId, "User profile updated.", cancellationToken);
        if (previousActive != user.Active)
        {
            await AuditAsync(
                user.Active ? SecurityAuditEventTypes.UserActivated : SecurityAuditEventTypes.UserDeactivated,
                user.Uid, previousActive?.ToString(), user.Active.ToString(), null, cancellationToken);
        }
        if (!string.Equals(previousRole, user.RoleCode, StringComparison.OrdinalIgnoreCase))
        {
            await AuditAsync(SecurityAuditEventTypes.UserRoleChanged, user.Uid, previousRole, user.RoleCode, null, cancellationToken);
        }

        return UserAdminOperationResult.Ok();
    }

    public async Task<UserAdminOperationResult> DeleteUsersAsync(
        IReadOnlyCollection<int> uids,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return UserAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Delete, cancellationToken))
        {
            return UserAdminOperationResult.Fail("Not authorized.");
        }

        if (uids.Count == 0)
        {
            return UserAdminOperationResult.Fail("No record selected.");
        }

        if (int.TryParse(_currentUser.SubjectUid, out var selfUid) && uids.Contains(selfUid))
        {
            return UserAdminOperationResult.Fail("You cannot delete your own account.");
        }

        var deleted = await _users.SoftDeleteAsync(
            uids,
            context.CompanyCode!,
            Truncate(context.UserId!, 10),
            SystemAdminProtectRole(),
            cancellationToken);

        if (deleted == 0)
        {
            return UserAdminOperationResult.Fail("Unable to delete user(s).");
        }

        foreach (var uid in uids)
        {
            await AuditAsync(SecurityAuditEventTypes.UserSoftDeleted, uid, null, null, "User soft-deleted.", cancellationToken);
        }

        _logger.LogInformation(
            "Users soft-deleted. AdminUserId={AdminUserId} Count={Count} CompanyCode={CompanyCode}",
            context.UserId,
            deleted,
            context.CompanyCode);

        return UserAdminOperationResult.Ok();
    }

    public async Task<UserAdminOperationResult> ResetPasswordAsync(
        int uid,
        string newPassword,
        string confirmPassword,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return UserAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit, cancellationToken))
        {
            return UserAdminOperationResult.Fail("Not authorized.");
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return UserAdminOperationResult.Fail("Password Not Match");
        }

        var target = await _users.GetAdminSecurityAsync(uid, context.CompanyCode!, cancellationToken);
        if (target is null)
        {
            return UserAdminOperationResult.Fail("No Record Selected.");
        }

        if (int.TryParse(_currentUser.SubjectUid, out var selfUid) && uid == selfUid)
        {
            return UserAdminOperationResult.Fail("You cannot reset your own password.");
        }

        if (IsSystemAdminRole(target.RoleCode) && !_currentUser.IsInRole(CompanyService.SystemAdminRole))
        {
            return UserAdminOperationResult.Fail("You cannot reset a SYSTEM_ADMIN password.");
        }

        var policyResult = _passwordPolicy.Validate(newPassword, target.LoginId);
        if (!policyResult.IsValid)
        {
            return UserAdminOperationResult.Fail(string.Join(" ", policyResult.Errors));
        }

        var hash = PasswordHasher.Hash(newPassword);
        var result = await _users.TryResetPasswordAsync(
            uid,
            context.CompanyCode!,
            target.PasswordHash,
            target.ChangePass,
            hash,
            Truncate(context.UserId!, 10),
            SystemAdminProtectRole(),
            cancellationToken);

        var error = MapSecurityMutationResult(result, "Unable to change password.");
        if (error is not null)
        {
            return UserAdminOperationResult.Fail(error);
        }

        await AuditAsync(SecurityAuditEventTypes.PasswordResetAdmin, uid, null, target.LoginId, "Administrator reset the password.", cancellationToken);

        // Complete any pending password-recovery request for this user so the queue reflects the reset.
        var recovery = await _recovery.CompletePendingAsync(uid, context.CompanyCode!, Truncate(context.UserId!, 10), SelfUid() ?? 0, cancellationToken);
        if (recovery.Succeeded)
        {
            await AuditAsync(SecurityAuditEventTypes.PasswordRecoveryCompleted, uid, null, target.LoginId, "Recovery request completed by administrator reset.", cancellationToken);
        }

        _logger.LogInformation(
            "Password reset performed. AdminUserId={AdminUserId} TargetLoginId={TargetLoginId} TargetUid={TargetUid} CompanyCode={CompanyCode}",
            context.UserId,
            target.LoginId,
            uid,
            context.CompanyCode);

        return UserAdminOperationResult.Ok();
    }

    public async Task<UserAdminOperationResult> ForceChangeAsync(
        int uid,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return UserAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit, cancellationToken))
        {
            return UserAdminOperationResult.Fail("Not authorized.");
        }

        var target = await _users.GetAdminSecurityAsync(uid, context.CompanyCode!, cancellationToken);
        if (target is null)
        {
            return UserAdminOperationResult.Fail("No Record Selected.");
        }

        if (int.TryParse(_currentUser.SubjectUid, out var selfUid) && uid == selfUid)
        {
            return UserAdminOperationResult.Fail("You cannot change your own password-change requirement.");
        }

        if (IsSystemAdminRole(target.RoleCode) && !_currentUser.IsInRole(CompanyService.SystemAdminRole))
        {
            return UserAdminOperationResult.Fail("You cannot change a SYSTEM_ADMIN account.");
        }

        var result = await _users.TrySetChangePassAsync(
            uid,
            context.CompanyCode!,
            target.PasswordHash,
            target.ChangePass,
            force,
            Truncate(context.UserId!, 10),
            SystemAdminProtectRole(),
            cancellationToken);

        var error = MapSecurityMutationResult(result, "Unable to update password-change requirement.");
        if (error is not null)
        {
            return UserAdminOperationResult.Fail(error);
        }

        await AuditAsync(
            force ? SecurityAuditEventTypes.ForcePasswordChangeSet : SecurityAuditEventTypes.ForcePasswordChangeCleared,
            uid, (!force).ToString(), force.ToString(), target.LoginId, cancellationToken);

        _logger.LogInformation(
            "Force password change {Action}. AdminUserId={AdminUserId} TargetLoginId={TargetLoginId} TargetUid={TargetUid} CompanyCode={CompanyCode}",
            force ? "set" : "cleared",
            context.UserId,
            target.LoginId,
            uid,
            context.CompanyCode);

        return UserAdminOperationResult.Ok();
    }

    private static string? MapSecurityMutationResult(UserSecurityMutationResult result, string genericError)
    {
        if (result.Succeeded)
        {
            return null;
        }

        return result.Status switch
        {
            UserSecurityMutationStatus.NotFoundOrDeleted => "No Record Selected.",
            UserSecurityMutationStatus.ProtectedTarget => "You cannot modify a SYSTEM_ADMIN account.",
            UserSecurityMutationStatus.ConcurrencyConflict =>
                "The user's security state was changed by another operation. Please refresh and try again.",
            _ => genericError
        };
    }

    private int? SelfUid() =>
        int.TryParse(_currentUser.SubjectUid, out var uid) ? uid : null;

    private async Task AuditAsync(
        string eventType,
        int? targetUid,
        string? oldValue,
        string? newValue,
        string? details,
        CancellationToken cancellationToken)
    {
        await _audit.WriteAsync(new SecurityAuditEvent
        {
            CompanyCode = _currentUser.CompanyCode?.Trim(),
            ActorUid = SelfUid(),
            ActorUserId = _currentUser.UserId,
            TargetUid = targetUid,
            EventType = eventType,
            Outcome = "SUCCESS",
            OldValue = oldValue,
            NewValue = newValue,
            Details = details
        }, cancellationToken);
    }

    private async Task<RoleCheckResult> CheckRequestedRoleAsync(
        string companyCode,
        string? roleCode,
        CancellationToken cancellationToken)
    {
        var normalized = (roleCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return RoleCheckResult.Fail("Role is required.");
        }

        if (string.Equals(normalized, CompanyService.SystemAdminRole, StringComparison.OrdinalIgnoreCase) &&
            !_currentUser.IsInRole(CompanyService.SystemAdminRole))
        {
            return RoleCheckResult.Fail("You cannot assign the SYSTEM_ADMIN role.");
        }

        if (!await _users.IsActiveRoleAsync(companyCode, normalized, cancellationToken))
        {
            return RoleCheckResult.Fail("The selected role is invalid or inactive.");
        }

        return RoleCheckResult.Ok(normalized);
    }

    private string? SystemAdminProtectRole() =>
        _currentUser.IsInRole(CompanyService.SystemAdminRole) ? null : CompanyService.SystemAdminRole;

    private static bool IsSystemAdminRole(string? roleCode) =>
        string.Equals(roleCode, CompanyService.SystemAdminRole, StringComparison.OrdinalIgnoreCase);

    private AdminContext ValidateAdminContext()
    {
        if (!_currentUser.IsAuthenticated)
        {
            return AdminContext.Fail("Not authorized.");
        }

        if (!_currentUser.IsInRole(AdminRole) &&
            !_currentUser.IsInRole(CompanyService.SystemAdminRole))
        {
            return AdminContext.Fail("Not authorized.");
        }

        if (string.IsNullOrWhiteSpace(_currentUser.SubjectUid) ||
            string.IsNullOrWhiteSpace(_currentUser.UserId))
        {
            return AdminContext.Fail("Invalid user identity.");
        }

        var companyCode = _currentUser.CompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return AdminContext.Fail("Invalid company context.");
        }

        return AdminContext.Ok(companyCode, _currentUser.UserId);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private readonly record struct AdminContext(string? CompanyCode, string? UserId, string? Error)
    {
        public static AdminContext Ok(string companyCode, string userId) => new(companyCode, userId, null);

        public static AdminContext Fail(string error) => new(null, null, error);
    }

    private readonly record struct RoleCheckResult(string? RoleCode, string? Error)
    {
        public static RoleCheckResult Ok(string roleCode) => new(roleCode, null);

        public static RoleCheckResult Fail(string error) => new(null, error);
    }
}
