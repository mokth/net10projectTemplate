using ErpWeb.Core.Security;
using Microsoft.Extensions.Options;

namespace ErpWeb.Tests;

public class PasswordPolicyTests
{
    private static IPasswordPolicy CreatePolicy(PasswordPolicyOptions? options = null)
    {
        options ??= new PasswordPolicyOptions
        {
            MinimumLength = 8,
            RequireLetter = true,
            RequireDigit = true,
            RejectUsername = true
        };

        return new PasswordPolicy(Options.Create(options));
    }

    [Fact]
    public void Rejects_password_shorter_than_minimum()
    {
        var result = CreatePolicy().Validate("Ab1xxxx", "user1");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_password_without_ascii_letter()
    {
        var result = CreatePolicy().Validate("12345678", "user1");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ASCII letter", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_password_without_ascii_digit()
    {
        var result = CreatePolicy().Validate("Abcdefgh", "user1");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ASCII digit", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_password_equal_to_username_case_insensitive()
    {
        var result = CreatePolicy().Validate("ADMIN123", "admin123");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("username", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Accepts_valid_password()
    {
        var result = CreatePolicy().Validate("Demo@123", "admin");
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Unicode_letter_does_not_satisfy_ascii_letter_requirement()
    {
        var result = CreatePolicy().Validate("パスワード12", "user1");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ASCII letter", StringComparison.Ordinal));
    }

    [Fact]
    public void Unicode_digit_does_not_satisfy_ascii_digit_requirement()
    {
        // Arabic-Indic digits are Unicode digits, not ASCII 0-9
        var result = CreatePolicy().Validate("Password١٢", "user1");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ASCII digit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_null_empty_or_whitespace_password(string? password)
    {
        var result = CreatePolicy().Validate(password!, "user1");
        Assert.False(result.IsValid);
    }
}

public class PasswordPolicyOptionsValidatorTests
{
    private readonly PasswordPolicyOptionsValidator _validator = new();

    [Fact]
    public void Accepts_locked_defaults()
    {
        var result = _validator.Validate(null, new PasswordPolicyOptions
        {
            MinimumLength = 8,
            RequireLetter = true,
            RequireDigit = true,
            RejectUsername = true
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Rejects_minimum_length_below_eight()
    {
        var result = _validator.Validate(null, new PasswordPolicyOptions
        {
            MinimumLength = 7,
            RequireLetter = true,
            RequireDigit = true,
            RejectUsername = true
        });

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Rejects_disabled_mandatory_flags(bool requireLetter, bool requireDigit, bool rejectUsername)
    {
        var result = _validator.Validate(null, new PasswordPolicyOptions
        {
            MinimumLength = 8,
            RequireLetter = requireLetter,
            RequireDigit = requireDigit,
            RejectUsername = rejectUsername
        });

        Assert.False(result.Succeeded);
    }
}
