using System.Reflection;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

public class UserLoginRepositoryPasswordAdminTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly UserLoginRepository _repository;

    public UserLoginRepositoryPasswordAdminTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        db.UserLogins.AddRange(
            new UserLogin
            {
                uid = 1,
                id = "admin",
                name = "Admin",
                password = PasswordHasher.Hash("Demo@123"),
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                UserID = "admin",
                active = true,
                changepass = false,
                userlevel = "ADMIN"
            },
            new UserLogin
            {
                uid = 2,
                id = "clerk",
                name = "Clerk",
                password = PasswordHasher.Hash("Clerk@12"),
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                UserID = "clerk",
                active = true,
                changepass = false,
                userlevel = "USER"
            },
            new UserLogin
            {
                uid = 3,
                id = "other",
                name = "Other Co",
                password = PasswordHasher.Hash("Other@12"),
                CompanyCode = "OTH",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                UserID = "other",
                active = true,
                changepass = false,
                userlevel = "ADMIN"
            });
        db.SaveChanges();

        _repository = new UserLoginRepository(_factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public void PasswordAdminUserRow_has_no_password_properties()
    {
        var names = typeof(PasswordAdminUserRow)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Password", names);
        Assert.DoesNotContain("PasswordHash", names);
        Assert.Contains("Uid", names);
        Assert.Contains("LoginId", names);
        Assert.Contains("Name", names);
        Assert.Contains("UserId", names);
        Assert.Contains("Active", names);
        Assert.Contains("ChangePass", names);
    }

    [Fact]
    public async Task ListByCompanyAsync_returns_only_company_users_without_password_fields()
    {
        var rows = await _repository.ListByCompanyAsync("DEMO");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.LoginId)));
        Assert.DoesNotContain(rows, r => r.LoginId == "other");
    }

    [Fact]
    public async Task ResetPasswordAsync_updates_matching_company_and_sets_changepass()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var ok = await _repository.ResetPasswordAsync(2, "DEMO", PasswordHasher.Hash("TempPass1"), "admin");
        Assert.True(ok);

        var after = await GetEntityAsync(2);
        Assert.NotNull(after);
        Assert.NotEqual(oldHash, after.password);
        Assert.True(after.changepass);
        Assert.Equal("admin", after.UpdatedUID);
        Assert.NotNull(after.Updated);
    }

    [Fact]
    public async Task ResetPasswordAsync_fails_for_wrong_company()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var ok = await _repository.ResetPasswordAsync(2, "OTH", PasswordHasher.Hash("TempPass1"), "admin");
        Assert.False(ok);

        var after = await GetEntityAsync(2);
        Assert.Equal(oldHash, after!.password);
        Assert.False(after.changepass);
    }

    [Fact]
    public async Task SetChangePassAsync_does_not_change_password_hash()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var ok = await _repository.SetChangePassAsync(2, "DEMO", true, "admin");
        Assert.True(ok);

        var after = await GetEntityAsync(2);
        Assert.Equal(oldHash, after!.password);
        Assert.True(after.changepass);
        Assert.Equal("admin", after.UpdatedUID);
    }

    [Fact]
    public async Task SetChangePassAsync_fails_for_wrong_company()
    {
        var ok = await _repository.SetChangePassAsync(2, "OTH", true, "admin");
        Assert.False(ok);

        var after = await GetEntityAsync(2);
        Assert.False(after!.changepass);
    }

    private async Task<UserLogin?> GetEntityAsync(int uid)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserLogins.AsNoTracking().FirstOrDefaultAsync(x => x.uid == uid);
    }
}
