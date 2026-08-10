using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

public class CompanyServiceBootstrapTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public CompanyServiceBootstrapTests()
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

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Companies.Add(new Company
        {
            CompanyCode = "DEMO",
            CompanyName = "Demo Company",
            IsActive = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "SEED"
        });

        var access = new Permission
        {
            PermissionCode = PermissionCodes.Access,
            PermissionName = "Access",
            PermissionType = "Navigation",
            SortOrder = 1,
            IsActive = true
        };
        var edit = new Permission
        {
            PermissionCode = PermissionCodes.Edit,
            PermissionName = "Edit",
            PermissionType = "Action",
            SortOrder = 2,
            IsActive = true
        };
        db.Permissions.AddRange(access, edit);

        var dashboard = new Menu { MenuCode = "DASHBOARD", MenuName = "Dashboard", IsActive = true };
        var inventory = new Menu { MenuCode = "INVENTORY_DEMO", MenuName = "Inventory", IsActive = true };
        var companyMenu = new Menu { MenuCode = "ADMIN_COMPANY", MenuName = "Company", IsActive = true };
        db.Menus.AddRange(dashboard, inventory, companyMenu);
        await db.SaveChangesAsync();

        var demoAdmin = new Role
        {
            CompanyCode = "DEMO",
            RoleCode = "ADMIN",
            RoleName = "Demo Administrator",
            IsActive = true
        };
        var demoUser = new Role
        {
            CompanyCode = "DEMO",
            RoleCode = "USER",
            RoleName = "Demo User",
            IsActive = true
        };
        db.Roles.AddRange(demoAdmin, demoUser);
        await db.SaveChangesAsync();

        db.RoleMenuPermissions.AddRange(
            new RoleMenuPermission
            {
                RoleId = demoUser.RoleId,
                MenuId = dashboard.MenuId,
                PermissionId = access.PermissionId,
                IsAllowed = true
            },
            new RoleMenuPermission
            {
                RoleId = demoUser.RoleId,
                MenuId = inventory.MenuId,
                PermissionId = access.PermissionId,
                IsAllowed = true
            },
            new RoleMenuPermission
            {
                RoleId = demoUser.RoleId,
                MenuId = companyMenu.MenuId,
                PermissionId = access.PermissionId,
                IsAllowed = true
            },
            new RoleMenuPermission
            {
                RoleId = demoUser.RoleId,
                MenuId = companyMenu.MenuId,
                PermissionId = edit.PermissionId,
                IsAllowed = true
            });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task AddCompany_provisions_roles_admin_user_and_copies_user_grants()
    {
        var sut = CreateSut();
        var result = await sut.AddCompanyAsync(
            new Company
            {
                CompanyCode = "ab",
                CompanyName = "Alpha Beta",
                IsActive = true,
                Country = "MY",
                CurrencyCode = "MYR",
                FiscalYearStartMonth = 1
            },
            new CompanyBootstrapRequest
            {
                AdminLoginId = "admin",
                AdminDisplayName = "AB Admin",
                AdminEmail = "admin@ab.local",
                AdminPassword = "Demo@123",
                ConfirmPassword = "Demo@123",
                BranchCode = "HQ",
                LocationCode = "MAIN"
            });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Company);
        Assert.Equal("AB", result.Company!.CompanyCode);
        Assert.NotNull(result.Bootstrap);
        Assert.Equal("admin", result.Bootstrap!.AdminLoginId);
        Assert.Equal("DEMO", result.Bootstrap.TemplateCompanyCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.Roles.AnyAsync(r => r.CompanyCode == "AB" && r.RoleCode == "ADMIN"));
        Assert.True(await db.Roles.AnyAsync(r => r.CompanyCode == "AB" && r.RoleCode == "USER"));

        var adminUser = await db.UserLogins.SingleAsync(u => u.CompanyCode == "AB" && u.id == "admin");
        Assert.Equal("ADMIN", adminUser.userlevel);
        Assert.True(adminUser.changepass);
        Assert.True(adminUser.active);
        Assert.Equal("HQ", adminUser.BranchCode);
        Assert.Equal("MAIN", adminUser.LocationCode);
        Assert.True(PasswordHasher.Verify("Demo@123", adminUser.password));

        var adminRole = await db.Roles.SingleAsync(r => r.CompanyCode == "AB" && r.RoleCode == "ADMIN");
        Assert.True(await db.UserRoleMappings.AnyAsync(m => m.UserUid == adminUser.uid && m.RoleId == adminRole.RoleId));

        var userRole = await db.Roles.SingleAsync(r => r.CompanyCode == "AB" && r.RoleCode == "USER");
        var grants = await db.RoleMenuPermissions.CountAsync(g => g.RoleId == userRole.RoleId && g.IsAllowed);
        Assert.Equal(4, grants);
    }

    [Fact]
    public async Task AddCompany_rejects_password_mismatch()
    {
        var sut = CreateSut();
        var result = await sut.AddCompanyAsync(
            new Company { CompanyCode = "ZZ", CompanyName = "Zed", IsActive = true, FiscalYearStartMonth = 1 },
            new CompanyBootstrapRequest
            {
                AdminLoginId = "admin",
                AdminPassword = "Demo@123",
                ConfirmPassword = "Other@123"
            });

        Assert.False(result.Succeeded);
        Assert.Contains("confirmation", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.Companies.AnyAsync(c => c.CompanyCode == "ZZ"));
    }

    [Fact]
    public async Task Deactivate_company_disables_users_and_blocks_login()
    {
        var companySut = CreateSut();
        var created = await companySut.AddCompanyAsync(
            new Company { CompanyCode = "XY", CompanyName = "Xylophone", IsActive = true, FiscalYearStartMonth = 1 },
            new CompanyBootstrapRequest
            {
                AdminLoginId = "admin",
                AdminPassword = "Demo@123",
                ConfirmPassword = "Demo@123"
            });
        Assert.True(created.Succeeded, created.ErrorMessage);

        var deactivate = await companySut.DeleteCompaniesAsync([created.Company!.CompanyId]);
        Assert.True(deactivate.Succeeded, deactivate.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var company = await db.Companies.SingleAsync(c => c.CompanyCode == "XY");
        Assert.False(company.IsActive);
        var user = await db.UserLogins.SingleAsync(u => u.CompanyCode == "XY" && u.id == "admin");
        Assert.False(user.active);

        var users = new Mock<IUserLoginRepository>();
        users.Setup(x => x.FindByLoginAsync("XY", "admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var auth = new AuthService(
            users.Object,
            _factory,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IPasswordPolicy>(),
            Mock.Of<IUserRoleSyncService>(),
            NullLogger<AuthService>.Instance);

        var login = await auth.ValidateCredentialsAsync("xy", "admin", "Demo@123");
        Assert.False(login.Succeeded);
    }

    [Fact]
    public async Task Company_admin_list_returns_only_own_company()
    {
        var system = CreateSut(systemAdmin: true);
        var created = await system.AddCompanyAsync(
            new Company { CompanyCode = "AB", CompanyName = "Alpha", IsActive = true, FiscalYearStartMonth = 1 },
            new CompanyBootstrapRequest
            {
                AdminLoginId = "admin",
                AdminPassword = "Demo@123",
                ConfirmPassword = "Demo@123"
            });
        Assert.True(created.Succeeded, created.ErrorMessage);

        var companyAdmin = CreateSut(systemAdmin: false, company: "AB", admin: true);
        var list = await companyAdmin.GetCompaniesAsync();

        Assert.True(list.Succeeded, list.ErrorMessage);
        Assert.Single(list.Companies);
        Assert.Equal("AB", list.Companies[0].CompanyCode);
    }

    [Fact]
    public async Task System_admin_list_returns_all_companies()
    {
        var system = CreateSut(systemAdmin: true);
        Assert.True((await system.AddCompanyAsync(
            new Company { CompanyCode = "AB", CompanyName = "Alpha", IsActive = true, FiscalYearStartMonth = 1 },
            new CompanyBootstrapRequest
            {
                AdminLoginId = "admin",
                AdminPassword = "Demo@123",
                ConfirmPassword = "Demo@123"
            })).Succeeded);

        var list = await system.GetCompaniesAsync();
        Assert.True(list.Succeeded, list.ErrorMessage);
        Assert.Contains(list.Companies, c => c.CompanyCode == "DEMO");
        Assert.Contains(list.Companies, c => c.CompanyCode == "AB");
    }

    [Fact]
    public async Task Company_admin_cannot_create_company()
    {
        var companyAdmin = CreateSut(systemAdmin: false, company: "DEMO", admin: true);
        var result = await companyAdmin.AddCompanyAsync(
            new Company { CompanyCode = "NO", CompanyName = "Nope", IsActive = true, FiscalYearStartMonth = 1 },
            new CompanyBootstrapRequest
            {
                AdminLoginId = "admin",
                AdminPassword = "Demo@123",
                ConfirmPassword = "Demo@123"
            });

        Assert.False(result.Succeeded);
        Assert.Equal("Not authorized.", result.ErrorMessage);
    }

    private CompanyService CreateSut(
        bool systemAdmin = true,
        bool admin = false,
        string company = "DEMO")
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.Setup(x => x.IsInRole(CompanyService.SystemAdminRole)).Returns(systemAdmin);
        current.Setup(x => x.IsInRole(CompanyService.AdminRole)).Returns(admin || systemAdmin);

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var policy = new Mock<IPasswordPolicy>();
        policy.Setup(x => x.Validate(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(PasswordValidationResult.Success());

        return new CompanyService(
            _factory,
            current.Object,
            access.Object,
            policy.Object,
            NullLogger<CompanyService>.Instance);
    }
}
