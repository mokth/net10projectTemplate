namespace ErpWeb.Model.Entities;

/// <summary>
/// A single-use, time-limited email password-reset token.
///
/// <para>
/// Only the SHA-256 hash of the token is persisted — never the raw value — so a database read
/// (backup, log, support export) cannot be replayed to take over an account. A row is consumed by
/// setting <see cref="UsedAtUtc"/>; expiry is checked against <see cref="ExpiresAtUtc"/>.
/// </para>
/// </summary>
public class PasswordResetToken
{
    public long PasswordResetTokenId { get; set; }

    public int UserUid { get; set; }

    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>Uppercase hex SHA-256 of the raw token. The raw token exists only in the email.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Null until redeemed. Non-null means the token can never be used again.</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>Requester IP, for abuse investigation. Not used for any authorization decision.</summary>
    public string? RequestedIp { get; set; }

    public UserLogin? User { get; set; }
}
