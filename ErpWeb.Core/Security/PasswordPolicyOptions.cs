namespace ErpWeb.Core.Security;

public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    public int MinimumLength { get; set; } = 8;

    public bool RequireLetter { get; set; } = true;

    public bool RequireDigit { get; set; } = true;

    public bool RejectUsername { get; set; } = true;

    /// <summary>Maximum password length in UTF-8 bytes (BCrypt limit). Valid range is 1..72.</summary>
    public int MaximumUtf8Bytes { get; set; } = 72;
}
