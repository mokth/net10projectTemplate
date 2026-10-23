using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class RoleAdminServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public RoleAdminServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        db.Roles.AddRange(
            Role(1, "DEMO", "ADMIN", "Administrator", active: true),
            Role(2, "DEMO", "SYSTEM_ADMIN", "System Admin", active: true),
            Role(3, "DEMO", "MANAGER", "Manager", active: true),
            Role(4, "OTH", "ADMIN", "Other Admin", active: true));
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private static Role Role(int id, string company, string code, string name, bool active) =>
        new()
        {
            RoleId = id,
            CompanyCode = company,
            RoleCode = code,
            RoleName = name,
            IsActive = active,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "seed"
        };

    private RoleAdminService CreateSut(bool isSystemAdmin = false, string company = "DEMO")
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns(company);
        current.Setup(x => x.IsInRole("ADMIN")).Returns(true);
        current.Setup(x => x.IsInRole(CompanyService.SystemAdminRole)).Returns(isSystemAdmin);

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new RoleAdminService(_factory, current.Object, access.Object, NullLogger<RoleAdminService>.Instance);
    }

    private async Task<Role?> GetRoleAsync(int roleId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Roles.AsNoTracking().FirstOrDefaultAsync(x => x.RoleId == roleId);
    }

    [Fact]
    public async Task AddRole_ADMIN_creating_SYSTEM_ADMIN_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false);
        var result = await sut.AddRoleAsync(new Role { RoleCode = "SYSTEM_ADMIN", RoleName = "Elevated", IsActive = true });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot create the SYSTEM_ADMIN role.", result.ErrorMessage);
    }

    [Fact]
    public async Task AddRole_SYSTEM_ADMIN_duplicate_SYSTEM_ADMIN_is_normal_duplicate_rejection()
    {
        var sut = CreateSut(isSystemAdmin: true);
        var result = await sut.AddRoleAsync(new Role { RoleCode = "SYSTEM_ADMIN", RoleName = "Elevated", IsActive = true });

        Assert.False(result.Succeeded);
        Assert.Equal("Role code already exists.", result.ErrorMessage);
    }

    [Fact]
    public async Task UpdateRole_ADMIN_editing_SYSTEM_ADMIN_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false);
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 2, RoleCode = "SYSTEM_ADMIN", RoleName = "Renamed", IsActive = true });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot modify the SYSTEM_ADMIN role.", result.ErrorMessage);
    }

    [Fact]
    public async Task UpdateRole_ADMIN_deactivating_SYSTEM_ADMIN_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false);
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 2, RoleCode = "SYSTEM_ADMIN", RoleName = "System Admin", IsActive = false });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot modify the SYSTEM_ADMIN role.", result.ErrorMessage);
    }

    [Fact]
    public async Task UpdateRole_SYSTEM_ADMIN_deactivating_SYSTEM_ADMIN_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: true);
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 2, RoleCode = "SYSTEM_ADMIN", RoleName = "System Admin", IsActive = false });

        Assert.False(result.Succeeded);
        Assert.Equal("The ADMIN and SYSTEM_ADMIN roles cannot be deactivated.", result.ErrorMessage);
        var role = await GetRoleAsync(2);
        Assert.True(role!.IsActive);
    }

    [Fact]
    public async Task UpdateRole_ADMIN_deactivating_ADMIN_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false);
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 1, RoleCode = "ADMIN", RoleName = "Administrator", IsActive = false });

        Assert.False(result.Succeeded);
        Assert.Equal("The ADMIN and SYSTEM_ADMIN roles cannot be deactivated.", result.ErrorMessage);
        var role = await GetRoleAsync(1);
        Assert.True(role!.IsActive);
    }

    [Fact]
    public async Task UpdateRole_SYSTEM_ADMIN_deactivating_ADMIN_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: true);
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 1, RoleCode = "ADMIN", RoleName = "Administrator", IsActive = false });

        Assert.False(result.Succeeded);
        Assert.Equal("The ADMIN and SYSTEM_ADMIN roles cannot be deactivated.", result.ErrorMessage);
        var role = await GetRoleAsync(1);
        Assert.True(role!.IsActive);
    }

    [Fact]
    public async Task DeleteRoles_ADMIN_role_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false);
        var result = await sut.DeleteRolesAsync(new[] { 1 });

        Assert.False(result.Succeeded);
        Assert.Equal("The ADMIN and SYSTEM_ADMIN roles cannot be deleted.", result.ErrorMessage);
        var role = await GetRoleAsync(1);
        Assert.NotNull(role);
    }

    [Fact]
    public async Task DeleteRoles_SYSTEM_ADMIN_role_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: true);
        var result = await sut.DeleteRolesAsync(new[] { 2 });

        Assert.False(result.Succeeded);
        Assert.Equal("The ADMIN and SYSTEM_ADMIN roles cannot be deleted.", result.ErrorMessage);
        var role = await GetRoleAsync(2);
        Assert.NotNull(role);
    }

    [Fact]
    public async Task AddRole_case_variant_SYSTEM_ADMIN_by_ADMIN_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false);
        var result = await sut.AddRoleAsync(new Role { RoleCode = "system_admin", RoleName = "Elevated", IsActive = true });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot create the SYSTEM_ADMIN role.", result.ErrorMessage);
    }

    [Fact]
    public async Task UpdateRole_case_variant_SYSTEM_ADMIN_by_ADMIN_is_denied()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var mixed = await db.Roles.FirstAsync(x => x.RoleId == 2);
        mixed.RoleCode = "System_Admin";
        await db.SaveChangesAsync();

        var sut = CreateSut(isSystemAdmin: false);
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 2, RoleCode = "System_Admin", RoleName = "Renamed", IsActive = true });

        Assert.False(result.Succeeded);
        Assert.Equal("You cannot modify the SYSTEM_ADMIN role.", result.ErrorMessage);
    }

    [Fact]
    public async Task UpdateRole_cross_company_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false, company: "DEMO");
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 4, RoleCode = "ADMIN", RoleName = "Hijack", IsActive = true });

        Assert.False(result.Succeeded);
        Assert.Equal("Role not found.", result.ErrorMessage);
    }

    [Fact]
    public async Task DeleteRoles_cross_company_is_denied()
    {
        var sut = CreateSut(isSystemAdmin: false, company: "DEMO");
        var result = await sut.DeleteRolesAsync(new[] { 4 });

        Assert.False(result.Succeeded);
        Assert.Equal("Unable to delete role(s).", result.ErrorMessage);
        var role = await GetRoleAsync(4);
        Assert.NotNull(role);
        Assert.True(role!.IsActive);
    }

    [Fact]
    public async Task Custom_role_create_edit_deactivate_continues_to_work()
    {
        var sut = CreateSut(isSystemAdmin: false);

        var add = await sut.AddRoleAsync(new Role { RoleCode = "AUDITOR", RoleName = "Auditor", IsActive = true });
        Assert.True(add.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var created = await db.Roles.AsNoTracking().FirstAsync(x => x.CompanyCode == "DEMO" && x.RoleCode == "AUDITOR");
        var createdId = created.RoleId;

        var edit = await sut.UpdateRoleAsync(new Role { RoleId = createdId, RoleCode = "AUDITOR", RoleName = "Auditor Renamed", IsActive = true });
        Assert.True(edit.Succeeded);
        var renamed = await GetRoleAsync(createdId);
        Assert.Equal("Auditor Renamed", renamed!.RoleName);

        var deactivate = await sut.UpdateRoleAsync(new Role { RoleId = createdId, RoleCode = "AUDITOR", RoleName = "Auditor Renamed", IsActive = false });
        Assert.True(deactivate.Succeeded);
        var deactivated = await GetRoleAsync(createdId);
        Assert.False(deactivated!.IsActive);
    }

    [Fact]
    public async Task SYSTEM_ADMIN_can_edit_SYSTEM_ADMIN_descriptive_name()
    {
        var sut = CreateSut(isSystemAdmin: true);
        var result = await sut.UpdateRoleAsync(new Role { RoleId = 2, RoleCode = "SYSTEM_ADMIN", RoleName = "Platform Administrator", IsActive = true });

        Assert.True(result.Succeeded);
        var role = await GetRoleAsync(2);
        Assert.Equal("Platform Administrator", role!.RoleName);
        Assert.True(role.IsActive);
    }
}
