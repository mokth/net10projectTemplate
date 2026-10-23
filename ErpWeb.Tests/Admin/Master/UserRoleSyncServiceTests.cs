using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class UserRoleSyncServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public UserRoleSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        db.UserLogins.Add(new UserLogin
        {
            uid = 1,
            id = "clerk",
            name = "Clerk",
            password = PasswordHasher.Hash("Clerk@12"),
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = true,
            userlevel = "USER"
        });

        db.Roles.AddRange(
            new Role { RoleId = 1, CompanyCode = "DEMO", RoleCode = "ADMIN", RoleName = "Administrator", IsActive = true },
            new Role { RoleId = 2, CompanyCode = "DEMO", RoleCode = "USER", RoleName = "User", IsActive = true },
            new Role { RoleId = 3, CompanyCode = "DEMO", RoleCode = "INACTIVE_ROLE", RoleName = "Inactive", IsActive = false },
            new Role { RoleId = 4, CompanyCode = "OTH", RoleCode = "MANAGER", RoleName = "Manager", IsActive = true },
            new Role { RoleId = 5, CompanyCode = "OTH", RoleCode = "ADMIN", RoleName = "Admin", IsActive = true });

        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task SeedMappingAsync(int uid, int roleId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.UserRoleMappings.Add(new UserRoleMapping { UserUid = uid, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    private async Task<List<int>> GetMappingRoleIdsAsync(int uid)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserRoleMappings
            .Where(m => m.UserUid == uid)
            .Select(m => m.RoleId)
            .ToListAsync();
    }

    private async Task<int> CountRolesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Roles.CountAsync();
    }

    [Fact]
    public async Task Valid_existing_active_role_creates_mapping()
    {
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        var result = await sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER");

        Assert.Equal(UserRoleSyncStatus.Success, result.Status);
        Assert.True(result.Succeeded);

        var roleIds = await GetMappingRoleIdsAsync(1);
        Assert.Equal([2], roleIds);
    }

    [Fact]
    public async Task Missing_role_fails_and_role_table_is_unchanged()
    {
        var before = await CountRolesAsync();
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        var result = await sut.ReconcileFromUserLevelAsync(1, "DEMO", "SALES");

        Assert.Equal(UserRoleSyncStatus.RoleMissing, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(before, await CountRolesAsync());
        Assert.Empty(await GetMappingRoleIdsAsync(1));
    }

    [Fact]
    public async Task Inactive_role_fails()
    {
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        var result = await sut.ReconcileFromUserLevelAsync(1, "DEMO", "INACTIVE_ROLE");

        Assert.Equal(UserRoleSyncStatus.RoleInactive, result.Status);
        Assert.Empty(await GetMappingRoleIdsAsync(1));
    }

    [Fact]
    public async Task Blank_userlevel_fails_and_creates_no_mappings()
    {
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        var result = await sut.ReconcileFromUserLevelAsync(1, "DEMO", null);

        Assert.Equal(UserRoleSyncStatus.BlankRole, result.Status);
        Assert.Empty(await GetMappingRoleIdsAsync(1));
    }

    [Fact]
    public async Task Wrong_company_role_fails()
    {
        // "MANAGER" exists only under OTH, not DEMO.
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        var result = await sut.ReconcileFromUserLevelAsync(1, "DEMO", "MANAGER");

        Assert.Equal(UserRoleSyncStatus.WrongCompany, result.Status);
        Assert.Empty(await GetMappingRoleIdsAsync(1));
    }

    [Fact]
    public async Task Stale_same_company_mapping_is_replaced()
    {
        await SeedMappingAsync(1, 2); // DEMO/USER

        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        var result = await sut.ReconcileFromUserLevelAsync(1, "DEMO", "ADMIN");

        Assert.True(result.Succeeded);
        Assert.Equal([1], await GetMappingRoleIdsAsync(1));
    }

    [Fact]
    public async Task Correct_mapping_exists_exactly_once_after_repeat_reconcile()
    {
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);

        Assert.True((await sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER")).Succeeded);
        Assert.True((await sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER")).Succeeded);

        Assert.Equal([2], await GetMappingRoleIdsAsync(1));
    }

    [Fact]
    public async Task Other_company_mapping_is_not_deleted()
    {
        await SeedMappingAsync(1, 2); // DEMO/USER (correct)
        await SeedMappingAsync(1, 1); // DEMO/ADMIN (stale same-company)
        await SeedMappingAsync(1, 4); // OTH/MANAGER (other company — must survive)

        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        var result = await sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER");

        Assert.True(result.Succeeded);
        var roleIds = await GetMappingRoleIdsAsync(1);
        Assert.Contains(2, roleIds); // target kept
        Assert.Contains(4, roleIds); // other-company kept
        Assert.DoesNotContain(1, roleIds); // stale same-company removed
        Assert.Equal(2, roleIds.Count);
    }

    [Fact]
    public async Task No_role_row_is_ever_created_by_sync()
    {
        var before = await CountRolesAsync();
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);

        await sut.ReconcileFromUserLevelAsync(1, "DEMO", "SALES"); // missing
        await sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER"); // success

        Assert.Equal(before, await CountRolesAsync());
    }
}

[Trait(TestCategories.Name, TestCategories.Admin)]
public class UserRoleSyncConcurrencyTests
{
    [Fact]
    public async Task Simultaneous_reconciliation_does_not_create_duplicate_mappings()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"erpweb_sync_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={dbPath}";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connStr).Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

        try
        {
            using (var seed = factory.CreateDbContext())
            {
                seed.Database.EnsureCreated();
                seed.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                seed.Roles.Add(new Role { RoleId = 2, CompanyCode = "DEMO", RoleCode = "USER", RoleName = "User", IsActive = true });
                seed.UserLogins.Add(new UserLogin
                {
                    uid = 1,
                    id = "clerk",
                    name = "Clerk",
                    password = "x",
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    LocationCode = "MAIN",
                    active = true,
                    userlevel = "USER"
                });
                seed.SaveChanges();
            }

            var sut = new UserRoleSyncService(factory, NullLogger<UserRoleSyncService>.Instance);
            var results = await Task.WhenAll(
                Enumerable.Range(0, 2)
                    .Select(_ => sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER")));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Detail));

            using var db = factory.CreateDbContext();
            var count = db.UserRoleMappings.Count(m => m.UserUid == 1 && m.RoleId == 2);
            Assert.Equal(1, count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
