using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

public class AuthServiceChangePasswordTests
{
    [Fact]
    public async Task Uses_authenticated_LoginId_for_policy_not_client_username()
    {
        var hash = PasswordHasher.Hash("OldPass1");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetByUidAsync(1, It.IsAny<CancellationToken>()))
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
                active = true
            });
        users.Setup(x => x.UpdatePasswordAsync(1, It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, string passwordHash, string? _, CancellationToken _) => new UserLogin
            {
                uid = 1,
                id = "admin",
                name = "Admin",
                password = passwordHash,
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                UserID = "admin",
                changepass = false
            });

        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.LoginId).Returns("admin");
        current.SetupGet(x => x.UserId).Returns("admin");

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
            NullLogger<AuthService>.Instance);

        var result = await sut.ChangePasswordAsync("OldPass1", "NewPass99");

        Assert.False(result.Succeeded);
        policy.Verify(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        users.Verify(x => x.UpdatePasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class UserAdminPasswordTests
{
    private static UserAdminService CreateSut(
        Mock<ICurrentUserService> current,
        Mock<IUserLoginRepository>? users = null,
        Mock<IPasswordPolicy>? policy = null,
        Mock<IAccessRightService>? accessRights = null)
    {
        users ??= new Mock<IUserLoginRepository>();
        policy ??= new Mock<IPasswordPolicy>();
        policy.Setup(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(PasswordValidationResult.Success());

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
            NullLogger<UserAdminService>.Instance);
    }

    private static Mock<ICurrentUserService> AdminCurrent(
        string company = "DEMO",
        string userId = "admin",
        string subject = "1",
        bool isAdmin = true,
        bool authenticated = true)
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(authenticated);
        current.SetupGet(x => x.SubjectUid).Returns(subject);
        current.SetupGet(x => x.UserId).Returns(userId);
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.SetupGet(x => x.LoginId).Returns("admin");
        current.Setup(x => x.IsInRole("ADMIN")).Returns(isAdmin);
        return current;
    }

    [Fact]
    public async Task Rejects_cross_company_target_on_reset()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(9, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordAdminUserRow?)null);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ChangePasswordAsync(9, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("No Record Selected.", result.ErrorMessage);
        users.Verify(x => x.ResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var result = await sut.ChangePasswordAsync(2, "TempPass1", "TempPass1");

        Assert.False(result.Succeeded);
        Assert.Equal("Not authorized.", result.ErrorMessage);
        users.Verify(x => x.GetAdminRowAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirmation_mismatch_rejects_before_repo()
    {
        var users = new Mock<IUserLoginRepository>();
        var sut = CreateSut(AdminCurrent(), users);

        var result = await sut.ChangePasswordAsync(2, "TempPass1", "TempPass2");

        Assert.False(result.Succeeded);
        Assert.Equal("Password Not Match", result.ErrorMessage);
        users.Verify(x => x.GetAdminRowAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(x => x.ResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reset_succeeds_for_same_company_target()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasswordAdminUserRow
            {
                Uid = 2,
                LoginId = "clerk",
                Name = "Clerk",
                UserId = "clerk",
                Active = true,
                ChangePass = false
            });
        users.Setup(x => x.ResetPasswordAsync(2, "DEMO", It.IsAny<string>(), "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ChangePasswordAsync(2, "TempPass1", "TempPass1");

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorMessage);
        users.Verify(x => x.ResetPasswordAsync(2, "DEMO", It.Is<string>(h => h.StartsWith("$2", StringComparison.Ordinal)), "admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Force_change_updates_flag_only()
    {
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.GetAdminRowAsync(2, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PasswordAdminUserRow
            {
                Uid = 2,
                LoginId = "clerk",
                Name = "Clerk",
                UserId = "clerk",
                Active = false,
                ChangePass = false
            });
        users.Setup(x => x.SetChangePassAsync(2, "DEMO", true, "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut(AdminCurrent(), users);
        var result = await sut.ForceChangeAsync(2, true);

        Assert.True(result.Succeeded);
        users.Verify(x => x.SetChangePassAsync(2, "DEMO", true, "admin", It.IsAny<CancellationToken>()), Times.Once);
        users.Verify(x => x.ResetPasswordAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
