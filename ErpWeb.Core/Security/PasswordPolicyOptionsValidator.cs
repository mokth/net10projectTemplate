using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Security;

public sealed class PasswordPolicyOptionsValidator : IValidateOptions<PasswordPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, PasswordPolicyOptions options)
    {
        var failures = new List<string>();

        if (options.MinimumLength < 8)
        {
            failures.Add($"{PasswordPolicyOptions.SectionName}:MinimumLength must be at least 8.");
        }

        if (!options.RequireLetter)
        {
            failures.Add($"{PasswordPolicyOptions.SectionName}:RequireLetter must be true.");
        }

        if (!options.RequireDigit)
        {
            failures.Add($"{PasswordPolicyOptions.SectionName}:RequireDigit must be true.");
        }

        if (!options.RejectUsername)
        {
            failures.Add($"{PasswordPolicyOptions.SectionName}:RejectUsername must be true.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
