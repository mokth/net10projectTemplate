namespace ErpWeb.Core.Services;

/// <summary>
/// Recognises, canonicalises and validates telephone numbers for the E.164 standard
/// (https://www.itu.int/rec/T-REC-E.164).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope: structure, not allocation.</b> This validates E.164 <i>structure</i> only — a leading
/// <c>+</c> followed by 8-15 digits. It deliberately does NOT validate Malaysian numbering allocation,
/// prefix validity or operator mobile/fixed-line ranges. A value passing <see cref="Validate"/> means
/// "structurally E.164", not "a number actually allocated in Malaysia". MyInvois stays the authority on
/// reachability, so a future numbering-plan change never becomes an ERP code change.
/// </para>
/// <para>
/// <b>Recognised input.</b> Local Malaysian formats are accepted and canonicalised, because that is what
/// the ERP's existing master data, invoices and printed documents already contain. The national trunk
/// <c>0</c> is dropped and replaced by <see cref="MalaysiaCountryCode"/>: <c>03-9876 5432</c> becomes
/// <c>+60398765432</c>.
/// </para>
/// <para>
/// <b>Placeholders are an ERP convention, not E.164.</b> Tokens such as <c>NA</c>, <c>NIL</c>, <c>-</c>,
/// <c>0</c> and <c>00</c> are treated as "no value supplied". E.164 says nothing about placeholders; this
/// list exists only because the ERP's historical rows use them, and emitting one to MyInvois as a
/// telephone number would be a rejection.
/// </para>
/// <para>
/// <b>One classifier.</b> <see cref="Classify"/> is the single source of truth. <see cref="Normalize"/>
/// (read/submit), <see cref="ToStored"/> (write) and <see cref="Validate"/> (message) all read from it, so
/// the three can never disagree about what a value is.
/// </para>
/// </remarks>
public static class PhoneNumberFormat
{
    /// <summary>
    /// The country code assumed when a value arrives in a national format. This is the ONLY place the
    /// default country code is written; every caller relies on the default parameter below rather than
    /// repeating the literal.
    /// </summary>
    public const string MalaysiaCountryCode = "60";

    /// <summary>
    /// Practical floor for the digit count. This is a sanity guard against truncated data, not an E.164
    /// rule — E.164 itself permits as few as one digit after the country code.
    /// </summary>
    public const int MinDigits = 8;

    /// <summary>The E.164 ceiling: an E.164 number carries at most 15 digits.</summary>
    public const int MaxDigits = 15;

    private const string DefaultFieldLabel = "Telephone number";

    /// <summary>Characters that are pure formatting and are removed during recognition.</summary>
    private static readonly char[] Separators = [' ', '\t', '\u00A0', '-', '.', '(', ')'];

    /// <summary>
    /// Characters that mean "more than one number in this field". A single column cannot hold two E.164
    /// numbers, so such a value is rejected rather than guessed at.
    /// </summary>
    private static readonly char[] MultipleValueMarkers = [',', ';', '/', '\\'];

    /// <summary>ERP placeholder tokens that mean "no phone number supplied". See the type remarks.</summary>
    private static readonly string[] Placeholders =
        ["NA", "N/A", "NIL", "NULL", "TBA", "TBD", "-", "--", ".", "0", "00", "X", "XX"];

    private enum Kind
    {
        /// <summary>Null, empty or whitespace only.</summary>
        Blank,

        /// <summary>An ERP placeholder token meaning "no value supplied".</summary>
        Placeholder,

        /// <summary>A phone number whose canonical E.164 form could be determined.</summary>
        Recognized,

        /// <summary>Not usable as a phone number, and not safely repairable.</summary>
        Invalid
    }

    /// <summary>
    /// What <see cref="Classify"/> decided: the verdict, the caller's value with outer whitespace removed
    /// (never further altered), and the canonical <c>+&lt;digits&gt;</c> form when one could be determined.
    /// </summary>
    private readonly record struct Classification(Kind Kind, string Trimmed, string? E164);

    /// <summary>
    /// The canonical E.164 form of <paramref name="value"/>, or <c>null</c> when the value is blank,
    /// a placeholder, or unusable. Use this when READING a value for display or for a submission payload.
    /// </summary>
    /// <remarks>
    /// This is intentionally lossy: an unusable value yields <c>null</c>. Never use it on a write path —
    /// use <see cref="ToStored"/> instead, which preserves what the user typed.
    /// </remarks>
    public static string? Normalize(string? value, string defaultCountryCode = MalaysiaCountryCode) =>
        Classify(value, defaultCountryCode).E164;

    /// <summary>
    /// The value to persist for <paramref name="value"/>: the canonical E.164 form when recognised,
    /// otherwise the caller's own text with only the outer whitespace removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately NON-LOSSY. It never strips punctuation, never reorders and never blanks a field
    /// the user typed, so a master-data save cannot damage a number this class does not understand. The
    /// separator stripping used during recognition (see <see cref="Classify"/>) applies to deciding what a
    /// value <i>is</i>, never to what gets stored for a value that was rejected.
    /// </para>
    /// <para>
    /// The single documented exception is the placeholder: a placeholder means "no value supplied", so it
    /// becomes <c>null</c> rather than being preserved. Persisting <c>"NA"</c> only to emit it to MyInvois
    /// as a telephone number would be worse.
    /// </para>
    /// </remarks>
    public static string? ToStored(string? value, string defaultCountryCode = MalaysiaCountryCode)
    {
        var classification = Classify(value, defaultCountryCode);
        return classification.Kind switch
        {
            Kind.Blank or Kind.Placeholder => null,
            Kind.Recognized => classification.E164,
            _ => classification.Trimmed.Length == 0 ? null : classification.Trimmed
        };
    }

