namespace ErpWeb.Core.Security;

public interface IPasswordPolicy
{
    PasswordValidationResult Validate(string newPassword, string? username);
}
