using ErpWeb.Library.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Security;

public sealed class PasswordResetRequestRow
{
    public required long PasswordResetRequestId { get; init; }
    public required int UserUid { get; init; }
    public required string LoginId { get; init; }
    public required string Name { get; init; }
    public required DateTime RequestedAtUtc { get; init; }
}

public enum PasswordRecoveryStatus
{
    Success = 1,
    NotFound = 2,
    NotPending = 3
}

public readonly record struct PasswordRecoveryResult(PasswordRecoveryStatus Status, string? Detail)
{
    public bool Succeeded => Status == PasswordRecoveryStatus.Success;

    public static PasswordRecoveryResult Ok() => new(PasswordRecoveryStatus.Success, null);

    public static PasswordRecoveryResult Fail(PasswordRecoveryStatus status, string? detail = null) =>
        new(status, detail);
}

public enum PasswordResetRedeemStatus
{
    Success = 1,

    /// <summary>Unknown, already used, expired, or the account is no longer eligible. Deliberately merged.</summary>
    InvalidToken = 2,

    /// <summary>The new password fails <see cref="IPasswordPolicy"/>; details are safe to show.</summary>
    PolicyRejected = 3,

    /// <summary>The row changed underneath us (concurrent reset). The token is spent; request a new link.</summary>
    Conflict = 4
}

public readonly record struct PasswordResetRedeemResult(
    PasswordResetRedeemStatus Status,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Status == PasswordResetRedeemStatus.Success;

    public static PasswordResetRedeemResult Ok() =>
        new(PasswordResetRedeemStatus.Success, Array.Empty<string>());

    public static PasswordResetRedeemResult Fail(
        PasswordResetRedeemStatus status,
        params string[] errors) =>
        new(status, errors);

    /// <summary>Overload for <see cref="PasswordValidationResult.Errors"/>, which is already a list.</summary>
    public static PasswordResetRedeemResult Fail(
        PasswordResetRedeemStatus status,
        IReadOnlyList<string> errors) =>
        new(status, errors);
}

/// <summary>
/// Password recovery. Two paths exist and are not exclusive:
///
/// <list type="number">
/// <item>A self-service email link: a single-use token is mailed to the address on the account and the
/// user chooses their own password. Preferred when SMTP is configured and the user has an email.</item>
/// <item>An admin-assisted request row that a company administrator resolves from User Accounts. This
/// is the fallback for users with no email address on file.</item>
/// </list>
///
/// <para>
/// Responses to the anonymous caller are always identical so account existence is never revealed.
/// </para>
/// </summary>
public interface IPasswordRecoveryService
{
    /// <summary>
    /// Records a recovery request and, when the account has an email address and mail is configured,
    /// emails a single-use reset link. Returns silently and identically for every input.
    /// </summary>
    /// <param name="resetLinkBaseUrl">
    /// Absolute base URL used to build the reset link (for example <c>https://host/erpweb</c>).
    /// When null or blank no email is sent — the request still reaches the administrator queue.
    /// </param>
    Task RequestResetAsync(
        string companyCode,
        string username,
        string? resetLinkBaseUrl = null,
        string? requestedIp = null,
        CancellationToken cancellationToken = default);

    /// <summary>Consumes an emailed token and sets the new password. Single-use and time-limited.</summary>
    Task<PasswordResetRedeemResult> RedeemAsync(
        string? token,
        string newPassword,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PasswordResetRequestRow>> ListPendingAsync(string companyCode, CancellationToken cancellationToken = default);

    Task<PasswordRecoveryResult> CancelAsync(
        long requestId,
        string companyCode,
        string actorUserId,
        int actorUid,
        CancellationToken cancellationToken = default);
}

public sealed class PasswordRecoveryService : IPasswordRecoveryService
{
    public const string Pending = PasswordResetRequestStatus.Pending;
    public const string Completed = PasswordResetRequestStatus.Completed;
    public const string Cancelled = PasswordResetRequestStatus.Cancelled;

