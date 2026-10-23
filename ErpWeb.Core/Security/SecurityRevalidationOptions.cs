using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Security;

/// <summary>
/// Options for periodic cookie/circuit security revalidation. Bound to the "Authentication"
/// configuration section.
/// </summary>
public sealed class SecurityRevalidationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// How often (in minutes) a signed-in principal is revalidated against the live database.
    /// Must be at least 1. Baseline is 5 minutes.
    /// </summary>
    public int SecurityRevalidationMinutes { get; set; } = 5;

    public TimeSpan RevalidationInterval => TimeSpan.FromMinutes(SecurityRevalidationMinutes);
}

public sealed class SecurityRevalidationOptionsValidator : IValidateOptions<SecurityRevalidationOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityRevalidationOptions options)
    {
        if (options.SecurityRevalidationMinutes <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{SecurityRevalidationOptions.SectionName}:SecurityRevalidationMinutes must be at least 1.");
        }

        return ValidateOptionsResult.Success;
    }
}