    /// <summary>
    /// A field-ready message when <paramref name="value"/> is present but unusable, otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// This is a FORMAT check only. Blank and placeholder values return <c>null</c> — deciding whether a
    /// phone is <i>required</i> belongs to the caller (the e-Invoice validator's <c>Require</c> calls), so a
    /// missing number and a malformed number never produce two competing messages for one field.
    /// </remarks>
    /// <param name="value">The value to check.</param>
    /// <param name="fieldLabel">How the field is named in the message, e.g. "Supplier telephone".</param>
    /// <param name="defaultCountryCode">Country code assumed for a national-format value.</param>
    public static string? Validate(
        string? value,
        string fieldLabel = DefaultFieldLabel,
        string defaultCountryCode = MalaysiaCountryCode)
    {
        var classification = Classify(value, defaultCountryCode);
        if (classification.Kind != Kind.Invalid)
        {
            return null;
        }

        return $"{fieldLabel} '{classification.Trimmed}' must follow the E.164 format " +
               $"(for example +{defaultCountryCode}123456789).";
    }

    /// <summary>
    /// True when <paramref name="value"/> is already exactly the canonical E.164 form, i.e. when
    /// <see cref="Normalize"/> would return it unchanged.
    /// </summary>
    /// <remarks>
    /// Deliberately false for <c>null</c> and blank: this answers "is this a valid E.164 value?", not
    /// "does this field need filling in?" (that question is <c>Validate(null) is null</c>).
    /// <c>IsE164(null)</c> is false; <c>IsE164("+60398765432")</c> is true;
    /// <c>IsE164("03-9876 5432")</c> and <c>IsE164("60398765432")</c> are false.
    /// </remarks>
    public static bool IsE164(string? value) => value is not null && Normalize(value) == value;

    /// <summary>
    /// The single source of truth for what a value is. Recognition order matters and is documented on
    /// each step.
    /// </summary>
    private static Classification Classify(string? value, string defaultCountryCode)
    {
        var trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return new Classification(Kind.Blank, trimmed, null);
        }

        if (IsPlaceholder(trimmed))
        {
            return new Classification(Kind.Placeholder, trimmed, null);
        }

        // Two numbers cannot fit in one E.164 value, so a multi-value field is rejected outright rather
        // than silently keeping the first one.
        if (trimmed.IndexOfAny(MultipleValueMarkers) >= 0)
        {
            return new Classification(Kind.Invalid, trimmed, null);
        }

        // Letters cover both junk ("A-phone") and extensions ("ext 12"), which E.164 cannot express.
        if (trimmed.Any(char.IsLetter))
        {
            return new Classification(Kind.Invalid, trimmed, null);
        }

        // '+' is only meaningful as the leading character.
        if (trimmed.IndexOf('+') > 0)
        {
            return new Classification(Kind.Invalid, trimmed, null);
        }

        // Separators here are recognition-only. The original text is preserved in Trimmed for the caller,
        // so nothing this method strips can reach storage.
        var compact = new string(trimmed.Where(c => !Separators.Contains(c)).ToArray());
        var body = compact.StartsWith('+') ? compact[1..] : compact;

        if (body.Length == 0 || !body.All(char.IsAsciiDigit))
        {
            return new Classification(Kind.Invalid, trimmed, null);
        }

        var hasCountryCodePrefix = compact.Length != body.Length;

        string digits;
        if (hasCountryCodePrefix)
        {
            // Already in international form; only the digit count is left to check.
            digits = body;
        }
        else if (body.Length >= 2 && body[0] == '0' && body[1] == '0')
        {
            // "00" is the international dialling prefix in much of the world, including Malaysia.
            digits = body[2..];
        }
        else if (body.Length >= 10 && body.StartsWith(defaultCountryCode, StringComparison.Ordinal))
        {
            // Country code present but without the '+'. The length guard is what stops a malformed
            // 9-digit "601234567" from being mistaken for a country-coded number: it fails here and falls
            // through to the ambiguous branch below.
            digits = body;
        }
        else if (body[0] == '0')
        {
            // National format: the trunk '0' is not part of an E.164 number, so it is replaced by the
            // country code rather than kept.
            digits = defaultCountryCode + body[1..];
        }
        else
        {
            // A bare subscriber number with neither a country code nor a trunk prefix. Guessing a country
            // here is exactly the kind of inference this class refuses to make.
            return new Classification(Kind.Invalid, trimmed, null);
        }

        if (digits.Length is < MinDigits or > MaxDigits)
        {
            return new Classification(Kind.Invalid, trimmed, null);
        }

        return new Classification(Kind.Recognized, trimmed, "+" + digits);
    }

    private static bool IsPlaceholder(string trimmed) =>
        Placeholders.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase));
}