    private const string DeletedMarker = "deleted";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISecurityAuditWriter _audit;
    private readonly IEmailSender _email;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly PasswordRecoveryOptions _options;
    private readonly ILogger<PasswordRecoveryService> _logger;

    public PasswordRecoveryService(
        IDbContextFactory<AppDbContext> dbFactory,
        ISecurityAuditWriter audit,
        IEmailSender email,
        IPasswordPolicy passwordPolicy,
        IOptions<PasswordRecoveryOptions> options,
        ILogger<PasswordRecoveryService> logger)
    {
        _dbFactory = dbFactory;
        _audit = audit;
        _email = email;
        _passwordPolicy = passwordPolicy;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RequestResetAsync(
        string companyCode,
        string username,
        string? resetLinkBaseUrl = null,
        string? requestedIp = null,
        CancellationToken cancellationToken = default)
    {
        companyCode = (companyCode ?? string.Empty).Trim().ToUpperInvariant();
        username = (username ?? string.Empty).Trim();

        if (companyCode.Length == 0 || username.Length == 0)
        {
            return;   // enumeration-safe: no-op
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var user = await db.UserLogins
            .AsNoTracking()
            .Where(x => x.id == username && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.active != true)
        {
            return;   // invalid / inactive / deleted -> identical generic outcome
        }

        await EnsurePendingRequestAsync(db, user, companyCode, cancellationToken);

        // The email path is additive: the administrator queue above stays available as the fallback
        // for accounts with no address on file, or when SMTP is unavailable.
        if (string.IsNullOrWhiteSpace(resetLinkBaseUrl) || string.IsNullOrWhiteSpace(user.email))
        {
            return;
        }

        await IssueEmailTokenAsync(db, user, companyCode, resetLinkBaseUrl!, requestedIp, cancellationToken);
    }

    private async Task EnsurePendingRequestAsync(
        AppDbContext db,
        UserLogin user,
        string companyCode,
        CancellationToken cancellationToken)
    {
        // Fast path: one pending request per user. The unique filtered index still guards the race
        // when two requests arrive together; the catch below absorbs that expected duplicate.
        var alreadyPending = await db.PasswordResetRequests
            .AnyAsync(r => r.UserUid == user.uid && r.Status == Pending, cancellationToken);
        if (alreadyPending)
        {
            return;
        }

        db.PasswordResetRequests.Add(new PasswordResetRequest
        {
            UserUid = user.uid,
            CompanyCode = companyCode,
            Status = Pending,
            RequestedAtUtc = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new SecurityAuditEvent
            {
                CompanyCode = companyCode,
                TargetUid = user.uid,
                EventType = SecurityAuditEventTypes.PasswordRecoveryRequested,
                Outcome = "SUCCESS",
                Details = "Password recovery request created."
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A pending request already exists for this user (unique filtered index). The race-safe
            // outcome is a single pending request; surface the same generic success as a fresh create.
            //
            // The failed insert is still tracked as Added at this point. Without detaching it, the
            // next SaveChanges on this context (the reset-token insert) would re-attempt the same
            // duplicate and fail again, silently suppressing the user's reset email.
            db.ChangeTracker.Clear();
            _logger.LogDebug(
                "Duplicate password reset request for user {Uid} company {CompanyCode} ignored",
                user.uid,
                companyCode);
        }
    }

    private async Task IssueEmailTokenAsync(
        AppDbContext db,
        UserLogin user,
        string companyCode,
        string resetLinkBaseUrl,
        string? requestedIp,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expiresAtUtc = now.Add(_options.TokenLifetime);
        var rawToken = PasswordResetTokens.CreateRawToken();

        // Only the newest link may work. Superseding outstanding tokens keeps a stolen older email
        // from remaining valid for the rest of its lifetime after the user asks again.
        await db.PasswordResetTokens
            .Where(t => t.UserUid == user.uid && t.UsedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, now), cancellationToken);

        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserUid = user.uid,
            CompanyCode = companyCode,
            TokenHash = PasswordResetTokens.Hash(rawToken),
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
            RequestedIp = string.IsNullOrWhiteSpace(requestedIp) ? null : Truncate(requestedIp, 45)
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not persist a password reset token for user {Uid}.", user.uid);
            return;
        }

        var link = BuildResetLink(resetLinkBaseUrl, rawToken);
        var lifetime = _options.TokenLifetimeMinutes;

        var textBody =
            $"A password reset was requested for the {companyCode} account '{user.id}'." + Environment.NewLine +
            Environment.NewLine +
            $"Open this link to choose a new password. It expires in {lifetime} minutes and can be used once:" +
            Environment.NewLine +
            Environment.NewLine +
            link + Environment.NewLine +
            Environment.NewLine +
            "If you did not request this, you can ignore this email. Your password will not change unless " +
            "the link above is used." + Environment.NewLine +
            Environment.NewLine +
            "Wincom ERP";

        var htmlBody =
            "<p>A password reset was requested for the <strong>" + Html(companyCode) + "</strong> account " +
            "<strong>" + Html(user.id) + "</strong>.</p>" +
            $"<p><a href=\"{Html(link)}\">Choose a new password</a></p>" +
            $"<p>This link expires in {lifetime} minutes and can be used once.</p>" +
            "<p>If you did not request this, you can ignore this email. Your password will not change unless " +
            "the link above is used.</p>" +
            "<p>Wincom ERP</p>";

        await _email.SendAsync(
            new EmailMessage(
                user.email!,
                "Reset your Wincom ERP password",
                textBody,
                htmlBody),
            cancellationToken);

        await _audit.WriteAsync(new SecurityAuditEvent
        {
            CompanyCode = companyCode,
            TargetUid = user.uid,
            EventType = SecurityAuditEventTypes.PasswordResetEmailIssued,
            Outcome = "SUCCESS",
            // Never record the token or the link.
            Details = "Password reset email issued."
        }, cancellationToken);
    }

    public async Task<PasswordResetRedeemResult> RedeemAsync(
        string? token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!PasswordResetTokens.LooksWellFormed(token))
        {
            return PasswordResetRedeemResult.Fail(
                PasswordResetRedeemStatus.InvalidToken,
                "This password reset link is not valid. Request a new one.");
        }

        var tokenHash = PasswordResetTokens.Hash(token!);
        var now = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.PasswordResetTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        // One message for unknown / spent / expired / ineligible so the response cannot be used to
        // probe whether a token ever existed.
        var invalid = PasswordResetRedeemResult.Fail(
            PasswordResetRedeemStatus.InvalidToken,
            "This password reset link is invalid or has expired. Request a new one.");

        if (row is null || row.UsedAtUtc is not null || row.ExpiresAtUtc <= now)
        {
            return invalid;
        }

        var user = await db.UserLogins
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.uid == row.UserUid && u.CompanyCode == row.CompanyCode, cancellationToken);

        if (user is null || user.active != true ||
            (user.UpdatedUID is not null && user.UpdatedUID == DeletedMarker))
        {
            return invalid;
        }

        // Validate BEFORE claiming the token: a rejected password must not burn the user's only link.
        var policy = _passwordPolicy.Validate(newPassword, user.id);
        if (!policy.IsValid)
        {
            return PasswordResetRedeemResult.Fail(PasswordResetRedeemStatus.PolicyRejected, policy.Errors);
        }

        if (PasswordHasher.Verify(newPassword, user.password))
        {
            return PasswordResetRedeemResult.Fail(
                PasswordResetRedeemStatus.PolicyRejected,
                "New password must be different from your current password.");
        }

        var newHash = PasswordHasher.Hash(newPassword);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Claim the token first. The conditional update is the serialization point: of N
            // concurrent redemptions of the same link exactly one updates a row here.
            var claimed = await db.PasswordResetTokens
                .Where(t => t.PasswordResetTokenId == row.PasswordResetTokenId)
                .Where(t => t.UsedAtUtc == null && t.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, now), cancellationToken);

