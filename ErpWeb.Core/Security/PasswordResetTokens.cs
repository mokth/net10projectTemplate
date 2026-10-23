using System.Security.Cryptography;
using System.Text;

namespace ErpWeb.Core.Security;

/// <summary>
/// Creates and hashes opaque password-reset tokens.
///
/// <para>
/// The raw token is 256 bits from <see cref="RandomNumberGenerator"/> rendered as base64url, which is
/// URL-safe without escaping and therefore survives an email client's link rewriting. Only the
/// uppercase-hex SHA-256 digest is stored. A plain hash (not BCrypt) is correct here: the input is
/// already high-entropy random, so there is nothing to slow down and no dictionary to defend against.
/// </para>
/// </summary>
public static class PasswordResetTokens
{
    public const int TokenByteLength = 32;

    private const int HashHexLength = 64;

    /// <summary>Generates a fresh raw token. This value is shown exactly once, in the email.</summary>
    public static string CreateRawToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));

    /// <summary>Hashes a raw token for storage or lookup. Case-sensitive; never trim the input.</summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    /// <summary>
    /// Cheap shape check so an obviously malformed token is rejected before it reaches the database.
    /// Rejects null/blank, wrong length and any character outside the base64url alphabet.
    /// </summary>
    public static bool LooksWellFormed(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        // 32 bytes -> 43 base64url characters (unpadded).
        if (rawToken.Length != 43)
        {
            return false;
        }

        foreach (var c in rawToken)
        {
            var ok = c is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    internal static int ExpectedHashLength => HashHexLength;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
