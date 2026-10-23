using ErpWeb.Core.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class PasswordRecoveryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PasswordRecoveryTests()
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

    private PasswordRecoveryService CreateSut() =>
        new(_factory, Mock.Of<ISecurityAuditWriter>(), NullLogger<PasswordRecoveryService>.Instance);

    private static UserLogin User(string loginId, bool active = true, bool? deleted = false) => new()
    {
        uid = 1,
        id = loginId,
        name = "Clerk",
        password = "hash",
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        LocationCode = "MAIN",
        active = active,
        userlevel = "USER",
        UpdatedUID = deleted == true ? "deleted" : null,
        changepass = false,
        CanViewPrice = false
    };

    private async Task SeedAsync(UserLogin user)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.UserLogins.Add(user);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Request_creates_pending_for_active_user()
    {
        await SeedAsync(User("clerk"));
        var sut = CreateSut();

        await sut.RequestResetAsync("demo", "clerk");

        await using var db = await _factory.CreateDbContextAsync();
        var request = await db.PasswordResetRequests.SingleAsync();
        Assert.Equal("PENDING", request.Status);
        Assert.Equal(1, request.UserUid);
        Assert.Equal("DEMO", request.CompanyCode);
    }

    [Fact]
    public async Task Request_is_noop_for_unknown_user()
    {
        await SeedAsync(User("clerk"));
        var sut = CreateSut();

        await sut.RequestResetAsync("DEMO", "nobody");

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.PasswordResetRequests.ToListAsync());
    }

    [Fact]
    public async Task Request_is_noop_for_inactive_user()
    {
        await SeedAsync(User("clerk", active: false));
        var sut = CreateSut();

        await sut.RequestResetAsync("DEMO", "clerk");

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.PasswordResetRequests.ToListAsync());
    }

    [Fact]
    public async Task Request_is_noop_for_deleted_user()
    {
        await SeedAsync(User("clerk", deleted: true));
        var sut = CreateSut();

        await sut.RequestResetAsync("DEMO", "clerk");

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.PasswordResetRequests.ToListAsync());
    }

    [Fact]
    public async Task Duplicate_request_stays_single_pending()
    {
        await SeedAsync(User("clerk"));
        var sut = CreateSut();

        await sut.RequestResetAsync("DEMO", "clerk");
        await sut.RequestResetAsync("DEMO", "clerk");

        await using var db = await _factory.CreateDbContextAsync();
        var pending = await db.PasswordResetRequests.Where(r => r.Status == "PENDING").ToListAsync();
        Assert.Single(pending);
    }

    [Fact]
    public async Task ListPending_is_company_scoped()
    {
        var clerk = User("clerk");
        await SeedAsync(clerk);

        int otherUid;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var other = new UserLogin
            {
                id = "other",
                name = "Other",
                password = "hash",
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                active = true,
                userlevel = "USER",
                changepass = false,
                CanViewPrice = false
            };
            db.UserLogins.Add(other);
            db.PasswordResetRequests.Add(new PasswordResetRequest
            {
                UserUid = clerk.uid,
                CompanyCode = "DEMO",
                Status = "PENDING",
                RequestedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            otherUid = other.uid;
        }

        await using (var db2 = await _factory.CreateDbContextAsync())
        {
            db2.PasswordResetRequests.Add(new PasswordResetRequest
            {
                UserUid = otherUid,
                CompanyCode = "OTHER",
                Status = "PENDING",
                RequestedAtUtc = DateTime.UtcNow
            });
            await db2.SaveChangesAsync();
        }

        var sut = CreateSut();
        var rows = await sut.ListPendingAsync("DEMO");

        Assert.Single(rows);
        Assert.Equal("clerk", rows[0].LoginId);
    }

    [Fact]
    public async Task Cancel_marks_cancelled()
    {
        await SeedAsync(User("clerk"));
        long id;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var request = new PasswordResetRequest { UserUid = 1, CompanyCode = "DEMO", Status = "PENDING", RequestedAtUtc = DateTime.UtcNow };
            db.PasswordResetRequests.Add(request);
            await db.SaveChangesAsync();
            id = request.PasswordResetRequestId;
        }

        var sut = CreateSut();
        var result = await sut.CancelAsync(id, "DEMO", "admin", 99);

        Assert.True(result.Succeeded);
        await using var db2 = await _factory.CreateDbContextAsync();
        var row = await db2.PasswordResetRequests.SingleAsync();
        Assert.Equal("CANCELLED", row.Status);
        Assert.NotNull(row.ResolvedAtUtc);
    }

    [Fact]
    public async Task CompletePending_marks_completed()
    {
        await SeedAsync(User("clerk"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PasswordResetRequests.Add(new PasswordResetRequest { UserUid = 1, CompanyCode = "DEMO", Status = "PENDING", RequestedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.CompletePendingAsync(1, "DEMO", "admin", 99);

        Assert.True(result.Succeeded);
        await using var db2 = await _factory.CreateDbContextAsync();
        var row = await db2.PasswordResetRequests.SingleAsync();
        Assert.Equal("COMPLETED", row.Status);
        Assert.Equal("admin", row.ResolvedBy);
    }
}
