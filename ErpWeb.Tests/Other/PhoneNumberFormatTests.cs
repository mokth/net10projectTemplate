using ErpWeb.Core.Services;

namespace ErpWeb.Tests.Other;
/// <summary>
/// The E.164 recognition rules behind every phone field: what is accepted and canonicalised, what is a
/// placeholder, what is rejected, and — most importantly — what a rejected value is stored as. The
/// non-lossy <c>ToStored</c> contract and the idempotency of <c>Normalize</c> are the two properties the
/// master-data and submission paths depend on, so both are asserted directly rather than implied.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Shared)]
public class PhoneNumberFormatTests
{
    // ─────────────────── Recognised: local formats are canonicalised ───────────────────

    [Theory]
    [InlineData(" 03-9876 5432 ", "+60398765432")]
    [InlineData("03-9876 5432", "+60398765432")]
    [InlineData("0398765432", "+60398765432")]
    [InlineData("(03) 9876 5432", "+60398765432")]
    [InlineData("+60 3-9876 5432", "+60398765432")]
    [InlineData("+60398765432", "+60398765432")]
    [InlineData("60398765432", "+60398765432")]
    [InlineData("0060398765432", "+60398765432")]
    [InlineData("011-1234 5678", "+601112345678")]
    [InlineData("0312345678", "+60312345678")]
    [InlineData("+1 415 555 2671", "+14155552671")]
    public void Accepted_formats_are_canonicalised_to_E164(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumberFormat.Normalize(input));
        Assert.Equal(expected, PhoneNumberFormat.ToStored(input));
        Assert.Null(PhoneNumberFormat.Validate(input));
        Assert.True(PhoneNumberFormat.IsE164(expected));
    }

    [Fact]
    public void A_different_default_country_code_can_be_supplied_for_a_national_format()
    {
        // "65" has no trunk prefix, so the country code has to be present in the value.
        Assert.Equal("+6598765432", PhoneNumberFormat.Normalize("6598765432", "65"));

        // "62" uses a trunk 0 that is dropped, exactly like Malaysia's.
        Assert.Equal("+62812345678", PhoneNumberFormat.Normalize("0812345678", "62"));
    }

    // ─────────────────────────── Placeholders and blanks ───────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NA")]
    [InlineData("n/a")]
    [InlineData("NIL")]
    [InlineData("NULL")]
    [InlineData("TBA")]
    [InlineData("TBD")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData(".")]
    [InlineData("0")]
    [InlineData("00")]
    [InlineData("X")]
    [InlineData("XX")]
    [InlineData("  NA  ")]
    public void Placeholders_and_blanks_mean_no_value_supplied(string? input)
    {
        Assert.Null(PhoneNumberFormat.Normalize(input));
        Assert.Null(PhoneNumberFormat.ToStored(input));
        Assert.Null(PhoneNumberFormat.Validate(input));
    }

    // ─────────────────────────────── Rejections ───────────────────────────────

    [Theory]
    // Letters: junk, and extensions E.164 cannot express.
    [InlineData("A-phone")]
    [InlineData("03-9876 5432 ext 12")]
    // More than one number in a single field.
    [InlineData("03-1234 5678 / 03-8765 4321")]
    [InlineData("03-1234, 04-5678")]
    // Ambiguous: no country code and no trunk prefix, so a country cannot be inferred.
    [InlineData("111")]
    [InlineData("999-0000")]
    [InlineData("1234567890123456")]
    // The country-code-without-plus rule must not swallow a 9-digit value that merely starts with "60".
    [InlineData("601234567")]
    // Bounds.
    [InlineData("+123")]
    [InlineData("+1234567890123456")]
    // Malformed "+".
    [InlineData("++60398765432")]
    [InlineData("+60+398765432")]
    [InlineData("03+98765432")]
    [InlineData("+")]
    // Not digits at all.
    [InlineData("###")]
    [InlineData("abc")]
    public void Unusable_values_are_rejected_and_reported(string input)
    {
        Assert.Null(PhoneNumberFormat.Normalize(input));
        Assert.NotNull(PhoneNumberFormat.Validate(input));
        Assert.False(PhoneNumberFormat.IsE164(input));
    }

    [Fact]
    public void Validate_names_the_field_and_shows_an_example()
    {
        var message = PhoneNumberFormat.Validate("A-phone", "Supplier telephone");

        Assert.NotNull(message);
        Assert.Contains("Supplier telephone", message, StringComparison.Ordinal);
        Assert.Contains("A-phone", message, StringComparison.Ordinal);
        Assert.Contains("E.164", message, StringComparison.Ordinal);
        Assert.Contains($"+{PhoneNumberFormat.MalaysiaCountryCode}", message, StringComparison.Ordinal);
    }

    // ──────────────────────── Storage semantics (non-lossy) ────────────────────────

    [Theory]
    [InlineData("  A-phone  ", "A-phone")]
    [InlineData("  999-0000  ", "999-0000")]
    [InlineData("03-1234 / 04-5678", "03-1234 / 04-5678")]
    [InlineData("111", "111")]
    [InlineData("601234567", "601234567")]
    public void ToStored_preserves_an_unusable_value_after_an_outer_trim_only(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumberFormat.ToStored(input));
        Assert.Null(PhoneNumberFormat.Normalize(input));
    }

    [Theory]
    [InlineData("NA", null)]
    [InlineData("  0  ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ToStored_collapses_a_placeholder_to_null(string? input, string? expected)
    {
        // The one documented exception to non-lossiness: a placeholder means "no value supplied".
        Assert.Equal(expected, PhoneNumberFormat.ToStored(input));
    }

    // ───────────────────────────── IsE164 semantics ─────────────────────────────

    [Fact]
    public void IsE164_is_false_for_null_and_blank_while_Validate_treats_them_as_optional()
    {
        Assert.False(PhoneNumberFormat.IsE164(null));
        Assert.False(PhoneNumberFormat.IsE164(""));
        Assert.False(PhoneNumberFormat.IsE164("   "));
        Assert.False(PhoneNumberFormat.IsE164("03-9876 5432"));
        Assert.False(PhoneNumberFormat.IsE164("60398765432"));
        Assert.True(PhoneNumberFormat.IsE164("+60398765432"));

        // The pair deliberately separates "is a valid E.164 value" from "is this field optional".
        Assert.Null(PhoneNumberFormat.Validate(null));
        Assert.Null(PhoneNumberFormat.Validate(""));
    }

    // ──────────────────────────────── Idempotency ────────────────────────────────

    [Theory]
    [InlineData("03-9876 5432")]
    [InlineData("+60398765432")]
    [InlineData("60398765432")]
    [InlineData("0060398765432")]
    [InlineData("011-1234 5678")]
    public void Normalize_is_idempotent(string input)
    {
        var once = PhoneNumberFormat.Normalize(input);

        Assert.NotNull(once);
        Assert.Equal(once, PhoneNumberFormat.Normalize(once));
        Assert.True(PhoneNumberFormat.IsE164(once));
    }

    [Fact]
    public void Normalize_leaves_an_already_canonical_value_untouched()
    {
        Assert.Equal("+60398765432", PhoneNumberFormat.Normalize("+60398765432"));
    }
}
