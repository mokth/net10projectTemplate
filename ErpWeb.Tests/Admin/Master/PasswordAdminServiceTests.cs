using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Admin.Master;
[Trait(TestCategories.Name, TestCategories.Admin)]
public class AuthServiceChangePasswordTests
{
    [Fact]
    public async Task Uses_authenticated_LoginId_for_policy_not_client_username()
    {
        var hash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserLogin
            {
                uid = 1,
                id = "admin",
                name = "Admin",
                password = hash,
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                UserID = "admin",
                active = true,
                changepass = false
            });
        users.Setup(x => x.TryChangeOwnPasswordAsync(
                1, "DEMO", hash, false, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.LoginId).Returns("admin");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns("DEMO");

        string? policyUsername = null;
        var policy = new Mock<IPasswordPolicy>();
        policy.Setup(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<string, string?>((_, username) => policyUsername = username)
            .Returns(PasswordValidationResult.Success());

        var roleSync = new Mock<IUserRoleSyncService>();
        var dbFactory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<ErpWeb.Model.Data.AppDbContext>>();
        var sut = new AuthService(
            users.Object,
            dbFactory.Object,
            current.Object,
            policy.Object,
            roleSync.Object,
            Mock.Of<ISecurityAuditWriter>(),
            NullLogger<AuthService>.Instance);

        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.True(result.Succeeded);
        Assert.Equal("admin", policyUsername);
        policy.Verify(x => x.Validate("NewPass99", "admin"), Times.Once);
    }

    [Fact]
    public async Task Fails_closed_when_LoginId_missing()
    {
        var users = new Mock<IUserLoginRepository>();
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.LoginId).Returns((string?)null);

        var policy = new Mock<IPasswordPolicy>();
        var roleSync = new Mock<IUserRoleSyncService>();
        var dbFactory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<ErpWeb.Model.Data.AppDbContext>>();
        var sut = new AuthService(
            users.Object,
            dbFactory.Object,
            current.Object,
            policy.Object,
            roleSync.Object,
            Mock.Of<ISecurityAuditWriter>(),
            NullLogger<AuthService>.Instance);

        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.False(result.Succeeded);
        policy.Verify(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        users.Verify(x => x.TryChangeOwnPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

[Trait(TestCategories.Name, TestCategories.Admin)]
public class AuthServiceSelfPasswordChangeTests
{
    private const string Company = "DEMO";

    private static UserLogin Target(string passwordHash, bool changePass = false, bool active = true) =>
        new()
        {
            uid = 1,
            id = "admin",
            name = "Admin",
            password = passwordHash,
            CompanyCode = Company,
            BranchCode = "HQ",
            LocationCode = "MAIN",
            UserID = "admin",
            active = active,
            changepass = changePass
        };

    private static Mock<ICurrentUserService> Current(string company = Company)
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.LoginId).Returns("admin");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns(company);
        return current;
    }

    private static AuthService CreateSut(
        IUserLoginRepository users,
        ICurrentUserService current,
        IPasswordPolicy? policy = null)
    {
        var effectivePolicy = policy ?? CreateAcceptingPolicy();
        return new AuthService(
            users,
            Mock.Of<Microsoft.EntityFrameworkCore.IDbContextFactory<ErpWeb.Model.Data.AppDbContext>>(),
            current,
            effectivePolicy,
            Mock.Of<IUserRoleSyncService>(),
            Mock.Of<ISecurityAuditWriter>(),
            NullLogger<AuthService>.Instance);
    }

    private static IPasswordPolicy CreateAcceptingPolicy()
    {
        var policy = new Mock<IPasswordPolicy>();
        policy.Setup(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(PasswordValidationResult.Success());
        return policy.Object;
    }

    [Fact]
    public async Task Valid_current_and_new_password_succeeds()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Target(oldHash));
        users.Setup(x => x.TryChangeOwnPasswordAsync(1, Company, oldHash, false, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.True(result.Succeeded, result.ErrorMessage);
        users.Verify(x => x.TryChangeOwnPasswordAsync(1, Company, oldHash, false, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Wrong_current_password_is_denied()
    {
        var oldHash = PasswordHasher.Hash("RealPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Target(oldHash));

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("WrongPass1", "NewPass99");

        Assert.False(result.Succeeded);
        Assert.Equal("Current password is incorrect.", result.ErrorMessage);
        users.Verify(x => x.TryChangeOwnPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task New_password_equal_to_current_is_denied()
    {
        var oldHash = PasswordHasher.Hash("SamePass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Target(oldHash));

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("SamePass1", "SamePass1");

        Assert.False(result.Succeeded);
        Assert.Equal("New password must be different from the current password.", result.ErrorMessage);
        users.Verify(x => x.TryChangeOwnPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cross_company_uid_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserLogin?)null); // uid 1 belongs to another company

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.False(result.Succeeded);
        Assert.Equal("Unable to change password.", result.ErrorMessage);
        users.Verify(x => x.TryChangeOwnPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deleted_user_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserLogin?)null);

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.False(result.Succeeded);
        Assert.Equal("Unable to change password.", result.ErrorMessage);
    }

    [Fact]
    public async Task Inactive_user_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserLogin?)null);

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.False(result.Succeeded);
        Assert.Equal("Unable to change password.", result.ErrorMessage);
    }

    [Fact]
    public async Task Policy_failure_is_denied()
    {
        var policy = new Mock<IPasswordPolicy>();
        policy.Setup(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(PasswordValidationResult.Failure(["Password must contain at least one ASCII digit (0-9)."]));

        var users = new Mock<IUserLoginRepository>();
        var sut = CreateSut(users.Object, Current().Object, policy.Object);

        var result = await sut.ChangePasswordAsync("OldPass1", "NoDigitsHere");

        Assert.False(result.Succeeded);
        Assert.Contains("ASCII digit", result.ErrorMessage);
        users.Verify(x => x.GetActiveByUidAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(x => x.TryChangeOwnPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cas_conflict_when_password_hash_changed_is_reported()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Target(oldHash));
        users.Setup(x => x.TryChangeOwnPasswordAsync(1, Company, oldHash, false, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // hash no longer matches (concurrent reset)

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.False(result.Succeeded);
        Assert.Equal("Your password was changed by another session. Please try again.", result.ErrorMessage);
    }

    [Fact]
    public async Task Cas_conflict_when_changepass_changed_is_reported()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Target(oldHash, changePass: true));
        users.Setup(x => x.TryChangeOwnPasswordAsync(1, Company, oldHash, true, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // changepass flag no longer matches

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.False(result.Succeeded);
        Assert.Equal("Your password was changed by another session. Please try again.", result.ErrorMessage);
    }

    [Fact]
    public async Task Success_clears_changepass_via_cas_predicate()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetActiveByUidAsync(1, Company, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Target(oldHash, changePass: true));
        users.Setup(x => x.TryChangeOwnPasswordAsync(1, Company, oldHash, true, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(users.Object, Current().Object);
        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.True(result.Succeeded, result.ErrorMessage);
        // The CAS predicate must carry the verified changepass=true; the repository sets changepass=false.
        users.Verify(x => x.TryChangeOwnPasswordAsync(1, Company, oldHash, true, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()), Times.Once);
    }
}

[Trait(TestCategories.Name, TestCategories.Admin)]
public class UserAdminPasswordTests
{
    private static UserAdminService CreateSut(
        Mock<ICurrentUserService> current,
        Mock<IUserLoginRepository>? users = null,
        Mock<IPasswordPolicy>? policy = null,
        Mock<IAccessRightService>? accessRights = null)
    {
        users ??= new Mock<IUserLoginRepository>();
        if (policy is null)
        {
            policy = new Mock<IPasswordPolicy>();
            policy.Setup(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(PasswordValidationResult.Success());
        }

        if (accessRights is null)
        {
            accessRights = new Mock<IAccessRightService>();
            accessRights
                .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        return new UserAdminService(
            users.Object,
            current.Object,
            accessRights.Object,
            policy.Object,
            Mock.Of<ISecurityAuditWriter>(),
            Mock.Of<IPasswordRecoveryService>(),
            NullLogger<UserAdminService>.Instance);
    }

    private static Mock<ICurrentUserService> AdminCurrent(
        string company = "DEMO",
        string userId = "admin",
        string subject = "1",
        bool isAdmin = true,
        bool authenticated = true,
        bool isSystemAdmin = false)
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(authenticated);
        current.SetupGet(x => x.SubjectUid).Returns(subject);
        current.SetupGet(x => x.UserId).Returns(userId);
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.SetupGet(x => x.LoginId).Returns("admin");
        current.Setup(x => x.IsInRole("ADMIN")).Returns(isAdmin);
        current.Setup(x => x.IsInRole(CompanyService.SystemAdminRole)).Returns(isSystemAdmin);
        return current;
    }

    private static UserAdminRow Row(int uid, string loginId, string? roleCode, bool active = true) =>
        new()
        {
            Uid = uid,
            LoginId = loginId,
            Name = loginId,
            RoleCode = roleCode,
            Active = active,
            ChangePass = false
        };

    private static UserAdminSecurity Security(string loginId, string? roleCode, string? passwordHash = null, bool changePass = false) =>
        new()
        {
            LoginId = loginId,
            RoleCode = roleCode,
            PasswordHash = passwordHash ?? PasswordHasher.Hash("OldPass1"),
            ChangePass = changePass
        };

    [Fact]
    public async Task Rejects_cross_company_target_on_reset()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(9, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAdminSecurity?)null);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(9, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("No Record Selected.", result.ErrorMessage);
        users.Verify(x => x.TryResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rejects_when_menu_permission_denied()
    {
        var access = new Mock<IAccessRightService>();
        access
            .Setup(x => x.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var users = new Mock<IUserLoginRepository>();
        var sut = CreateSut(AdminCurrent(), users, accessRights: access);

        var result = await sut.ResetPasswordAsync(2, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("Not authorized.", result.ErrorMessage);
        users.Verify(x => x.GetAdminSecurityAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirmation_mismatch_rejects_before_repo()
    {
        var users = new Mock<IUserLoginRepository>();
        var sut = CreateSut(AdminCurrent(), users);

        var result = await sut.ResetPasswordAsync(2, "TempPass1", "TempPass2");

        Assert.False(result.Succeeded);
        Assert.Equal("Password Not Match", result.ErrorMessage);
        users.Verify(x => x.GetAdminSecurityAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(x => x.TryResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_succeeds_for_same_company_target()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAdminSecurity
            {
                LoginId = "clerk",
                RoleCode = "USER",
                PasswordHash = oldHash,
                ChangePass = false
            });
        users.Setup(x => x.TryResetPasswordAsync(2, "DEMO", oldHash, false, It.IsAny<string>(), "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserSecurityMutationResult.Ok());

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(2, "TempPass1", "TempPass1");

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorMessage);
        // CAS predicate carries the inspected hash + changepass and the new hash is BCrypt.
        users.Verify(x => x.TryResetPasswordAsync(2, "DEMO", oldHash, false, It.Is<string>(h => h.StartsWith("$2", StringComparison.Ordinal)), "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Force_change_updates_flag_only()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAdminSecurity
            {
                LoginId = "clerk",
                RoleCode = "USER",
                PasswordHash = oldHash,
                ChangePass = false
            });
        users.Setup(x => x.TrySetChangePassAsync(2, "DEMO", oldHash, false, true, "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserSecurityMutationResult.Ok());

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ForceChangeAsync(2, true);

        Assert.True(result.Succeeded);
        // Force-change never carries a new hash and is guarded by the inspected hash + changepass.
        users.Verify(x => x.TrySetChangePassAsync(2, "DEMO", oldHash, false, true, "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        users.Verify(x => x.TryResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddUser_confirmation_mismatch_rejects_before_repo()
    {
        var users = new Mock<IUserLoginRepository>();
        var sut = CreateSut(AdminCurrent(), users);

        var result = await sut.AddUserAsync(new CreateUserRequest
        {
            LoginId = "newuser",
            Name = "New User",
            TemporaryPassword = "TempPass1",
            ConfirmPassword = "TempPass2",
            RoleCode = "USER"
        });

        Assert.False(result.Succeeded);
        Assert.Equal("Password Not Match", result.ErrorMessage);
        users.Verify(x => x.AddAsync(It.IsAny<UserLogin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddUser_hashes_password_and_derives_company_branch_location()
    {
        var current = AdminCurrent();
        current.SetupGet(x => x.BranchCode).Returns("HQ");
        current.SetupGet(x => x.LocationCode).Returns("MAIN");

        UserLogin? captured = null;
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.LoginExistsAsync("DEMO", "newuser", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        users.Setup(x => x.IsActiveRoleAsync("DEMO", "USER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        users.Setup(x => x.AddAsync(It.IsAny<UserLogin>(), It.IsAny<CancellationToken>()))
            .Callback<UserLogin, CancellationToken>((u, _) => captured = u)
            .ReturnsAsync((UserLogin u, CancellationToken _) => u);

        var sut = CreateSut(current, users);
        var result = await sut.AddUserAsync(new CreateUserRequest
        {
            LoginId = "newuser",
            Name = "New User",
            TemporaryPassword = "TempPass1",
            ConfirmPassword = "TempPass1",
            RoleCode = "USER",
            CanViewPrice = true
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(captured);
        Assert.Equal("DEMO", captured!.CompanyCode);
        Assert.Equal("HQ", captured.BranchCode);
        Assert.Equal("MAIN", captured.LocationCode);
        Assert.True(captured.changepass);
        Assert.True(captured.CanViewPrice);
        Assert.True(captured.password.StartsWith("$2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateUser_edits_without_password_via_narrow_model()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(2, "clerk", "USER"));
        users.Setup(x => x.IsActiveRoleAsync("DEMO", "USER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        users.Setup(x => x.UpdateProfileAsync(
                It.IsAny<EditUserRequest>(), "DEMO", "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.UpdateUserAsync(new EditUserRequest
        {
            Uid = 2,
            LoginId = "clerk",
            Name = "Clerk Updated",
            RoleCode = "USER",
            CanViewPrice = true
        });

        Assert.True(result.Succeeded);
        users.Verify(x => x.UpdateProfileAsync(
            It.Is<EditUserRequest>(r => r.Uid == 2 && r.CanViewPrice && r.Name == "Clerk Updated"),
            "DEMO", "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddUser_ADMIN_assigning_SYSTEM_ADMIN_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.LoginExistsAsync("DEMO", "newsa", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.AddUserAsync(new CreateUserRequest
        {
            LoginId = "newsa",
            Name = "New SA",
            TemporaryPassword = "TempPass1",
            ConfirmPassword = "TempPass1",
            RoleCode = "SYSTEM_ADMIN"
        });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot assign the SYSTEM_ADMIN role.", result.ErrorMessage);
        users.Verify(x => x.AddAsync(It.IsAny<UserLogin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUser_ADMIN_assigning_SYSTEM_ADMIN_role_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(2, "clerk", "USER"));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.UpdateUserAsync(new EditUserRequest
        {
            Uid = 2,
            LoginId = "clerk",
            Name = "Clerk",
            RoleCode = "SYSTEM_ADMIN"
        });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot assign the SYSTEM_ADMIN role.", result.ErrorMessage);
        users.Verify(x => x.UpdateProfileAsync(It.IsAny<EditUserRequest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUser_ADMIN_mutating_SYSTEM_ADMIN_target_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(5, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(5, "sysadmin", "SYSTEM_ADMIN"));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.UpdateUserAsync(new EditUserRequest
        {
            Uid = 5,
            LoginId = "sysadmin",
            Name = "Renamed",
            RoleCode = "SYSTEM_ADMIN"
        });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot modify a SYSTEM_ADMIN account.", result.ErrorMessage);
        users.Verify(x => x.UpdateProfileAsync(It.IsAny<EditUserRequest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUser_self_deactivate_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(1, "admin", "ADMIN", active: true));
        users.Setup(x => x.IsActiveRoleAsync("DEMO", "ADMIN", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.UpdateUserAsync(new EditUserRequest
        {
            Uid = 1,
            LoginId = "admin",
            Name = "Admin",
            RoleCode = "ADMIN",
            Active = false
        });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot deactivate your own account.", result.ErrorMessage);
        users.Verify(x => x.UpdateProfileAsync(It.IsAny<EditUserRequest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUser_self_role_change_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Row(1, "admin", "ADMIN", active: true));
        users.Setup(x => x.IsActiveRoleAsync("DEMO", "USER", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.UpdateUserAsync(new EditUserRequest
        {
            Uid = 1,
            LoginId = "admin",
            Name = "Admin",
            RoleCode = "USER",
            Active = true
        });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot change your own role.", result.ErrorMessage);
        users.Verify(x => x.UpdateProfileAsync(It.IsAny<EditUserRequest>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetPassword_self_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("admin", "ADMIN"));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(1, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot reset your own password.", result.ErrorMessage);
        users.Verify(x => x.TryResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForceChange_self_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("admin", "ADMIN"));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ForceChangeAsync(1, true);

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot change your own password-change requirement.", result.ErrorMessage);
        users.Verify(x => x.TrySetChangePassAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetPassword_SYSTEM_ADMIN_target_by_ADMIN_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(5, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("sysadmin", "SYSTEM_ADMIN"));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(5, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot reset a SYSTEM_ADMIN password.", result.ErrorMessage);
        users.Verify(x => x.TryResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteUsers_self_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.DeleteUsersAsync(new[] { 1, 2 });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot delete your own account.", result.ErrorMessage);
        users.Verify(x => x.SoftDeleteAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase 7: reset/force-change concurrency & privilege boundaries ─────

    [Fact]
    public async Task Reset_deleted_target_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAdminSecurity?)null); // soft-deleted or cross-company

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(2, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("No Record Selected.", result.ErrorMessage);
        users.Verify(x => x.TryResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_policy_failure_is_denied()
    {
        var policy = new Mock<IPasswordPolicy>();
        policy.Setup(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(PasswordValidationResult.Failure(["Password must contain at least one ASCII digit (0-9)."]));

        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("clerk", "USER"));

        var sut = CreateSut(AdminCurrent(), users, policy);
        var result = await sut.ResetPasswordAsync(2, "NoDigitsHere", "NoDigitsHere");

        Assert.False(result.Succeeded);
        Assert.Contains("ASCII digit", result.ErrorMessage);
        users.Verify(x => x.TryResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_conflict_when_hash_changed_is_reported()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("clerk", "USER", oldHash));
        users.Setup(x => x.TryResetPasswordAsync(2, "DEMO", oldHash, false, It.IsAny<string>(), "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserSecurityMutationResult.Fail(UserSecurityMutationStatus.ConcurrencyConflict));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(2, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("The user's security state was changed by another operation. Please refresh and try again.", result.ErrorMessage);
    }

    [Fact]
    public async Task Reset_conflict_when_changepass_changed_is_reported()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("clerk", "USER", oldHash, changePass: true));
        users.Setup(x => x.TryResetPasswordAsync(2, "DEMO", oldHash, true, It.IsAny<string>(), "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserSecurityMutationResult.Fail(UserSecurityMutationStatus.ConcurrencyConflict));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(2, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("The user's security state was changed by another operation. Please refresh and try again.", result.ErrorMessage);
    }

    [Fact]
    public async Task ForceChange_SYSTEM_ADMIN_target_by_ADMIN_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(5, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("sysadmin", "SYSTEM_ADMIN"));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ForceChangeAsync(5, true);

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot change a SYSTEM_ADMIN account.", result.ErrorMessage);
        users.Verify(x => x.TrySetChangePassAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForceChange_cross_company_target_is_denied()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAdminSecurity?)null);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ForceChangeAsync(2, true);

        Assert.False(result.Succeeded);
        Assert.Equal("No Record Selected.", result.ErrorMessage);
        users.Verify(x => x.TrySetChangePassAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForceChange_conflict_does_not_clear_newer_reset()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("clerk", "USER", oldHash));
        users.Setup(x => x.TrySetChangePassAsync(2, "DEMO", oldHash, false, false, "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserSecurityMutationResult.Fail(UserSecurityMutationStatus.ConcurrencyConflict));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ForceChangeAsync(2, false);

        Assert.False(result.Succeeded);
        Assert.Equal("The user's security state was changed by another operation. Please refresh and try again.", result.ErrorMessage);
    }

    [Fact]
    public async Task Privilege_changed_to_SYSTEM_ADMIN_before_mutation_fails()
    {
        var oldHash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        // The service read the target as a normal USER, but the guarded mutation re-checked and found it
        // became SYSTEM_ADMIN (defense-in-depth protected-target outcome).
        users.Setup(x => x.GetAdminSecurityAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Security("clerk", "USER", oldHash));
        users.Setup(x => x.TryResetPasswordAsync(2, "DEMO", oldHash, false, It.IsAny<string>(), "admin", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UserSecurityMutationResult.Fail(UserSecurityMutationStatus.ProtectedTarget));

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ResetPasswordAsync(2, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot modify a SYSTEM_ADMIN account.", result.ErrorMessage);
    }
}
