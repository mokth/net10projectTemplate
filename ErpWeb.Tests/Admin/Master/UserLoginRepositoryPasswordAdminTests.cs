using System.Reflection;
using ErpWeb.Core.Security;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Admin.Master;
[Trait(TestCategories.Name, TestCategories.Admin)]
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
                userlevel = "USER",
                CanViewPrice = true,
                ImagePath = "/img/clerk.png"
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
    public void UserAdminRow_has_no_password_properties()
    {
        var names = typeof(UserAdminRow)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Password", names);
        Assert.DoesNotContain("PasswordHash", names);
        Assert.Contains("Uid", names);
        Assert.Contains("LoginId", names);
        Assert.Contains("Name", names);
        Assert.Contains("Email", names);
        Assert.Contains("MobileNo", names);
        Assert.Contains("RoleCode", names);
        Assert.Contains("Active", names);
        Assert.Contains("ChangePass", names);
        Assert.Contains("CanViewPrice", names);
        Assert.Contains("Created", names);
        Assert.Contains("CreatedBy", names);
        Assert.Contains("Updated", names);
        Assert.Contains("UpdatedBy", names);
    }

    [Fact]
    public async Task ListForAdminAsync_returns_only_company_users_without_password_fields()
    {
        var rows = await _repository.ListForAdminAsync("DEMO");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.LoginId)));
        Assert.DoesNotContain(rows, r => r.LoginId == "other");
    }

    [Fact]
    public async Task TryResetPasswordAsync_updates_matching_company_and_sets_changepass()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var result = await _repository.TryResetPasswordAsync(
            2, "DEMO", oldHash, before.changepass, PasswordHasher.Hash("TempPass1"), "admin", null);

        Assert.True(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.Success, result.Status);

        var after = await GetEntityAsync(2);
        Assert.NotNull(after);
        Assert.NotEqual(oldHash, after.password);
        Assert.True(after.changepass);
        Assert.Equal("admin", after.UpdatedUID);
        Assert.NotNull(after.Updated);
    }

    [Fact]
    public async Task TryResetPasswordAsync_fails_for_wrong_company()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var result = await _repository.TryResetPasswordAsync(
            2, "OTH", oldHash, before.changepass, PasswordHasher.Hash("TempPass1"), "admin", null);

        Assert.False(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.NotFoundOrDeleted, result.Status);

        var after = await GetEntityAsync(2);
        Assert.Equal(oldHash, after!.password);
        Assert.False(after.changepass);
    }

    [Fact]
    public async Task TryResetPasswordAsync_completes_the_pending_recovery_request()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;
        var requestId = await SeedPendingRecoveryRequestAsync(2, "DEMO");

        var result = await _repository.TryResetPasswordAsync(
            2, "DEMO", oldHash, before.changepass, PasswordHasher.Hash("TempPass1"), "admin", null);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.CompletedRecoveryRequests);

        var request = await GetRecoveryRequestAsync(requestId);
        Assert.NotNull(request);
        Assert.Equal(PasswordResetRequestStatus.Completed, request!.Status);
        Assert.NotNull(request.ResolvedAtUtc);
        Assert.Equal("admin", request.ResolvedBy);
    }

    [Fact]
    public async Task TryResetPasswordAsync_conflict_never_completes_the_pending_request()
    {
        var before = await GetEntityAsync(2);
        var requestId = await SeedPendingRecoveryRequestAsync(2, "DEMO");

        // A stale expected hash makes the compare-and-set match 0 rows.
        var result = await _repository.TryResetPasswordAsync(
            2, "DEMO", "stale-hash", before!.changepass, PasswordHasher.Hash("TempPass1"), "admin", null);

        Assert.False(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, result.Status);
        Assert.Equal(0, result.CompletedRecoveryRequests);

        var request = await GetRecoveryRequestAsync(requestId);
        Assert.Equal(PasswordResetRequestStatus.Pending, request!.Status);
        Assert.Null(request.ResolvedAtUtc);
    }

    [Fact]
    public async Task TryResetPasswordAsync_retires_outstanding_email_reset_links()
    {
        var before = await GetEntityAsync(2);
        var token = new PasswordResetToken
        {
            UserUid = 2,
            CompanyCode = "DEMO",
            TokenHash = PasswordResetTokens.Hash("some-raw-token-value"),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PasswordResetTokens.Add(token);
            await db.SaveChangesAsync();
        }

        var result = await _repository.TryResetPasswordAsync(
            2, "DEMO", before!.password, before.changepass, PasswordHasher.Hash("TempPass1"), "admin", null);

        Assert.True(result.Succeeded);

        await using var check = await _factory.CreateDbContextAsync();
        var stored = await check.PasswordResetTokens.SingleAsync(t => t.PasswordResetTokenId == token.PasswordResetTokenId);
        Assert.NotNull(stored.UsedAtUtc);
    }

    [Fact]
    public async Task TryResetPasswordAsync_conflict_leaves_outstanding_email_reset_links_usable()
    {
        var before = await GetEntityAsync(2);
        var token = new PasswordResetToken
        {
            UserUid = 2,
            CompanyCode = "DEMO",
            TokenHash = PasswordResetTokens.Hash("another-raw-token-value"),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        };
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PasswordResetTokens.Add(token);
            await db.SaveChangesAsync();
        }

        var result = await _repository.TryResetPasswordAsync(
            2, "DEMO", "stale-hash", before!.changepass, PasswordHasher.Hash("TempPass1"), "admin", null);

        Assert.False(result.Succeeded);

        await using var check = await _factory.CreateDbContextAsync();
        var stored = await check.PasswordResetTokens.SingleAsync(t => t.PasswordResetTokenId == token.PasswordResetTokenId);
        Assert.Null(stored.UsedAtUtc);
    }

    private async Task<long> SeedPendingRecoveryRequestAsync(int uid, string companyCode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var request = new PasswordResetRequest
        {
            UserUid = uid,
            CompanyCode = companyCode,
            Status = PasswordResetRequestStatus.Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        db.PasswordResetRequests.Add(request);
        await db.SaveChangesAsync();
        return request.PasswordResetRequestId;
    }

    private async Task<PasswordResetRequest?> GetRecoveryRequestAsync(long requestId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PasswordResetRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PasswordResetRequestId == requestId);
    }

    [Fact]
    public async Task TrySetChangePassAsync_does_not_change_password_hash()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var result = await _repository.TrySetChangePassAsync(
            2, "DEMO", oldHash, before.changepass, true, "admin", null);

        Assert.True(result.Succeeded);

        var after = await GetEntityAsync(2);
        Assert.Equal(oldHash, after!.password);
        Assert.True(after.changepass);
        Assert.Equal("admin", after.UpdatedUID);
    }

    [Fact]
    public async Task TrySetChangePassAsync_fails_for_wrong_company()
    {
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var result = await _repository.TrySetChangePassAsync(
            2, "OTH", oldHash, before.changepass, true, "admin", null);

        Assert.False(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.NotFoundOrDeleted, result.Status);

        var after = await GetEntityAsync(2);
        Assert.False(after!.changepass);
    }

    [Fact]
    public async Task ListForAdminAsync_projects_grid_fields_and_excludes_soft_deleted()
    {
        // Soft-delete clerk (uid 2) in DEMO.
        var deleted = await _repository.SoftDeleteAsync(new[] { 2 }, "DEMO", "admin", null);
        Assert.Equal(1, deleted);

        var rows = await _repository.ListForAdminAsync("DEMO");

        Assert.Single(rows);
        var admin = rows[0];
        Assert.Equal("admin", admin.LoginId);
        Assert.Equal("ADMIN", admin.RoleCode);
        Assert.Equal("admin", admin.CreatedBy);
    }

    [Fact]
    public async Task UpdateProfileAsync_writes_CanViewPrice_and_preserves_uneditable_fields()
    {
        var before = await GetEntityAsync(2);
        var originalHash = before!.password;

        var ok = await _repository.UpdateProfileAsync(
            new EditUserRequest
            {
                Uid = 2,
                LoginId = "ignored",
                Name = "Clerk Renamed",
                Email = "clerk@example.com",
                MobileNo = "6011112222",
                Active = true,
                RoleCode = "MANAGER",
                CanViewPrice = false
            },
            "DEMO",
            "admin",
            null);

        Assert.True(ok);

        var after = await GetEntityAsync(2);
        Assert.NotNull(after);
        Assert.Equal(originalHash, after!.password);
        Assert.Equal("clerk", after.id);
        Assert.Equal("DEMO", after.CompanyCode);
        Assert.Equal("HQ", after.BranchCode);
        Assert.Equal("MAIN", after.LocationCode);
        Assert.Equal("/img/clerk.png", after.ImagePath);
        Assert.False(after.changepass);

        Assert.Equal("Clerk Renamed", after.name);
        Assert.Equal("clerk@example.com", after.email);
        Assert.Equal("6011112222", after.mobileno);
        Assert.True(after.active);
        Assert.Equal("MANAGER", after.userlevel);
        Assert.False(after.CanViewPrice);
        Assert.Equal("admin", after.UpdatedUID);
        Assert.NotNull(after.Updated);
    }

    [Fact]
    public async Task GetAdminRowAsync_excludes_soft_deleted()
    {
        await _repository.SoftDeleteAsync(new[] { 2 }, "DEMO", "admin", null);

        var row = await _repository.GetAdminRowAsync(2, "DEMO");

        Assert.Null(row);
    }

    [Fact]
    public async Task TryResetPasswordAsync_excludes_soft_deleted_target()
    {
        await _repository.SoftDeleteAsync(new[] { 2 }, "DEMO", "admin", null);
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var result = await _repository.TryResetPasswordAsync(
            2, "DEMO", oldHash, before.changepass, PasswordHasher.Hash("TempPass1"), "admin", null);

        Assert.False(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.NotFoundOrDeleted, result.Status);
        var after = await GetEntityAsync(2);
        Assert.Equal(oldHash, after!.password);
    }

    [Fact]
    public async Task TrySetChangePassAsync_excludes_soft_deleted_target()
    {
        await _repository.SoftDeleteAsync(new[] { 2 }, "DEMO", "admin", null);
        var before = await GetEntityAsync(2);
        var oldHash = before!.password;

        var result = await _repository.TrySetChangePassAsync(
            2, "DEMO", oldHash, before.changepass, true, "admin", null);

        Assert.False(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.NotFoundOrDeleted, result.Status);
        var after = await GetEntityAsync(2);
        Assert.False(after!.changepass);
    }

    [Fact]
    public async Task LoginExistsAsync_reserves_deleted_login_id()
    {
        await _repository.SoftDeleteAsync(new[] { 2 }, "DEMO", "admin", null);

        // The unique (id, CompanyCode) index still reserves "clerk" even after soft delete.
        var exists = await _repository.LoginExistsAsync("DEMO", "clerk");

        Assert.True(exists);
    }

    [Fact]
    public async Task SoftDeleteAsync_is_all_or_none_when_a_row_is_protected()
    {
        await SeedUserAsync(SystemAdminUser(9));

        var deleted = await _repository.SoftDeleteAsync(new[] { 2, 9 }, "DEMO", "admin", "SYSTEM_ADMIN");

        Assert.Equal(0, deleted);
        var clerk = await GetEntityAsync(2);
        Assert.NotNull(clerk);
        Assert.True(clerk!.active);
        Assert.NotEqual("deleted", clerk.UpdatedUID);
        var sysadmin = await GetEntityAsync(9);
        Assert.NotNull(sysadmin);
        Assert.True(sysadmin!.active);
    }

    [Fact]
    public async Task SoftDeleteAsync_is_all_or_none_when_a_row_is_cross_company()
    {
        var deleted = await _repository.SoftDeleteAsync(new[] { 2, 3 }, "DEMO", "admin", null);

        Assert.Equal(0, deleted);
        var clerk = await GetEntityAsync(2);
        Assert.NotNull(clerk);
        Assert.True(clerk!.active);
        Assert.NotEqual("deleted", clerk.UpdatedUID);
    }

    [Fact]
    public async Task TryResetPasswordAsync_denies_protected_role_target()
    {
        await SeedUserAsync(SystemAdminUser(9));
        var before = await GetEntityAsync(9);
        var oldHash = before!.password;

        var result = await _repository.TryResetPasswordAsync(
            9, "DEMO", oldHash, before.changepass, PasswordHasher.Hash("TempPass1"), "admin", "SYSTEM_ADMIN");

        Assert.False(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ProtectedTarget, result.Status);
        var after = await GetEntityAsync(9);
        Assert.Equal(oldHash, after!.password);
    }

    [Fact]
    public async Task UpdateProfileAsync_denies_protected_role_target()
    {
        await SeedUserAsync(SystemAdminUser(9));

        var ok = await _repository.UpdateProfileAsync(
            new EditUserRequest
            {
                Uid = 9,
                Name = "Renamed",
                RoleCode = "SYSTEM_ADMIN",
                Active = true
            },
            "DEMO",
            "admin",
            "SYSTEM_ADMIN");

        Assert.False(ok);
        var after = await GetEntityAsync(9);
        Assert.Equal("Sys Admin", after!.name);
    }

    // ── Phase 7: compare-and-set concurrency guards ─────────────────────────

    [Fact]
    public async Task TryResetPasswordAsync_conflict_when_hash_changed()
    {
        var before = await GetEntityAsync(2);
        var originalHash = before!.password;

        // A concurrent reset replaced the hash; our inspected hash is now stale.
        var newer = await _repository.TryResetPasswordAsync(
            2, "DEMO", originalHash, before.changepass, PasswordHasher.Hash("NewerPass1"), "other", null);
        Assert.True(newer.Succeeded);

        var stale = await _repository.TryResetPasswordAsync(
            2, "DEMO", originalHash, before.changepass, PasswordHasher.Hash("StalePass1"), "admin", null);

        Assert.False(stale.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, stale.Status);

        var after = await GetEntityAsync(2);
        Assert.NotEqual(originalHash, after!.password);          // newer hash preserved
        Assert.False(PasswordHasher.Verify("StalePass1", after.password)); // stale reset did not win
        Assert.True(PasswordHasher.Verify("NewerPass1", after.password));
    }

    [Fact]
    public async Task TryResetPasswordAsync_conflict_when_changepass_changed()
    {
        var before = await GetEntityAsync(2);
        var originalHash = before!.password;

        // A concurrent force-change flipped changepass; our inspected flag is now stale.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.UserLogins.FirstAsync(x => x.uid == 2);
            row.changepass = true;
            await db.SaveChangesAsync();
        }

        var stale = await _repository.TryResetPasswordAsync(
            2, "DEMO", originalHash, false, PasswordHasher.Hash("StalePass1"), "admin", null);

        Assert.False(stale.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, stale.Status);
        var after = await GetEntityAsync(2);
        Assert.Equal(originalHash, after!.password);
        Assert.True(after.changepass);
    }

    [Fact]
    public async Task Stale_force_clear_cannot_defeat_concurrent_reset()
    {
        var before = await GetEntityAsync(2);
        var originalHash = before!.password;

        // Another admin resets the password (new hash, changepass=true).
        var reset = await _repository.TryResetPasswordAsync(
            2, "DEMO", originalHash, before.changepass, PasswordHasher.Hash("ResetPass1"), "other", null);
        Assert.True(reset.Succeeded);

        // A stale force-clear based on the pre-reset state must not clear the newer requirement.
        var staleClear = await _repository.TrySetChangePassAsync(
            2, "DEMO", originalHash, before.changepass, false, "admin", null);

        Assert.False(staleClear.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, staleClear.Status);

        var after = await GetEntityAsync(2);
        Assert.True(after!.changepass);                          // newer reset requirement preserved
        Assert.False(PasswordHasher.Verify("Clerk@12", after.password));
        Assert.True(PasswordHasher.Verify("ResetPass1", after.password));
    }

    [Fact]
    public async Task TrySetChangePassAsync_conflict_when_hash_changed()
    {
        var before = await GetEntityAsync(2);
        var originalHash = before!.password;

        var reset = await _repository.TryResetPasswordAsync(
            2, "DEMO", originalHash, before.changepass, PasswordHasher.Hash("ResetPass1"), "other", null);
        Assert.True(reset.Succeeded);

        var stale = await _repository.TrySetChangePassAsync(
            2, "DEMO", originalHash, before.changepass, true, "admin", null);

        Assert.False(stale.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ConcurrencyConflict, stale.Status);
        var after = await GetEntityAsync(2);
        Assert.True(after!.changepass);
    }

    [Fact]
    public async Task TrySetChangePassAsync_denies_protected_role_target()
    {
        await SeedUserAsync(SystemAdminUser(9));
        var before = await GetEntityAsync(9);

        var result = await _repository.TrySetChangePassAsync(
            9, "DEMO", before!.password, before.changepass, true, "admin", "SYSTEM_ADMIN");

        Assert.False(result.Succeeded);
        Assert.Equal(UserSecurityMutationStatus.ProtectedTarget, result.Status);
        var after = await GetEntityAsync(9);
        Assert.False(after!.changepass);
    }

    [Fact]
    public async Task GetAdminSecurityAsync_excludes_soft_deleted_and_cross_company()
    {
        await _repository.SoftDeleteAsync(new[] { 2 }, "DEMO", "admin", null);

        Assert.Null(await _repository.GetAdminSecurityAsync(2, "DEMO")); // soft-deleted
        Assert.Null(await _repository.GetAdminSecurityAsync(3, "DEMO")); // other company

        var target = await _repository.GetAdminSecurityAsync(1, "DEMO");
        Assert.NotNull(target);
        Assert.Equal("admin", target!.LoginId);
        Assert.Equal("ADMIN", target.RoleCode);
        Assert.False(string.IsNullOrWhiteSpace(target.PasswordHash));
    }

    private static UserLogin SystemAdminUser(int uid) => new()
    {
        uid = uid,
        id = "sysadmin",
        name = "Sys Admin",
        password = PasswordHasher.Hash("Sys@12345"),
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        LocationCode = "MAIN",
        UserID = "sysadmin",
        active = true,
        changepass = false,
        userlevel = "SYSTEM_ADMIN"
    };

    private async Task SeedUserAsync(UserLogin user)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.UserLogins.Add(user);
        await db.SaveChangesAsync();
    }

    private async Task<UserLogin?> GetEntityAsync(int uid)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.UserLogins.AsNoTracking().FirstOrDefaultAsync(x => x.uid == uid);
    }
}
