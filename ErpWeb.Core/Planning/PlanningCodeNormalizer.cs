using System.Text.RegularExpressions;

namespace ErpWeb.Core.Planning;

public static class PlanningCodeNormalizer
{
    public static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    public static string? NormalizeDescription(string? value) =>
        value is null ? null : value.Trim();
}

public static class PlanningInputValidation
{
    private static readonly Regex Forbidden = new(@"[;?:@&=+$%,']", RegexOptions.Compiled);

    public static string? ValidateCode(string? code, int maxLength = 10)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "Code is required.";

        if (code != code.Trim())
            return "Code cannot have leading or trailing spaces.";

        if (code.Length > maxLength)
            return $"Code cannot exceed {maxLength} characters.";

        if (Forbidden.IsMatch(code))
            return "Code contains forbidden characters (;?:@&=+$%,').";

        return null;
    }
}
