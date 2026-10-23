using System.Security.Claims;
using ErpWeb.Core.Authentication;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class AuthSecurityStateValidatorTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AuthSecurityStateValidatorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private AuthSecurityStateValidator CreateSut() =>
        new(_factory, NullLogger<AuthSecurityStateValidator>.Instance);

    private async Task SeedValidUserAsync(bool active = true, bool? deleted = false, string? level = "USER")
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Roles.Add(new Role { CompanyCode = "DEMO", RoleCode = "USER", RoleName = "User", IsActive = true });
        db.UserLogins.Add(new UserLogin
        {
            uid = 1,
            id = "clerk",
            name = "Clerk",
            password = "hash",
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = active,
            userlevel = level,
            UpdatedUID = deleted == true ? "deleted" : null,
            changepass = false,
            CanViewPrice = false
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Valid_user_and_active_role_is_valid()
    {
        await SeedValidUserAsync();
        var sut = CreateSut();
        Assert.True(await sut.IsValidAsync(Principal()));
    }

    [Fact]
    public async Task Inactive_user_is_invalid()
    {
        await SeedValidUserAsync(active: false);
        var sut = CreateSut();
        Assert.False(await sut.IsValidAsync(Principal()));
    }

    [Fact]
    public async Task Soft_deleted_user_is_invalid()
    {
        await SeedValidUserAsync(deleted: true);
        var sut = CreateSut();
        Assert.False(await sut.IsValidAsync(Principal()));
    }

    [Fact]
    public async Task ChangePass_mismatch_is_invalid()
    {
        await SeedValidUserAsync();
        var sut = CreateSut();
        Assert.False(await sut.IsValidAsync(Principal(changePass: true)));
    }

    [Fact]
    public async Task UserLevel_mismatch_is_invalid()
    {
        await SeedValidUserAsync(level: "ADMIN");
        var sut = CreateSut();
        Assert.False(await sut.IsValidAsync(Principal(level: "USER")));
    }

    [Fact]
    public async Task Inactive_role_is_invalid()
    {
        await SeedValidUserAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var role = await db.Roles.SingleAsync(r => r.RoleCode == "USER");
            role.IsActive = false;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        Assert.False(await sut.IsValidAsync(Principal()));
    }

    [Fact]
    public async Task Missing_role_is_invalid()
    {
        await SeedValidUserAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Roles.RemoveRange(db.Roles);
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        Assert.False(await sut.IsValidAsync(Principal()));
    }

    [Fact]
    public async Task Inactive_company_is_invalid()
    {
        await SeedValidUserAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Companies.Add(new Company { CompanyCode = "DEMO", CompanyName = "Demo", IsActive = false });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        Assert.False(await sut.IsValidAsync(Principal()));
    }

    [Fact]
    public async Task Missing_company_is_legacy_allow()
    {
        await SeedValidUserAsync();
        var sut = CreateSut();
        Assert.True(await sut.IsValidAsync(Principal()));
    }

    [Fact]
    public async Task Missing_subject_is_invalid()
    {
        await SeedValidUserAsync();
        var sut = CreateSut();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(AppClaimTypes.CompanyCode, "DEMO")
        }, "Cookies"));
        Assert.False(await sut.IsValidAsync(principal));
    }

    private static ClaimsPrincipal Principal(
        string subject = "1",
        string company = "DEMO",
        string branch = "HQ",
        string location = "MAIN",
        string level = "USER",
        bool changePass = false,
        bool canViewPrice = false)
    {
        var claims = new List<Claim>
        {
            new(AppClaimTypes.Subject, subject),
            new(AppClaimTypes.CompanyCode, company),
            new(AppClaimTypes.BranchCode, branch),
            new(AppClaimTypes.LocationCode, location),
            new(AppClaimTypes.Level, level),
            new(AppClaimTypes.ChangePassword, changePass ? "true" : "false"),
            new(AppClaimTypes.CanViewPrice, canViewPrice ? "true" : "false"),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }
}