            if (claimed == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return invalid;
            }

            // Guarded password write, mirroring UserLoginRepository.TryChangeOwnPasswordAsync: the
            // row must still be active, not soft-deleted, and carry the hash we validated against.
            // Unlike a self-service change this also clears any pending forced change, because the
            // user has just chosen their own password.
            var rows = await db.UserLogins
                .Where(u => u.uid == user.uid && u.CompanyCode == row.CompanyCode)
                .Where(u => u.active == true)
                .Where(u => u.UpdatedUID == null || u.UpdatedUID != DeletedMarker)
                .Where(u => u.password == user.password)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(u => u.password, newHash)
                        .SetProperty(u => u.changepass, false)
                        .SetProperty(u => u.Updated, now)
                        .SetProperty(u => u.UpdatedUID, user.id),
                    cancellationToken);

            if (rows == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return PasswordResetRedeemResult.Fail(
                    PasswordResetRedeemStatus.Conflict,
                    "Your password was changed by another session. Request a new reset link.");
            }

            // Any other outstanding link for this user is now dead as well.
            await db.PasswordResetTokens
                .Where(t => t.UserUid == user.uid && t.UsedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, now), cancellationToken);

            await db.PasswordResetRequests
                .Where(r => r.UserUid == user.uid && r.CompanyCode == row.CompanyCode && r.Status == Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.Status, Completed)
                        .SetProperty(r => r.ResolvedAtUtc, now)
                        .SetProperty(r => r.ResolvedBy, Truncate(user.id, 10)),
                    cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception)
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        await _audit.WriteAsync(new SecurityAuditEvent
        {
            CompanyCode = row.CompanyCode,
            TargetUid = user.uid,
            EventType = SecurityAuditEventTypes.PasswordResetEmailRedeemed,
            Outcome = "SUCCESS",
            Details = "Password reset completed with an emailed token."
        }, cancellationToken);

        _logger.LogInformation(
            "Password reset completed via emailed token for user {Uid} company {CompanyCode}.",
            user.uid,
            row.CompanyCode);

        return PasswordResetRedeemResult.Ok();
    }

    public async Task<IReadOnlyList<PasswordResetRequestRow>> ListPendingAsync(string companyCode, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await (
            from r in db.PasswordResetRequests.AsNoTracking()
            join u in db.UserLogins.AsNoTracking() on r.UserUid equals u.uid
            where r.CompanyCode == companyCode && r.Status == Pending
            orderby r.RequestedAtUtc
            select new PasswordResetRequestRow
            {
                PasswordResetRequestId = r.PasswordResetRequestId,
                UserUid = r.UserUid,
                LoginId = u.id,
                Name = u.name,
                RequestedAtUtc = r.RequestedAtUtc
            }
        ).ToListAsync(cancellationToken);
    }

    public async Task<PasswordRecoveryResult> CancelAsync(
        long requestId,
        string companyCode,
        string actorUserId,
        int actorUid,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.PasswordResetRequests
            .FirstOrDefaultAsync(r => r.PasswordResetRequestId == requestId && r.CompanyCode == companyCode, cancellationToken);
        if (request is null)
        {
            return PasswordRecoveryResult.Fail(PasswordRecoveryStatus.NotFound);
        }
        if (request.Status != Pending)
        {
            return PasswordRecoveryResult.Fail(PasswordRecoveryStatus.NotPending);
        }

        request.Status = Cancelled;
        request.ResolvedAtUtc = DateTime.UtcNow;
        request.ResolvedBy = Truncate(actorUserId, 10);
        await db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new SecurityAuditEvent
        {
            CompanyCode = companyCode,
            ActorUid = actorUid,
            ActorUserId = actorUserId,
            TargetUid = request.UserUid,
            EventType = SecurityAuditEventTypes.PasswordRecoveryCancelled,
            Outcome = "SUCCESS",
            Details = "Password recovery request cancelled."
        }, cancellationToken);
        return PasswordRecoveryResult.Ok();
    }

    private static string BuildResetLink(string baseUrl, string rawToken) =>
        $"{baseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(rawToken)}";

    private static string Html(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
