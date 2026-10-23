namespace ErpWeb.Core.Security;

/// <summary>A single outbound email. Plain text is mandatory; HTML is optional.</summary>
public sealed record EmailMessage(string ToAddress, string Subject, string TextBody, string? HtmlBody = null);

/// <summary>
/// Outbound mail seam. Implementations must not throw for a missing/unconfigured transport: mail is
/// best-effort and must never fail the operation that triggered it (password reset requests are
/// anonymous, enumeration-safe and always report the same outcome).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
