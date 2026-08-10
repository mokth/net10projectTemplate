namespace ErpWeb.Core.Security;

public sealed class PasswordValidationResult
{
    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static PasswordValidationResult Success() =>
        new() { IsValid = true };

    public static PasswordValidationResult Failure(params string[] errors) =>
        new() { IsValid = false, Errors = errors };
}
