using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

/// <summary>Well-known security audit event types.</summary>
public static class SecurityAuditEventTypes
{
    public const string UserCreated = "USER_CREATED";
    public const string UserProfileUpdated = "USER_PROFILE_UPDATED";
    public const string UserActivated = "USER_ACTIVATED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string UserRoleChanged = "USER_ROLE_CHANGED";
    public const string UserSoftDeleted = "USER_SOFT_DELETED";
    public const string PasswordResetAdmin = "PASSWORD_RESET_ADMIN";
    public const string PasswordChangeSelf = "PASSWORD_CHANGE_SELF";
    public const string ForcePasswordChangeSet = "FORCE_PASSWORD_CHANGE_SET";
    public const string ForcePasswordChangeCleared = "FORCE_PASSWORD_CHANGE_CLEARED";
    public const string PasswordRecoveryRequested = "PASSWORD_RECOVERY_REQUESTED";
    public const string PasswordRecoveryCompleted = "PASSWORD_RECOVERY_COMPLETED";
    public const string PasswordRecoveryCancelled = "PASSWORD_RECOVERY_CANCELLED";
}

/// <summary>
/// Appends security audit rows. Writes are append-only and must never contain a password, hash or
/// reset token. Failed logins are NOT audited here (attacker-driven write flooding); they use the
/// structured logger instead.
/// </summary>
public interface ISecurityAuditWriter
{
    Task WriteAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

public sealed class SecurityAuditWriter : ISecurityAuditWriter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<SecurityAuditWriter> _logger;

    public SecurityAuditWriter(IDbContextFactory<AppDbContext> dbFactory, ILogger<SecurityAuditWriter> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task WriteAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            db.SecurityAuditEvents.Add(auditEvent);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Audit must never fail the business operation it records.
            _logger.LogError(ex, "Failed to write security audit event {EventType}", auditEvent.EventType);
        }
    }
}
