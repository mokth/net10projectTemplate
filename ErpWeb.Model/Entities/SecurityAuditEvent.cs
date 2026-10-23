namespace ErpWeb.Model.Entities;

/// <summary>
/// Append-only security audit row. Never store a password, hash or reset token in OldValue/NewValue/Details.
/// </summary>
public class SecurityAuditEvent
{
    public long SecurityAuditEventId { get; set; }

    public string? CompanyCode { get; set; }

    public int? ActorUid { get; set; }

    public string? ActorUserId { get; set; }

    public int? TargetUid { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    public string? Details { get; set; }
}
