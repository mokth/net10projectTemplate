using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Security;

/// <summary>
/// Options for the email password-reset flow. Bound to the "PasswordRecovery" configuration section.
/// </summary>
public sealed class PasswordRecoveryOptions
{
    public const string SectionName = "PasswordRecovery";

    /// <summary>
    /// How long an emailed reset link stays valid. Short by design: the window is the only thing
    /// protecting an intercepted mailbox. Must be between 5 and 1440 minutes (24 hours).
    /// </summary>
    public int TokenLifetimeMinutes { get; set; } = 60;

    /// <summary>
    /// Absolute base URL used to build reset links, e.g. <c>https://host/erpweb</c>.
    ///
    /// <para>
    /// When blank the link is derived from the incoming request. Set it explicitly in production:
    /// behind IIS/reverse proxies the request scheme and host are not always trustworthy, and a
    /// reset link must never point at an attacker-controlled origin.
    /// </para>
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public TimeSpan TokenLifetime => TimeSpan.FromMinutes(TokenLifetimeMinutes);
}

public sealed class PasswordRecoveryOptionsValidator : IValidateOptions<PasswordRecoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, PasswordRecoveryOptions options)
    {
        if (options.TokenLifetimeMinutes is < 5 or > 1440)
        {
            return ValidateOptionsResult.Fail(
                $"{PasswordRecoveryOptions.SectionName}:TokenLifetimeMinutes must be between 5 and 1440.");
        }

        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl) &&
            !Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail(
                $"{PasswordRecoveryOptions.SectionName}:PublicBaseUrl must be an absolute URL when set.");
        }

        return ValidateOptionsResult.Success;
    }
}
