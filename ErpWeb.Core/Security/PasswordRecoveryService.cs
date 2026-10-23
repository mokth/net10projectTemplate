using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// Admin-assisted password recovery. A reset request is a PENDING row that a company administrator
/// resolves from User Accounts; no email token is generated. Responses to the anonymous caller are
/// always generic so account existence is not revealed.
/// </summary>
public interface IPasswordRecoveryService
{
    Task RequestResetAsync(string companyCode, string username, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PasswordResetRequestRow>> ListPendingAsync(string companyCode, CancellationToken cancellationToken = default);

    Task<PasswordRecoveryResult> CancelAsync(
        long requestId,
        string companyCode,
        string actorUserId,
        int actorUid,
        CancellationToken cancellationToken = default);

    Task<PasswordRecoveryResult> CompletePendingAsync(
        int userUid,
        string companyCode,
        string actorUserId,
        int actorUid,
        CancellationToken cancellationToken = default);
}

public sealed class PasswordRecoveryService : IPasswordRecoveryService
{
    public const string Pending = "PENDING";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    private const string DeletedMarker = "deleted";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISecurityAuditWriter _audit;
    private readonly ILogger<PasswordRecoveryService> _logger;

    public PasswordRecoveryService(
        IDbContextFactory<AppDbContext> dbFactory,
        ISecurityAuditWriter audit,
        ILogger<PasswordRecoveryService> logger)
    {
        _dbFactory = dbFactory;
        _audit = audit;
        _logger = logger;
    }

    public async Task RequestResetAsync(string companyCode, string username, CancellationToken cancellationToken = default)
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

        // Fast path: one pending request per user. The unique filtered index still guards the race
        // when two requests arrive together; the catch below absorbs that expected duplicate.
        var alreadyPending = await db.PasswordResetRequests
            .AnyAsync(r => r.UserUid == user.uid && r.Status == Pending, cancellationToken);
        if (alreadyPending)
        {
            return;
        }

        var request = new PasswordResetRequest
        {
            UserUid = user.uid,
            CompanyCode = companyCode,
            Status = Pending,
            RequestedAtUtc = DateTime.UtcNow
        };
        db.PasswordResetRequests.Add(request);

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
            _logger.LogDebug(
                "Duplicate password reset request for user {Uid} company {CompanyCode} ignored",
                user.uid,
                companyCode);
        }
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

    public async Task<PasswordRecoveryResult> CompletePendingAsync(
        int userUid,
        string companyCode,
        string actorUserId,
        int actorUid,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var requests = await db.PasswordResetRequests
            .Where(r => r.UserUid == userUid && r.CompanyCode == companyCode && r.Status == Pending)
            .ToListAsync(cancellationToken);
        if (requests.Count == 0)
        {
            return PasswordRecoveryResult.Fail(PasswordRecoveryStatus.NotFound);
        }

        foreach (var request in requests)
        {
            request.Status = Completed;
            request.ResolvedAtUtc = DateTime.UtcNow;
            request.ResolvedBy = Truncate(actorUserId, 10);
        }

        await db.SaveChangesAsync(cancellationToken);
        return PasswordRecoveryResult.Ok();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
