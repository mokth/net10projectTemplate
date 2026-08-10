using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

public class AccessRightServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AccessRightServiceTests()
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

    [Fact]
    public async Task E2E_User_Role_Permission_CanAsync_POST()
    {
        await SeedSalesScenarioAsync();

        var current = MockUser(uid: 1, company: "DEMO", admin: false);
        var sut = CreateSut(current);

        Assert.True(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));
        Assert.True(await sut.CanAccessAsync("DASHBOARD"));
    }

    [Fact]
    public async Task Company_isolation_prevents_cross_company_permissions()
    {
        await SeedSalesScenarioAsync();

        var current = MockUser(uid: 1, company: "OTHER", admin: false);
        var sut = CreateSut(current);

        Assert.False(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));
    }

    [Fact]
    public async Task ACCESS_gate_blocks_action_when_ACCESS_missing()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.UserLogins.Add(User(1, "DEMO", "SALES"));
            var role = new Role { CompanyCode = "DEMO", RoleCode = "SALES", RoleName = "Sales", IsActive = true };
            db.Roles.Add(role);
            var menu = new Menu { MenuCode = "DASHBOARD", MenuName = "Dashboard", IsActive = true };
            db.Menus.Add(menu);
            var access = Perm(PermissionCodes.Access, "Navigation", 1);
            var post = Perm(PermissionCodes.Post, "Action", 2);
            db.Permissions.AddRange(access, post);
            await db.SaveChangesAsync();

            db.UserRoleMappings.Add(new UserRoleMapping { UserUid = 1, RoleId = role.RoleId });
            db.MenuPermissions.AddRange(
                new MenuPermission { MenuId = menu.MenuId, PermissionId = access.PermissionId, IsActive = true },
                new MenuPermission { MenuId = menu.MenuId, PermissionId = post.PermissionId, IsActive = true });
            // Only POST allowed, no ACCESS
            db.RoleMenuPermissions.Add(new RoleMenuPermission
            {
                RoleId = role.RoleId,
                MenuId = menu.MenuId,
                PermissionId = post.PermissionId,
                IsAllowed = true
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut(MockUser(1, "DEMO", false));
        Assert.False(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));
    }

    [Fact]
    public async Task Multiple_roles_OR_aggregate()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.UserLogins.Add(User(1, "DEMO", "MULTI"));
            var roleA = new Role { CompanyCode = "DEMO", RoleCode = "A", RoleName = "A", IsActive = true };
            var roleB = new Role { CompanyCode = "DEMO", RoleCode = "B", RoleName = "B", IsActive = true };
            db.Roles.AddRange(roleA, roleB);
            var menu = new Menu { MenuCode = "DASHBOARD", MenuName = "Dashboard", IsActive = true };
            db.Menus.Add(menu);
            var access = Perm(PermissionCodes.Access, "Navigation", 1);
            var edit = Perm(PermissionCodes.Edit, "Action", 2);
            var post = Perm(PermissionCodes.Post, "Action", 3);
            db.Permissions.AddRange(access, edit, post);
            await db.SaveChangesAsync();

            db.UserRoleMappings.AddRange(
                new UserRoleMapping { UserUid = 1, RoleId = roleA.RoleId },
                new UserRoleMapping { UserUid = 1, RoleId = roleB.RoleId });
            db.MenuPermissions.AddRange(
                new MenuPermission { MenuId = menu.MenuId, PermissionId = access.PermissionId, IsActive = true },
                new MenuPermission { MenuId = menu.MenuId, PermissionId = edit.PermissionId, IsActive = true },
                new MenuPermission { MenuId = menu.MenuId, PermissionId = post.PermissionId, IsActive = true });
            db.RoleMenuPermissions.AddRange(
                new RoleMenuPermission { RoleId = roleA.RoleId, MenuId = menu.MenuId, PermissionId = access.PermissionId, IsAllowed = true },
                new RoleMenuPermission { RoleId = roleA.RoleId, MenuId = menu.MenuId, PermissionId = edit.PermissionId, IsAllowed = true },
                new RoleMenuPermission { RoleId = roleB.RoleId, MenuId = menu.MenuId, PermissionId = access.PermissionId, IsAllowed = true },
                new RoleMenuPermission { RoleId = roleB.RoleId, MenuId = menu.MenuId, PermissionId = post.PermissionId, IsAllowed = true });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut(MockUser(1, "DEMO", false));
        Assert.True(await sut.CanAsync("DASHBOARD", PermissionCodes.Edit));
        Assert.True(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));
    }

    [Fact]
    public async Task Explicit_DENY_overrides_ALLOW_across_roles()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.UserLogins.Add(User(1, "DEMO", "MULTI"));
            var manager = new Role { CompanyCode = "DEMO", RoleCode = "MANAGER", RoleName = "Manager", IsActive = true };
            var hr = new Role { CompanyCode = "DEMO", RoleCode = "HR", RoleName = "HR", IsActive = true };
            db.Roles.AddRange(manager, hr);
            var menu = new Menu { MenuCode = "DASHBOARD", MenuName = "Dashboard", IsActive = true };
            db.Menus.Add(menu);
            var access = Perm(PermissionCodes.Access, "Navigation", 1);
            var edit = Perm(PermissionCodes.Edit, "Action", 2);
            db.Permissions.AddRange(access, edit);
            await db.SaveChangesAsync();

            db.UserRoleMappings.AddRange(
                new UserRoleMapping { UserUid = 1, RoleId = manager.RoleId },
                new UserRoleMapping { UserUid = 1, RoleId = hr.RoleId });
            db.MenuPermissions.AddRange(
                new MenuPermission { MenuId = menu.MenuId, PermissionId = access.PermissionId, IsActive = true },
                new MenuPermission { MenuId = menu.MenuId, PermissionId = edit.PermissionId, IsActive = true });
            db.RoleMenuPermissions.AddRange(
                new RoleMenuPermission { RoleId = manager.RoleId, MenuId = menu.MenuId, PermissionId = access.PermissionId, IsAllowed = true },
                new RoleMenuPermission { RoleId = manager.RoleId, MenuId = menu.MenuId, PermissionId = edit.PermissionId, IsAllowed = true },
                new RoleMenuPermission { RoleId = hr.RoleId, MenuId = menu.MenuId, PermissionId = access.PermissionId, IsAllowed = true },
                new RoleMenuPermission { RoleId = hr.RoleId, MenuId = menu.MenuId, PermissionId = edit.PermissionId, IsAllowed = false });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut(MockUser(1, "DEMO", false));
        Assert.True(await sut.CanAccessAsync("DASHBOARD"));
        Assert.False(await sut.CanAsync("DASHBOARD", PermissionCodes.Edit));
    }

    [Fact]
    public async Task Permission_not_in_MenuPermission_is_denied()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.UserLogins.Add(User(1, "DEMO", "SALES"));
            var role = new Role { CompanyCode = "DEMO", RoleCode = "SALES", RoleName = "Sales", IsActive = true };
            db.Roles.Add(role);
            var menu = new Menu { MenuCode = "DASHBOARD", MenuName = "Dashboard", IsActive = true };
            db.Menus.Add(menu);
            var access = Perm(PermissionCodes.Access, "Navigation", 1);
            var post = Perm(PermissionCodes.Post, "Action", 2);
            db.Permissions.AddRange(access, post);
            await db.SaveChangesAsync();

            db.UserRoleMappings.Add(new UserRoleMapping { UserUid = 1, RoleId = role.RoleId });
            // Only ACCESS is applicable to this menu; POST RoleMenuPermission must be ignored.
            db.MenuPermissions.Add(
                new MenuPermission { MenuId = menu.MenuId, PermissionId = access.PermissionId, IsActive = true });
            db.RoleMenuPermissions.AddRange(
                new RoleMenuPermission { RoleId = role.RoleId, MenuId = menu.MenuId, PermissionId = access.PermissionId, IsAllowed = true },
                new RoleMenuPermission { RoleId = role.RoleId, MenuId = menu.MenuId, PermissionId = post.PermissionId, IsAllowed = true });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut(MockUser(1, "DEMO", false));
        Assert.True(await sut.CanAccessAsync("DASHBOARD"));
        Assert.False(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));
    }

    [Fact]
    public async Task Unknown_menu_and_permission_denied()
    {
        await SeedSalesScenarioAsync();
        var sut = CreateSut(MockUser(1, "DEMO", false));

        Assert.False(await sut.CanAccessAsync("DOES_NOT_EXIST"));
        Assert.False(await sut.CanAsync("DASHBOARD", "NOT_A_REAL_PERMISSION"));
    }

    [Fact]
    public async Task ADMIN_bypasses_without_RoleMenuPermission()
    {
        var sut = CreateSut(MockUser(1, "DEMO", admin: true));
        Assert.True(await sut.CanAsync("ADMIN_DEMO", PermissionCodes.Access));
        Assert.True(await sut.CanAsync("ANY_MENU", "FUTURE_PERM"));
    }

    [Fact]
    public async Task ADMIN_GetPermissionsAsync_returns_all_known_codes()
    {
        var sut = CreateSut(MockUser(1, "DEMO", admin: true));
        var perms = await sut.GetPermissionsAsync("ANY_MENU");

        Assert.Equal(PermissionCodes.All.Count, perms.Count);
        Assert.Contains(PermissionCodes.Access, perms);
        Assert.Contains(PermissionCodes.Edit, perms);
        Assert.Contains(PermissionCodes.Add, perms);
        Assert.Contains(PermissionCodes.Post, perms);
    }

    [Fact]
    public async Task Non_admin_action_permission_denied_when_missing()
    {
        await SeedSalesScenarioAsync();
        var sut = CreateSut(MockUser(1, "DEMO", false));

        Assert.True(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));
        Assert.False(await sut.CanAsync("DASHBOARD", PermissionCodes.Edit));
        Assert.False(await sut.CanAsync("DASHBOARD", PermissionCodes.Add));
    }

    [Fact]
    public async Task RefreshPermissionsAsync_reloads_after_revoke()
    {
        await SeedSalesScenarioAsync();
        var sut = CreateSut(MockUser(1, "DEMO", false));
        Assert.True(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var rows = await db.RoleMenuPermissions.ToListAsync();
            db.RoleMenuPermissions.RemoveRange(rows);
            await db.SaveChangesAsync();
        }

        await sut.RefreshPermissionsAsync();
        Assert.False(await sut.CanAsync("DASHBOARD", PermissionCodes.Post));
    }

    [Fact]
    public async Task No_UserRoleMapping_denies()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.UserLogins.Add(User(1, "DEMO", "SALES"));
            db.Menus.Add(new Menu { MenuCode = "DASHBOARD", MenuName = "Dashboard", IsActive = true });
            db.Permissions.Add(Perm(PermissionCodes.Access, "Navigation", 1));
            await db.SaveChangesAsync();
        }

        var sut = CreateSut(MockUser(1, "DEMO", false));
        Assert.False(await sut.CanAccessAsync("DASHBOARD"));
    }

    private AccessRightService CreateSut(Mock<ICurrentUserService> current) =>
        new(current.Object, _factory, NullLogger<AccessRightService>.Instance);

    private static Mock<ICurrentUserService> MockUser(int uid, string company, bool admin)
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns(uid.ToString());
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.Setup(x => x.IsInRole(AccessRightService.AdminRole)).Returns(admin);
        return current;
    }

    private async Task SeedSalesScenarioAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.UserLogins.Add(User(1, "DEMO", "SALES"));
        var role = new Role { CompanyCode = "DEMO", RoleCode = "SALES", RoleName = "Sales", IsActive = true };
        db.Roles.Add(role);
        var menu = new Menu { MenuCode = "DASHBOARD", MenuName = "Dashboard", IsActive = true };
        db.Menus.Add(menu);
        var access = Perm(PermissionCodes.Access, "Navigation", 1);
        var post = Perm(PermissionCodes.Post, "Action", 2);
        db.Permissions.AddRange(access, post);
        await db.SaveChangesAsync();

        db.UserRoleMappings.Add(new UserRoleMapping { UserUid = 1, RoleId = role.RoleId });
        db.MenuPermissions.AddRange(
            new MenuPermission { MenuId = menu.MenuId, PermissionId = access.PermissionId, IsActive = true },
            new MenuPermission { MenuId = menu.MenuId, PermissionId = post.PermissionId, IsActive = true });
        db.RoleMenuPermissions.AddRange(
            new RoleMenuPermission
            {
                RoleId = role.RoleId,
                MenuId = menu.MenuId,
                PermissionId = access.PermissionId,
                IsAllowed = true
            },
            new RoleMenuPermission
            {
                RoleId = role.RoleId,
                MenuId = menu.MenuId,
                PermissionId = post.PermissionId,
                IsAllowed = true
            });
        await db.SaveChangesAsync();
    }

    private static UserLogin User(int uid, string company, string level) => new()
    {
        uid = uid,
        id = "u" + uid,
        name = "User " + uid,
        password = "x",
        CompanyCode = company,
        BranchCode = "HQ",
        LocationCode = "MAIN",
        active = true,
        userlevel = level
    };

    private static Permission Perm(string code, string type, int sort) => new()
    {
        PermissionCode = code,
        PermissionName = code,
        PermissionType = type,
        SortOrder = sort,
        IsActive = true
    };
}
