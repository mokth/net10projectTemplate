using ErpWeb.Core.Security;
using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class PasswordRecoveryTests : IAsyncLifetime
{
    private const string BaseUrl = "https://erp.example.test/erpweb";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CapturingEmailSender _email = new();

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

    private PasswordRecoveryService CreateSut(int tokenLifetimeMinutes = 60) =>
        new(
            _factory,
            Mock.Of<ISecurityAuditWriter>(),
            _email,
            new PasswordPolicy(Options.Create(new PasswordPolicyOptions())),
            Options.Create(new PasswordRecoveryOptions { TokenLifetimeMinutes = tokenLifetimeMinutes }),
            NullLogger<PasswordRecoveryService>.Instance);

    private static UserLogin User(string loginId, bool active = true, bool? deleted = false, string? email = null) => new()
    {
        uid = 1,
        id = loginId,
        name = "Clerk",
        password = PasswordHasher.Hash("Current@12"),
        email = email,
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

    private async Task RequestEmailResetAsync(PasswordRecoveryService sut) =>
        await sut.RequestResetAsync("DEMO", "clerk", BaseUrl);

    /// <summary>Pulls the raw token back out of the captured email, exactly as a user would click it.</summary>
    private static string ExtractToken(EmailMessage message)
    {
        const string marker = "token=";
        var index = message.TextBody.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "The reset email must contain a token link.");
        var start = index + marker.Length;
        var end = start;
        while (end < message.TextBody.Length && !char.IsWhiteSpace(message.TextBody[end]))
        {
            end++;
        }

        return message.TextBody[start..end];
    }

    private async Task<string> IssueTokenAsync(PasswordRecoveryService sut)
    {
        await RequestEmailResetAsync(sut);
        var message = Assert.Single(_email.Sent);
        return ExtractToken(message);
    }

    // ---------------------------------------------------------------- request

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

    // ------------------------------------------------------------ email issue

    [Fact]
    public async Task Request_without_email_does_not_send_and_does_not_issue_token()
    {
        await SeedAsync(User("clerk", email: null));
        var sut = CreateSut();

        await RequestEmailResetAsync(sut);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(_email.Sent);
        Assert.Empty(await db.PasswordResetTokens.ToListAsync());
        // The administrator fallback still exists.
        Assert.Single(await db.PasswordResetRequests.ToListAsync());
    }

    [Fact]
    public async Task Request_without_base_url_does_not_send()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();

        await sut.RequestResetAsync("DEMO", "clerk");

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Request_with_email_sends_link_and_stores_only_the_hash()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();

        await RequestEmailResetAsync(sut);

        var message = Assert.Single(_email.Sent);
        Assert.Equal("clerk@example.test", message.ToAddress);
        Assert.Contains("/erpweb/reset-password?token=", message.TextBody);

        var token = ExtractToken(message);
        Assert.True(PasswordResetTokens.LooksWellFormed(token));

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PasswordResetTokens.SingleAsync();
        Assert.Equal(PasswordResetTokens.Hash(token), row.TokenHash);
        // The raw token must never be persisted anywhere.
        Assert.DoesNotContain(token, row.TokenHash);
        Assert.Null(row.UsedAtUtc);
        Assert.True(row.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Request_never_puts_the_token_in_the_audit_details()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var audit = new CapturingAuditWriter();
        var sut = new PasswordRecoveryService(
            _factory,
            audit,
            _email,
            new PasswordPolicy(Options.Create(new PasswordPolicyOptions())),
            Options.Create(new PasswordRecoveryOptions()),
            NullLogger<PasswordRecoveryService>.Instance);

        await RequestEmailResetAsync(sut);

        var token = ExtractToken(Assert.Single(_email.Sent));
        Assert.All(audit.Events, e =>
        {
            Assert.DoesNotContain(token, e.Details ?? string.Empty);
            Assert.DoesNotContain(token, e.NewValue ?? string.Empty);
        });
    }

    /// <summary>
    /// The admin queue row is created once, but a repeat request must still email a fresh link —
    /// otherwise a user who lost the first email would be permanently stuck behind a dedup row.
    /// </summary>
    [Fact]
    public async Task Request_while_a_pending_request_exists_still_emails_a_link()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();

        await RequestEmailResetAsync(sut);
        await RequestEmailResetAsync(sut);

        Assert.Equal(2, _email.Sent.Count);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.PasswordResetRequests.ToListAsync());
        // Exactly one live token survives; the superseded one is marked used.
        Assert.Single(await db.PasswordResetTokens.Where(t => t.UsedAtUtc == null).ToListAsync());
        Assert.Equal(2, await db.PasswordResetTokens.CountAsync());
    }

    [Fact]
    public async Task Requesting_again_supersedes_the_previous_link()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();

        var firstToken = await IssueTokenAsync(sut);
        await RequestEmailResetAsync(sut);
        var secondToken = ExtractToken(_email.Sent[^1]);

        Assert.NotEqual(firstToken, secondToken);

        var stale = await sut.RedeemAsync(firstToken, "Brandnew@34");
        Assert.False(stale.Succeeded);
        Assert.Equal(PasswordResetRedeemStatus.InvalidToken, stale.Status);

        var fresh = await sut.RedeemAsync(secondToken, "Brandnew@34");
        Assert.True(fresh.Succeeded);
    }

    // --------------------------------------------------------------- redeem

    [Fact]
    public async Task Redeem_sets_the_password_and_consumes_the_token()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);

        var result = await sut.RedeemAsync(token, "Brandnew@34");

        Assert.True(result.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.UserLogins.SingleAsync();
        Assert.True(PasswordHasher.Verify("Brandnew@34", user.password));
        Assert.False(user.changepass);

        var row = await db.PasswordResetTokens.SingleAsync();
        Assert.NotNull(row.UsedAtUtc);
    }

    [Fact]
    public async Task Redeem_completes_the_admin_request_queue()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);

        await sut.RedeemAsync(token, "Brandnew@34");

        Assert.Empty(await sut.ListPendingAsync("DEMO"));
    }

    [Fact]
    public async Task Redeem_is_single_use()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);

        Assert.True((await sut.RedeemAsync(token, "Brandnew@34")).Succeeded);
        var replay = await sut.RedeemAsync(token, "Another@56");

        Assert.False(replay.Succeeded);
        Assert.Equal(PasswordResetRedeemStatus.InvalidToken, replay.Status);
    }

    [Fact]
    public async Task Redeem_rejects_an_expired_token()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.PasswordResetTokens.SingleAsync();
            row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var result = await sut.RedeemAsync(token, "Brandnew@34");

        Assert.False(result.Succeeded);
        Assert.Equal(PasswordResetRedeemStatus.InvalidToken, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("!!!!")]
    public async Task Redeem_rejects_a_malformed_token(string? token)
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();

        var result = await sut.RedeemAsync(token, "Brandnew@34");

        Assert.False(result.Succeeded);
        Assert.Equal(PasswordResetRedeemStatus.InvalidToken, result.Status);
    }

    [Fact]
    public async Task Redeem_rejects_a_well_formed_but_unknown_token()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();

        var result = await sut.RedeemAsync(PasswordResetTokens.CreateRawToken(), "Brandnew@34");

        Assert.False(result.Succeeded);
        Assert.Equal(PasswordResetRedeemStatus.InvalidToken, result.Status);
    }

    [Fact]
    public async Task Redeem_does_not_burn_the_token_when_the_password_fails_policy()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);

        var rejected = await sut.RedeemAsync(token, "short");
        Assert.Equal(PasswordResetRedeemStatus.PolicyRejected, rejected.Status);

        // The same link must still work once a compliant password is supplied.
        Assert.True((await sut.RedeemAsync(token, "Brandnew@34")).Succeeded);
    }

    [Fact]
    public async Task Redeem_rejects_the_current_password()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);

        var result = await sut.RedeemAsync(token, "Current@12");

        Assert.Equal(PasswordResetRedeemStatus.PolicyRejected, result.Status);
    }

    [Fact]
    public async Task Redeem_rejects_when_the_account_was_deactivated_after_the_link_was_sent()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var user = await db.UserLogins.SingleAsync();
            user.active = false;
            await db.SaveChangesAsync();
        }

        var result = await sut.RedeemAsync(token, "Brandnew@34");

        Assert.False(result.Succeeded);
        Assert.Equal(PasswordResetRedeemStatus.InvalidToken, result.Status);
    }

    [Fact]
    public async Task Redeem_clears_a_pending_forced_change()
    {
        await SeedAsync(User("clerk", email: "clerk@example.test"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var user = await db.UserLogins.SingleAsync();
            user.changepass = true;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var token = await IssueTokenAsync(sut);
        Assert.True((await sut.RedeemAsync(token, "Brandnew@34")).Succeeded);

        await using var db2 = await _factory.CreateDbContextAsync();
        Assert.False((await db2.UserLogins.SingleAsync()).changepass);
    }

    // ---------------------------------------------------------------- queue

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
}

[Trait(TestCategories.Name, TestCategories.Admin)]
public class PasswordResetTokenTests
{
    [Fact]
    public void Raw_tokens_are_unique_url_safe_and_have_the_expected_shape()
    {
        var first = PasswordResetTokens.CreateRawToken();
        var second = PasswordResetTokens.CreateRawToken();

        Assert.NotEqual(first, second);
        Assert.True(PasswordResetTokens.LooksWellFormed(first));
        Assert.True(PasswordResetTokens.LooksWellFormed(second));
        Assert.Equal(43, first.Length);
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void Hash_is_stable_hex_sha256_and_does_not_contain_the_token()
    {
        var token = PasswordResetTokens.CreateRawToken();
        var hash = PasswordResetTokens.Hash(token);

        Assert.Equal(hash, PasswordResetTokens.Hash(token));
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(token, hash);
        Assert.Matches("^[0-9A-F]{64}$", hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void Malformed_tokens_are_rejected_without_touching_the_database(string? token) =>
        Assert.False(PasswordResetTokens.LooksWellFormed(token));
}

internal sealed class CapturingEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class CapturingAuditWriter : ISecurityAuditWriter
{
    public List<SecurityAuditEvent> Events { get; } = [];

    public Task WriteAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
