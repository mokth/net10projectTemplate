namespace ErpWeb.Library.Security;

public static class PasswordHasher
{
    public static string Hash(string plainText) =>
        BCrypt.Net.BCrypt.HashPassword(plainText);

    public static bool Verify(string plainText, string hash) =>
        BCrypt.Net.BCrypt.Verify(plainText, hash);
}
