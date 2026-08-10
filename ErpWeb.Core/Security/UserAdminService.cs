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
    private readonly ILogger<UserAdminService> _logger;

    public UserAdminService(
        IUserLoginRepository users,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        IPasswordPolicy passwordPolicy,
        ILogger<UserAdminService> logger)
    {
        _users = users;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _passwordPolicy = passwordPolicy;
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
        return UserAdminOperationResult.OkRoles(roles);
    }

    public async Task<UserAdminOperationResult> AddUserAsync(
        UserLogin user,
        string plainPassword,
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

        var loginId = (user.id ?? string.Empty).Trim();
        var name = (user.name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(loginId))
        {
            return UserAdminOperationResult.Fail("User ID is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return UserAdminOperationResult.Fail("User name is required.");
        }

        var policyResult = _passwordPolicy.Validate(plainPassword, loginId);
        if (!policyResult.IsValid)
        {
            return UserAdminOperationResult.Fail(string.Join(" ", policyResult.Errors));
        }

        if (await _users.LoginExistsAsync(context.CompanyCode!, loginId, cancellationToken: cancellationToken))
        {
            return UserAdminOperationResult.Fail("User ID already exists.");
        }

        var entity = new UserLogin
        {
            id = loginId,
            name = name,
            password = PasswordHasher.Hash(plainPassword),
            email = user.email?.Trim(),
            mobileno = user.mobileno?.Trim(),
            active = user.active ?? true,
            userlevel = user.userlevel?.Trim(),
            Created = DateTime.Now,
            UserID = Truncate(context.UserId!, 10),
            CompanyCode = context.CompanyCode!,
            BranchCode = string.IsNullOrWhiteSpace(user.BranchCode)
                ? (_currentUser.BranchCode ?? string.Empty)
                : user.BranchCode.Trim(),
            LocationCode = string.IsNullOrWhiteSpace(user.LocationCode)
                ? (_currentUser.LocationCode ?? string.Empty)
                : user.LocationCode.Trim(),
            changepass = true,
            ImagePath = user.ImagePath
        };

        if (string.IsNullOrWhiteSpace(entity.BranchCode) || string.IsNullOrWhiteSpace(entity.LocationCode))
        {
            return UserAdminOperationResult.Fail("Branch and location are required.");
        }

        await _users.AddAsync(entity, cancellationToken);
        _logger.LogInformation(
            "User created. AdminUserId={AdminUserId} NewLoginId={LoginId} CompanyCode={CompanyCode}",
            context.UserId,
            entity.id,
            context.CompanyCode);

        return UserAdminOperationResult.Ok();
    }

    public async Task<UserAdminOperationResult> UpdateUserAsync(
        UserLogin user,
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

        if (user.uid <= 0)
        {
            return UserAdminOperationResult.Fail("Invalid user.");
        }

        var name = (user.name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return UserAdminOperationResult.Fail("User name is required.");
        }

        user.name = name;
        user.email = user.email?.Trim();
        user.mobileno = user.mobileno?.Trim();
        user.userlevel = user.userlevel?.Trim();
        user.CompanyCode = context.CompanyCode!;
        user.Updated = DateTime.Now;
        user.UpdatedUID = Truncate(context.UserId!, 10);
        user.BranchCode = string.IsNullOrWhiteSpace(user.BranchCode)
            ? (_currentUser.BranchCode ?? string.Empty)
            : user.BranchCode.Trim();
        user.LocationCode = string.IsNullOrWhiteSpace(user.LocationCode)
            ? (_currentUser.LocationCode ?? string.Empty)
            : user.LocationCode.Trim();

        var updated = await _users.UpdateProfileAsync(user, cancellationToken);
        if (!updated)
        {
            return UserAdminOperationResult.Fail("Unable to update user.");
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
            cancellationToken);

        if (deleted == 0)
        {
            return UserAdminOperationResult.Fail("Unable to delete user(s).");
        }

        _logger.LogInformation(
            "Users soft-deleted. AdminUserId={AdminUserId} Count={Count} CompanyCode={CompanyCode}",
            context.UserId,
            deleted,
            context.CompanyCode);

        return UserAdminOperationResult.Ok();
    }

    public async Task<UserAdminOperationResult> ChangePasswordAsync(
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

        var target = await _users.GetAdminRowAsync(uid, context.CompanyCode!, cancellationToken);
        if (target is null)
        {
            return UserAdminOperationResult.Fail("No Record Selected.");
        }

        var policyResult = _passwordPolicy.Validate(newPassword, target.LoginId);
        if (!policyResult.IsValid)
        {
            return UserAdminOperationResult.Fail(string.Join(" ", policyResult.Errors));
        }

        var hash = PasswordHasher.Hash(newPassword);
        var updated = await _users.ResetPasswordAsync(
            uid,
            context.CompanyCode!,
            hash,
            Truncate(context.UserId!, 10),
            cancellationToken);

        if (!updated)
        {
            return UserAdminOperationResult.Fail("Unable to change password.");
        }

        _logger.LogInformation(
            "Password reset performed. AdminUserId={AdminUserId} TargetUserId={TargetUserId} TargetUid={TargetUid} CompanyCode={CompanyCode}",
            context.UserId,
            target.UserId,
            target.Uid,
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

        var target = await _users.GetAdminRowAsync(uid, context.CompanyCode!, cancellationToken);
        if (target is null)
        {
            return UserAdminOperationResult.Fail("No Record Selected.");
        }

        var updated = await _users.SetChangePassAsync(
            uid,
            context.CompanyCode!,
            force,
            Truncate(context.UserId!, 10),
            cancellationToken);

        if (!updated)
        {
            return UserAdminOperationResult.Fail("Unable to update password-change requirement.");
        }

        _logger.LogInformation(
            "Force password change {Action}. AdminUserId={AdminUserId} TargetUserId={TargetUserId} TargetUid={TargetUid} CompanyCode={CompanyCode}",
            force ? "set" : "cleared",
            context.UserId,
            target.UserId,
            target.Uid,
            context.CompanyCode);

        return UserAdminOperationResult.Ok();
    }

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
}
