using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ErpWeb.Core.Security;

/// <summary>
/// SMTP delivery for <see cref="IEmailSender"/>, built on MailKit.
///
/// <para>
/// MailKit is used instead of <see cref="System.Net.Mail.SmtpClient"/> because the BCL client only
/// understands STARTTLS on port 587 and cannot speak implicit TLS on port 465. The production mail
/// host offers TLS solely as implicit TLS, so SmtpClient could never connect to it.
/// </para>
///
/// <para>
/// Certificate validation is always on and is never relaxed: a hostname/certificate mismatch must
/// fail loudly rather than silently sending credentials to an unverified endpoint. Point
/// <see cref="EmailOptions.Host"/> at a name the server's certificate actually covers.
/// </para>
///
/// <para>
/// Delivery failures are logged and swallowed after the caller's own retry/guard logic — a caller
/// that is anonymous and enumeration-safe must not have its response change because a mailbox
/// bounced. When <see cref="EmailOptions.Enabled"/> is false, or the transport is incomplete, the
/// message is dropped with a warning and no exception.
/// </para>
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptionsMonitor<EmailOptions> _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptionsMonitor<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.ToAddress))
        {
            _logger.LogWarning("Email '{Subject}' skipped: no recipient address.", message.Subject);
            return;
        }

        var options = _options.CurrentValue;

        if (!options.Enabled ||
            string.IsNullOrWhiteSpace(options.Host) ||
            string.IsNullOrWhiteSpace(options.FromAddress))
        {
            // Expected in development and on installs without SMTP. Never log the body: it can
            // contain a password-reset link.
            _logger.LogWarning(
                "Email '{Subject}' not sent: SMTP is not configured (Enabled={Enabled}, Host={Host}, FromAddress={FromAddress}).",
                message.Subject,
                options.Enabled,
                options.Host,
                options.FromAddress);
            return;
        }

        try
        {
            using var client = new SmtpClient
            {
                Timeout = options.TimeoutSeconds > 0
                    ? (int)Math.Min(TimeSpan.FromSeconds(options.TimeoutSeconds).TotalMilliseconds, int.MaxValue)
                    : 30_000
            };

            await client.ConnectAsync(
                options.Host,
                options.Port,
                ResolveSocketOptions(options),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.UserName))
            {
                await client.AuthenticateAsync(options.UserName, options.Password, cancellationToken);
            }

            await client.SendAsync(BuildMessage(options, message), cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            _logger.LogInformation("Email '{Subject}' sent to the configured recipient.", message.Subject);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately opaque: the recipient address is not logged at Information level to keep
            // personal data out of routine logs.
            _logger.LogError(ex, "Email '{Subject}' failed to send.", message.Subject);
        }
    }

    /// <summary>
    /// Maps configuration onto a TLS mode that never degrades to cleartext.
    /// <para>
    /// <see cref="SecureSocketOptions.StartTlsWhenAvailable"/> is deliberately not used: it silently
    /// falls back to an unencrypted session when a server stops advertising STARTTLS, which would put
    /// the mailbox password on the wire. <see cref="SecureSocketOptions.StartTls"/> fails instead.
    /// </para>
    /// </summary>
    internal static SecureSocketOptions ResolveSocketOptions(EmailOptions options)
    {
        if (!options.EnableSsl)
        {
            return SecureSocketOptions.None;
        }

        // Port 465 is implicit TLS: the handshake happens before the SMTP greeting.
        return options.Port == 465
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;
    }

    internal static MimeMessage BuildMessage(EmailOptions options, EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.TextBody };
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            body.HtmlBody = message.HtmlBody;
        }

        mime.Body = body.ToMessageBody();
        return mime;
    }
}
