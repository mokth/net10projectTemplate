using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.Tests.Admin.Master;

/// <summary>
/// Real SQL Server race tests for the Phase 7 admin password reset and force-change compare-and-set guards.
///
/// <para>
/// Skipped unless <c>ConnectionStrings:SqlServerTestConnection</c> points at a scratch database whose name
/// contains "test". <c>ERPWEB_REQUIRE_SQLSERVER_TESTS=1</c> turns the silent skip into a failure, so a green
/// suite cannot hide that these never ran.
/// </para>
/// </summary>
[Trait(TestCategories.Name, TestCategories.Admin)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public class UserAdminPasswordSqlServerConcurrencyTests : IAsyncLifetime
{
    internal const string TestConnectionKey = "SqlServerTestConnection";

    private const string Company = "DEMO";

    private string? _connectionString;
    private IDbContextFactory<AppDbContext>? _factory;

    private static bool RequireSqlServer =>
        string.Equals(
            Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS"),
            "1",
            StringComparison.Ordinal);

    public async Task InitializeAsync()
    {
        _connectionString = GetSqlServerConnectionString();
        if (_connectionString is null || !IsScratchDatabase(_connectionString))
        {
            return;   // skip: no scratch SQL Server configured
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options);

        try
        {
            await using var db = await factory.CreateDbContextAsync();
            if (!db.Database.IsSqlServer())
            {
                return;
            }

            await db.Database.EnsureCreatedAsync();
            await CleanupAsync(db);
        }
        catch when (!RequireSqlServer)
        {
            return;   // an unusable scratch server is a skip, not a failure (unless the rail is on)
        }

        _factory = factory;
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync();
        await CleanupAsync(db);
    }

    // ── the cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_vs_reset_exactly_one_wins()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var repo = new UserLoginRepository(_factory!);
        var uid = await SeedTargetAsync("race1", "USER");
        var initial = await repo.GetAdminSecurityAsync(uid, Company);
        Assert.NotNull(initial);

        // Two admins both reset based on the same inspected security state.
        var first = repo.TryResetPasswordAsync(uid, Company, initial!.PasswordHash, initial.ChangePass, PasswordHasher.Hash("WinnerA1"), "admin-a", null);
        var second = repo.TryResetPasswordAsync(uid, Company, initial.PasswordHash, initial.ChangePass, PasswordHasher.Hash("WinnerB1"), "admin-b", null);

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.Succeeded);
        var conflict = Assert.Single(results, r => !r.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, conflict.Status);

        var after = await repo.GetAdminSecurityAsync(uid, Company);
        Assert.NotNull(after);
        var matchesA = PasswordHasher.Verify("WinnerA1", after!.PasswordHash);
        var matchesB = PasswordHasher.Verify("WinnerB1", after.PasswordHash);
        Assert.True(matchesA ^ matchesB);   // exactly one winner's hash is stored
        Assert.True(after.ChangePass);      // the winning reset still sets changepass = true
    }

    [Fact]
    public async Task Self_change_vs_reset_exactly_one_wins()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var repo = new UserLoginRepository(_factory!);
        var uid = await SeedTargetAsync("race2", "USER");
        var initial = await repo.GetAdminSecurityAsync(uid, Company);
        Assert.NotNull(initial);

        var self = repo.TryChangeOwnPasswordAsync(uid, Company, initial!.PasswordHash, initial.ChangePass, PasswordHasher.Hash("SelfPass1"), "self", default);
        var reset = repo.TryResetPasswordAsync(uid, Company, initial.PasswordHash, initial.ChangePass, PasswordHasher.Hash("ResetPass1"), "admin", null);

        var selfOk = await self;
        var resetResult = await reset;

        Assert.True(selfOk ^ resetResult.Succeeded);
        if (!resetResult.Succeeded)
        {
            Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, resetResult.Status);
        }

        var after = await repo.GetAdminSecurityAsync(uid, Company);
        Assert.NotNull(after);
        var matchesSelf = PasswordHasher.Verify("SelfPass1", after!.PasswordHash);
        var matchesReset = PasswordHasher.Verify("ResetPass1", after.PasswordHash);
        Assert.True(matchesSelf ^ matchesReset);
        Assert.Equal(resetResult.Succeeded, after.ChangePass); // reset -> true, self-change -> false
    }

    [Fact]
    public async Task Stale_force_clear_cannot_defeat_concurrent_reset()
    {
        if (SkipWithoutSqlServer())
        {
            return;
        }

        var repo = new UserLoginRepository(_factory!);
        // Target already has changepass = true, so a force-clear is a genuine state change.
        var uid = await SeedTargetAsync("race3", "USER", changePass: true);
        var initial = await repo.GetAdminSecurityAsync(uid, Company);
        Assert.NotNull(initial);

        var reset = repo.TryResetPasswordAsync(uid, Company, initial!.PasswordHash, initial.ChangePass, PasswordHasher.Hash("ResetPass1"), "admin", null);
        var clear = repo.TrySetChangePassAsync(uid, Company, initial.PasswordHash, initial.ChangePass, false, "admin", null);

        var resetResult = await reset;
        var clearResult = await clear;

        Assert.True(resetResult.Succeeded ^ clearResult.Succeeded);

        var after = await repo.GetAdminSecurityAsync(uid, Company);
        Assert.NotNull(after);
        if (resetResult.Succeeded)
        {
            // The winning reset's changepass = true must survive; the stale force-clear lost the race.
            Assert.True(after!.ChangePass);
            Assert.True(PasswordHasher.Verify("ResetPass1", after.PasswordHash));
        }
        else
        {
            // The force-clear won first, so the stale reset must have conflicted rather than overwriting.
            Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, resetResult.Status);
            Assert.False(after!.ChangePass);
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────

    /// <summary>Returns true when the test must bail out (no usable scratch SQL Server).</summary>
    private bool SkipWithoutSqlServer()
    {
        if (_factory is not null)
        {
            return false;
        }

        if (RequireSqlServer)
        {
            Assert.Fail(
                "The password admin concurrency suite requires a scratch SQL Server: set "
                + "ConnectionStrings:SqlServerTestConnection to a database whose name contains 'test'.");
        }

        return true;
    }

    private async Task<int> SeedTargetAsync(string loginId, string roleCode, bool changePass = false)
    {
        await using var db = await _factory!.CreateDbContextAsync();
        var user = new UserLogin
        {
            id = loginId,
            name = loginId,
            password = PasswordHasher.Hash("RacePass1"),
            CompanyCode = Company,
            BranchCode = "HQ",
            LocationCode = "MAIN",
            UserID = "admin",
            active = true,
            changepass = changePass,
            userlevel = roleCode,
            CanViewPrice = false
        };
        db.UserLogins.Add(user);
        await db.SaveChangesAsync();
        return user.uid;
    }

    private static async Task CleanupAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM dbo.userlogin WHERE CompanyCode = '{Company}';");
    }

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString(TestConnectionKey);
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    private static string? DatabaseNameOf(string connectionString)
    {
        foreach (var raw in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Split('=', 2);
            if (part.Length != 2)
            {
                continue;
            }

            var key = part[0].Trim();
            if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return part[1].Trim();
            }
        }

        return null;
    }

    private static bool IsScratchDatabase(string connectionString)
    {
        var name = DatabaseNameOf(connectionString);
        return name is not null && name.Contains("test", StringComparison.OrdinalIgnoreCase);
    }
}
