using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpWeb.Tests;

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
            password = "x",
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "MAIN",
            active = true,
            userlevel = "USER"
        });
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Reconcile_creates_role_and_mapping_from_userlevel()
    {
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        await sut.ReconcileFromUserLevelAsync(1, "DEMO", "SALES");

        await using var db = await _factory.CreateDbContextAsync();
        var role = await db.Roles.SingleAsync(r => r.CompanyCode == "DEMO" && r.RoleCode == "SALES");
        Assert.True(await db.UserRoleMappings.AnyAsync(m => m.UserUid == 1 && m.RoleId == role.RoleId));
    }

    [Fact]
    public async Task Reconcile_replaces_same_company_role_when_userlevel_changes()
    {
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        await sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER");
        await sut.ReconcileFromUserLevelAsync(1, "DEMO", "ADMIN");

        await using var db = await _factory.CreateDbContextAsync();
        var mappings = await db.UserRoleMappings
            .Include(m => m.Role)
            .Where(m => m.UserUid == 1)
            .ToListAsync();
        Assert.Single(mappings);
        Assert.Equal("ADMIN", mappings[0].Role.RoleCode);
    }

    [Fact]
    public async Task Blank_userlevel_clears_company_mappings()
    {
        var sut = new UserRoleSyncService(_factory, NullLogger<UserRoleSyncService>.Instance);
        await sut.ReconcileFromUserLevelAsync(1, "DEMO", "USER");
        await sut.ReconcileFromUserLevelAsync(1, "DEMO", null);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.UserRoleMappings.Where(m => m.UserUid == 1).ToListAsync());
    }
}
