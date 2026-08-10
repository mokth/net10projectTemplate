namespace ErpWeb.Core.Security;

public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    public int MinimumLength { get; set; } = 8;

    public bool RequireLetter { get; set; } = true;

    public bool RequireDigit { get; set; } = true;

    public bool RejectUsername { get; set; } = true;
}
