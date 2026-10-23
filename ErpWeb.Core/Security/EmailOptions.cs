using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Security;

/// <summary>
/// SMTP settings for outbound mail. Bound to the "Email" configuration section.
///
/// <para>
/// <see cref="Enabled"/> is false by default so a fresh install never attempts (and never blocks on)
/// a network call. Credentials belong in <c>appsettings.local.json</c> or environment variables
/// (<c>Email__Password</c>), never in the committed appsettings files.
/// </para>
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Submission port. 465 means implicit TLS (<c>SslOnConnect</c>); any other port with
    /// <see cref="EnableSsl"/> means STARTTLS, which is required and never downgraded to cleartext.
    /// </summary>
    public int Port { get; set; } = 465;

    /// <summary>
    /// Require TLS. When false the session (including the password) is sent in cleartext — only
    /// acceptable to a relay on the same machine.
    /// </summary>
    public bool EnableSsl { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Wincom ERP";

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (!options.Enabled)
        {
            // Disabled is a valid, explicit configuration: reset emails are silently skipped.
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            errors.Add($"{EmailOptions.SectionName}:Host is required when Enabled is true.");
        }

        if (options.Port is < 1 or > 65535)
        {
            errors.Add($"{EmailOptions.SectionName}:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            errors.Add($"{EmailOptions.SectionName}:FromAddress is required when Enabled is true.");
        }

        if (options.TimeoutSeconds is < 1 or > 300)
        {
            errors.Add($"{EmailOptions.SectionName}:TimeoutSeconds must be between 1 and 300.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
