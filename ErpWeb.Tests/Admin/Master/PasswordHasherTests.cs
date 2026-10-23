using ErpWeb.Library.Security;

namespace ErpWeb.Tests.Admin.Master;

[Trait(TestCategories.Name, TestCategories.Admin)]
public class PasswordHasherTests
{
    [Fact]
    public void Exactly_72_ascii_bytes_hashes_and_verifies()
    {
        var password = new string('a', 71) + "1"; // 72 ASCII bytes, contains letter + digit
        var hash = PasswordHasher.Hash(password);

        Assert.True(PasswordHasher.Verify(password, hash));
    }

    [Fact]
    public void Hash_rejects_over_72_ascii_bytes_without_truncating()
    {
        var password = new string('a', 72) + "1"; // 73 ASCII bytes

        var ex = Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(password));
        Assert.Contains("72", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_returns_false_for_oversized_attacker_password()
    {
        var password = new string('a', 72) + "1"; // 73 bytes
        var hash = PasswordHasher.Hash("Demo@123");

        Assert.False(PasswordHasher.Verify(password, hash));
    }

    [Fact]
    public void Verify_returns_false_for_malformed_stored_hash()
    {
        Assert.False(PasswordHasher.Verify("Demo@123", "not-a-valid-bcrypt-hash"));
        Assert.False(PasswordHasher.Verify("Demo@123", "$2a$10$short"));
    }

    [Fact]
    public void Verify_returns_false_for_null_or_empty_credentials()
    {
        var hash = PasswordHasher.Hash("Demo@123");
        Assert.False(PasswordHasher.Verify("", hash));
        Assert.False(PasswordHasher.Verify(null!, hash));
        Assert.False(PasswordHasher.Verify("Demo@123", ""));
        Assert.False(PasswordHasher.Verify("Demo@123", null!));
    }

    [Fact]
    public void Valid_existing_bcrypt_hash_verifies()
    {
        var hash = PasswordHasher.Hash("Demo@123");
        Assert.True(PasswordHasher.Verify("Demo@123", hash));
        Assert.False(PasswordHasher.Verify("Demo@124", hash));
    }

    [Fact]
    public void No_silent_truncation_shared_72_byte_prefix_is_not_accepted()
    {
        // Both passwords share the same first 72 bytes; they differ only at byte 73.
        var prefix = new string('a', 72);
        var hash = PasswordHasher.Hash(prefix); // exactly 72 bytes

        var longer = prefix + "b"; // 73 bytes

        // Without the guard BCrypt would truncate the 73-byte input to the same 72 bytes and
        // accept it. The guard must reject it.
        Assert.True(PasswordHasher.Verify(prefix, hash));
        Assert.False(PasswordHasher.Verify(longer, hash));
        Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(longer));
    }

    [Fact]
    public void Multibyte_password_within_byte_limit_hashes_and_verifies()
    {
        // Each CJK char is 3 UTF-8 bytes. 20 chars = 60 bytes + "a1" = 62 bytes total.
        var password = new string('密', 20) + "a1";
        Assert.Equal(62, System.Text.Encoding.UTF8.GetByteCount(password));

        var hash = PasswordHasher.Hash(password);
        Assert.True(PasswordHasher.Verify(password, hash));
    }

    [Fact]
    public void Multibyte_password_over_byte_limit_is_rejected()
    {
        // 24 CJK chars = 72 bytes + "a1" = 74 bytes (but only 26 characters).
        var password = new string('密', 24) + "a1";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(password) > PasswordHasher.MaxInputBytes);

        Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(password));
        Assert.False(PasswordHasher.Verify(password, PasswordHasher.Hash("Demo@123")));
    }
}
