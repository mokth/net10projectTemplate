namespace ErpWeb.Model.Entities;

/// <summary>
/// An admin-assisted password reset request. No password, hash or token is ever stored here — the
/// row only records that a user asked a company administrator to reset their password.
/// </summary>
public class PasswordResetRequest
{
    public long PasswordResetRequestId { get; set; }

    public int UserUid { get; set; }

    public string CompanyCode { get; set; } = string.Empty;

    public string Status { get; set; } = "PENDING";

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedAtUtc { get; set; }

    public string? ResolvedBy { get; set; }

    public UserLogin? User { get; set; }
}
