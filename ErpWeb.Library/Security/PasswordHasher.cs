using System.Text;
using BCrypt.Net;

namespace ErpWeb.Library.Security;

public static class PasswordHasher
{
    /// <summary>
    /// BCrypt only operates on the first 72 bytes of input. Longer passwords are rejected here
    /// rather than silently truncated, and <see cref="Verify"/> refuses oversized attacker input.
    /// </summary>
    public const int MaxInputBytes = 72;

    public static string Hash(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            throw new ArgumentException("Password cannot be empty.", nameof(plainText));
        }

        if (Encoding.UTF8.GetByteCount(plainText) > MaxInputBytes)
        {
            throw new ArgumentException(
                $"Password must not exceed {MaxInputBytes} UTF-8 bytes.",
                nameof(plainText));
        }

        return BCrypt.Net.BCrypt.HashPassword(plainText);
    }

    public static bool Verify(string plainText, string hash)
    {
        if (string.IsNullOrEmpty(plainText) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        // Attacker-controlled login input must never reach BCrypt when over the byte limit.
        if (Encoding.UTF8.GetByteCount(plainText) > MaxInputBytes)
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(plainText, hash);
        }
        catch (SaltParseException)
        {
            // Malformed stored hash / invalid salt format: a credential failure, not a server error.
            return false;
        }
        catch (HashInformationException)
        {
            // Unrecognised hash revision/type: treat as credential failure.
            return false;
        }
        catch (ArgumentException)
        {
            // Invalid BCrypt credential format.
            return false;
        }
    }
}
