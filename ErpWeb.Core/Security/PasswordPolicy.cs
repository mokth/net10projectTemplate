using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Security;

public sealed class PasswordPolicy : IPasswordPolicy
{
    private static readonly Regex AsciiLetter = new("[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex AsciiDigit = new("[0-9]", RegexOptions.Compiled);

    private readonly PasswordPolicyOptions _options;

    public PasswordPolicy(IOptions<PasswordPolicyOptions> options)
    {
        _options = options.Value;
    }

    public PasswordValidationResult Validate(string newPassword, string? username)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            errors.Add("Password is required.");
            return PasswordValidationResult.Failure(errors.ToArray());
        }

        if (newPassword.Length < _options.MinimumLength)
        {
            errors.Add($"Password must be at least {_options.MinimumLength} characters.");
        }

        // BCrypt only uses the first 72 bytes; enforce the limit by UTF-8 byte count, never by
        // character count, so multibyte passwords are measured correctly and never truncated.
        if (Encoding.UTF8.GetByteCount(newPassword) > _options.MaximumUtf8Bytes)
        {
            errors.Add($"Password must not exceed {_options.MaximumUtf8Bytes} UTF-8 bytes.");
        }

        if (_options.RequireLetter && !AsciiLetter.IsMatch(newPassword))
        {
            errors.Add("Password must contain at least one ASCII letter (A-Z or a-z).");
        }

        if (_options.RequireDigit && !AsciiDigit.IsMatch(newPassword))
        {
            errors.Add("Password must contain at least one ASCII digit (0-9).");
        }

        if (_options.RejectUsername &&
            !string.IsNullOrWhiteSpace(username) &&
            string.Equals(newPassword, username.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Password must not match the username.");
        }

        return errors.Count == 0
            ? PasswordValidationResult.Success()
            : PasswordValidationResult.Failure(errors.ToArray());
    }
}