[Trait(TestCategories.Name, TestCategories.Admin)]
public class AccessRightServiceForcedChangeTests
{
    [Fact]
    public async Task Forced_change_denies_even_admin_bypass()
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.CompanyCode).Returns("DEMO");
        current.SetupGet(x => x.MustChangePassword).Returns(true);
        current.Setup(x => x.IsInRole(AccessRightService.AdminRole)).Returns(true);
        current.Setup(x => x.IsInRole(AccessRightService.SystemAdminRole)).Returns(true);

        var sut = new AccessRightService(
            current.Object,
            Mock.Of<IDbContextFactory<AppDbContext>>(),
            NullLogger<AccessRightService>.Instance);

        Assert.False(await sut.CanAsync("ANY_MENU", PermissionCodes.Access));
        Assert.Empty(await sut.GetPermissionsAsync("ANY_MENU"));
    }
}

[Trait(TestCategories.Name, TestCategories.Admin)]
public class CurrentUserServiceProviderAuthorityTests
{
    [Fact]
    public void Provider_unauthenticated_does_not_resurrect_stale_HttpContext_user()
    {
        var httpUser = AuthenticatedPrincipal();
        var http = new DefaultHttpContext { User = httpUser };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(http);

        var provider = new Mock<AuthenticationStateProvider>();
        provider.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

        var services = new ServiceCollection();
        services.AddSingleton(provider.Object);
        var sut = new CurrentUserService(accessor.Object, services.BuildServiceProvider());

        Assert.False(sut.IsAuthenticated);
    }

    [Fact]
    public void Provider_not_initialized_falls_back_to_HttpContext()
    {
        var httpUser = AuthenticatedPrincipal();
        var http = new DefaultHttpContext { User = httpUser };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(http);

        var provider = new Mock<AuthenticationStateProvider>();
        provider.Setup(x => x.GetAuthenticationStateAsync())
            .ThrowsAsync(new InvalidOperationException("not initialized"));

        var services = new ServiceCollection();
        services.AddSingleton(provider.Object);
        var sut = new CurrentUserService(accessor.Object, services.BuildServiceProvider());

        Assert.True(sut.IsAuthenticated);
        Assert.Equal("clerk", sut.LoginId);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal()
    {
        var claims = new List<Claim>
        {
            new(AppClaimTypes.Subject, "1"),
            new(AppClaimTypes.LoginId, "clerk"),
            new(AppClaimTypes.CompanyCode, "DEMO"),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }
}

[Trait(TestCategories.Name, TestCategories.Admin)]
public class AuthServiceFinalRecheckTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AuthServiceFinalRecheckTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Roles.Add(new Role { CompanyCode = "DEMO", RoleCode = "USER", RoleName = "User", IsActive = true });
        // Real user row so the role-sync UserRoleMapping insert satisfies its FK before the
        // final-recheck assertion runs.
        db.UserLogins.Add(new UserLogin
        {
            uid = 1,
            id = "clerk",
            name = "Clerk",
            password = "hash",
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = true,
            userlevel = "USER",
            changepass = false,
            CanViewPrice = false
        });
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private AuthService CreateSut(IUserLoginRepository users) =>
        new(
            users,
            _factory,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IPasswordPolicy>(),
            new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance),
            Mock.Of<ISecurityAuditWriter>(),
            NullLogger<AuthService>.Instance);

    private static UserLogin User(string passwordHash) => new()
    {
        uid = 1,
        id = "clerk",
        name = "Clerk",
        password = passwordHash,
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        LocationCode = "MAIN",
        UserID = "clerk",
        active = true,
        userlevel = "USER"
    };

    [Fact]
    public async Task Password_change_during_login_is_rejected()
    {
        var originalHash = PasswordHasher.Hash("Clerk@12");
        var differentHash = PasswordHasher.Hash("Changed@99");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.FindByLoginAsync("DEMO", "clerk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(originalHash));
        users.Setup(x => x.GetActiveByUidAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(differentHash));

        var result = await CreateSut(users.Object).ValidateCredentialsAsync("DEMO", "clerk", "Clerk@12");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthService.GenericLoginFailure, result.ErrorMessage);
    }

    [Fact]
    public async Task Deactivation_during_login_is_rejected()
    {
        var originalHash = PasswordHasher.Hash("Clerk@12");
        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.FindByLoginAsync("DEMO", "clerk", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(originalHash));
        users.Setup(x => x.GetActiveByUidAsync(1, "DEMO", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserLogin?)null);

        var result = await CreateSut(users.Object).ValidateCredentialsAsync("DEMO", "clerk", "Clerk@12");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthService.GenericLoginFailure, result.ErrorMessage);
    }
}
