using ErpWeb.Core.Security;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class UserLoginRepositorySelfPasswordChangeTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly UserLoginRepository _repository;

    public UserLoginRepositorySelfPasswordChangeTests()
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
            User(1, "DEMO", "clerk", PasswordHasher.Hash("Clerk@12"), active: true, changepass: false),
            User(2, "DEMO", "inactive", PasswordHasher.Hash("Inactive@1"), active: false, changepass: false),
            User(3, "DEMO", "deleted", PasswordHasher.Hash("Deleted@1"), active: true, changepass: false, updatedUid: "deleted"),
            User(4, "OTH", "other", PasswordHasher.Hash("Other@12"), active: true, changepass: false));
        db.SaveChanges();

        _repository = new UserLoginRepository(_factory);
    }

    private static UserLogin User(
        int uid,
        string company,
        string id,
        string hash,
        bool active,
        bool changepass,
        string? updatedUid = null) =>
        new()
        {
            uid = uid,
            id = id,
            name = id,
            password = hash,
            CompanyCode = company,
            BranchCode = "HQ",
            LocationCode = "MAIN",
            UserID = id,
            active = active,
            changepass = changepass,
            UpdatedUID = updatedUid
        };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task<UserLogin?> GetAsync(int uid)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserLogins.AsNoTracking().FirstOrDefaultAsync(x => x.uid == uid);
    }

    [Fact]
    public async Task GetActiveByUidAsync_excludes_inactive_deleted_and_cross_company()
    {
        Assert.Null(await _repository.GetActiveByUidAsync(2, "DEMO")); // inactive
        Assert.Null(await _repository.GetActiveByUidAsync(3, "DEMO")); // deleted
        Assert.Null(await _repository.GetActiveByUidAsync(4, "DEMO")); // other company
        Assert.Null(await _repository.GetActiveByUidAsync(1, "OTH")); // wrong company scope

        var target = await _repository.GetActiveByUidAsync(1, "DEMO");
        Assert.NotNull(target);
        Assert.Equal("clerk", target!.id);
    }

    [Fact]
    public async Task TryChangeOwnPasswordAsync_succeeds_and_clears_changepass()
    {
        var before = await GetAsync(1);
        var oldHash = before!.password;

        var ok = await _repository.TryChangeOwnPasswordAsync(
            1, "DEMO", oldHash, before.changepass, PasswordHasher.Hash("NewPass99"), "clerk");

        Assert.True(ok);
        var after = await GetAsync(1);
        Assert.NotEqual(oldHash, after!.password);
        Assert.False(after.changepass);
        Assert.Equal("clerk", after.UpdatedUID);
        Assert.NotNull(after.Updated);
    }

    [Fact]
    public async Task TryChangeOwnPasswordAsync_retires_outstanding_email_reset_links()
    {
        var before = await GetAsync(1);
        var token = new PasswordResetToken
        {
            UserUid = 1,
            CompanyCode = "DEMO",
            TokenHash = PasswordResetTokens.Hash("self-change-raw-token"),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PasswordResetTokens.Add(token);
            await db.SaveChangesAsync();
        }

        var ok = await _repository.TryChangeOwnPasswordAsync(
            1, "DEMO", before!.password, before.changepass, PasswordHasher.Hash("NewPass99"), "clerk");

        Assert.True(ok);

        await using var check = await _factory.CreateDbContextAsync();
        var stored = await check.PasswordResetTokens.SingleAsync(t => t.PasswordResetTokenId == token.PasswordResetTokenId);
        Assert.NotNull(stored.UsedAtUtc);
    }

    [Fact]
    public async Task TryChangeOwnPasswordAsync_returns_false_when_hash_changed()
    {
        var before = await GetAsync(1);
        var originalHash = before!.password;

        // Predicate expects a different hash than what is actually stored.
        var ok = await _repository.TryChangeOwnPasswordAsync(
            1, "DEMO", PasswordHasher.Hash("WrongOld"), before.changepass, PasswordHasher.Hash("NewPass99"), "clerk");

        Assert.False(ok);
        var after = await GetAsync(1);
        Assert.Equal(originalHash, after!.password);
        Assert.False(after.changepass);
    }

    [Fact]
    public async Task TryChangeOwnPasswordAsync_returns_false_when_changepass_changed()
    {
        var before = await GetAsync(1);
        var originalHash = before!.password;

        // Predicate expects changepass=true but stored value is false.
        var ok = await _repository.TryChangeOwnPasswordAsync(
            1, "DEMO", originalHash, true, PasswordHasher.Hash("NewPass99"), "clerk");

        Assert.False(ok);
        var after = await GetAsync(1);
        Assert.Equal(originalHash, after!.password);
        Assert.False(after.changepass);
    }

    [Fact]
    public async Task TryChangeOwnPasswordAsync_returns_false_for_inactive_deleted_and_cross_company()
    {
        Assert.False(await _repository.TryChangeOwnPasswordAsync(
            2, "DEMO", PasswordHasher.Hash("Inactive@1"), false, PasswordHasher.Hash("X"), "clerk"));
        Assert.False(await _repository.TryChangeOwnPasswordAsync(
            3, "DEMO", PasswordHasher.Hash("Deleted@1"), false, PasswordHasher.Hash("X"), "clerk"));
        Assert.False(await _repository.TryChangeOwnPasswordAsync(
            4, "DEMO", PasswordHasher.Hash("Other@12"), false, PasswordHasher.Hash("X"), "clerk"));
        Assert.False(await _repository.TryChangeOwnPasswordAsync(
            1, "OTH", PasswordHasher.Hash("Clerk@12"), false, PasswordHasher.Hash("X"), "clerk"));

        var after = await GetAsync(1);
        Assert.NotEqual("X", after!.password);
    }

    [Fact]
    public async Task Failed_cas_leaves_newer_state_untouched()
    {
        var before = await GetAsync(1);
        var originalHash = before!.password;

        // Simulate a concurrent force-change: flip changepass to true after the read.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.UserLogins.FirstAsync(x => x.uid == 1);
            row.changepass = true;
            await db.SaveChangesAsync();
        }

        // CAS with the stale changepass=false predicate must not overwrite the newer state.
        var ok = await _repository.TryChangeOwnPasswordAsync(
            1, "DEMO", originalHash, false, PasswordHasher.Hash("NewPass99"), "clerk");

        Assert.False(ok);
        var after = await GetAsync(1);
        Assert.Equal(originalHash, after!.password); // password untouched
        Assert.True(after.changepass); // newer changepass preserved
    }
}
