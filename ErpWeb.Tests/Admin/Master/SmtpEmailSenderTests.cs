using ErpWeb.Core.Security;
using MailKit.Security;

namespace ErpWeb.Tests.Admin.Master;

/// <summary>
/// Guards the SMTP transport decisions that caused a production incident: the mail host offers TLS
/// only as implicit TLS on 465, which the old <c>System.Net.Mail.SmtpClient</c> could not do.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Admin)]
public class SmtpEmailSenderTests
{
    private static EmailOptions Options(int port, bool enableSsl = true) => new()
    {
        Enabled = true,
        Host = "smtp.example.com",
        Port = port,
        EnableSsl = enableSsl,
        FromAddress = "erp@example.com",
        FromName = "Wincom ERP"
    };

    [Fact]
    public void Port_465_selects_implicit_tls()
    {
        Assert.Equal(SecureSocketOptions.SslOnConnect, SmtpEmailSender.ResolveSocketOptions(Options(465)));
    }

    [Fact]
    public void Port_587_selects_mandatory_starttls()
    {
        // StartTls (not StartTlsWhenAvailable): a server that stops advertising STARTTLS must fail
        // rather than fall back to sending the mailbox password in cleartext.
        Assert.Equal(SecureSocketOptions.StartTls, SmtpEmailSender.ResolveSocketOptions(Options(587)));
    }

    [Fact]
    public void Disabled_ssl_selects_plain_connection()
    {
        Assert.Equal(SecureSocketOptions.None, SmtpEmailSender.ResolveSocketOptions(Options(25, enableSsl: false)));
    }

    [Fact]
    public void BuildMessage_maps_from_to_subject_and_text_body()
    {
        var mime = SmtpEmailSender.BuildMessage(
            Options(465),
            new EmailMessage("user@example.com", "Reset your password", "plain text body"));

        Assert.Equal("erp@example.com", mime.From.Mailboxes.Single().Address);
        Assert.Equal("Wincom ERP", mime.From.Mailboxes.Single().Name);
        Assert.Equal("user@example.com", mime.To.Mailboxes.Single().Address);
        Assert.Equal("Reset your password", mime.Subject);
        Assert.Equal("plain text body", mime.TextBody);
    }

    [Fact]
    public void BuildMessage_includes_html_alternative_when_supplied()
    {
        var mime = SmtpEmailSender.BuildMessage(
            Options(465),
            new EmailMessage("user@example.com", "Subject", "plain", "<p>html</p>"));

        Assert.Equal("plain", mime.TextBody);
        Assert.Contains("<p>html</p>", mime.HtmlBody);
    }

    [Fact]
    public void BuildMessage_omits_html_body_when_absent()
    {
        var mime = SmtpEmailSender.BuildMessage(
            Options(465),
            new EmailMessage("user@example.com", "Subject", "plain"));

        Assert.Null(mime.HtmlBody);
    }
}
