using ErpWeb.Core.Settings;

namespace ErpWeb.Tests;

/// <summary>
/// The pure resolution ladder — Branch → Company → Global → code default. No fixture, no database.
///
/// <para>
/// These use their own definitions rather than catalogue entries on purpose: the ladder must be pinned
/// independently of whatever the catalogue happens to ship, or a catalogue change would silently move
/// what these tests mean.
/// </para>
/// </summary>
public class AppSettingResolverTests
{
    private static AppSettingDefinition TokenDef() => new(
        AppSettingModules.Sales,
        "TEST_TOKEN",
        AppSettingType.Token,
        AppSettingScope.Global | AppSettingScope.Company | AppSettingScope.Branch,
        "FULL",
        ["FULL", "LIST", "ITEM"],
        AppSettingBacking.Registry,
        m => m?.ToUpperInvariant(),
        "test");

    private static AppSettingDefinition NumberDef(int defaultScope = 0) => new(
        AppSettingModules.Admin,
        "TEST_NUMBER",
        AppSettingType.Number,
        AppSettingScope.Company,
        "30",
        Description: "test");

    private static AppSettingDefinition FlagDef() => new(
        AppSettingModules.Inventory,
        "TEST_FLAG",
        AppSettingType.Flag,
        AppSettingScope.Company | AppSettingScope.Branch,
        "false",
        Description: "test");

    private static AppSettingDefinition GlobalOnlyDef() => new(
        AppSettingModules.Admin,
        "TEST_GLOBAL",
        AppSettingType.Number,
        AppSettingScope.Global,
        "60",
        Description: "test");

    [Fact]
    public void Branch_beats_company_beats_global()
    {
        var definition = TokenDef();

        var branchWins = AppSettingResolver.Resolve(definition, "ITEM", "LIST", "FULL");
        Assert.Equal("ITEM", branchWins.Value);
        Assert.Equal(AppSettingScope.Branch, branchWins.Provenance);
        Assert.False(branchWins.IsDefault);

        var companyWins = AppSettingResolver.Resolve(definition, null, "LIST", "FULL");
        Assert.Equal("LIST", companyWins.Value);
        Assert.Equal(AppSettingScope.Company, companyWins.Provenance);

        var globalWins = AppSettingResolver.Resolve(definition, null, null, "FULL");
        Assert.Equal("FULL", globalWins.Value);
        Assert.Equal(AppSettingScope.Global, globalWins.Provenance);
    }

    [Fact]
    public void No_value_anywhere_returns_the_code_default_with_no_provenance()
    {
        var resolution = AppSettingResolver.Resolve(TokenDef(), null, null, null);

        Assert.Equal("FULL", resolution.Value);
        Assert.True(resolution.IsDefault);
        Assert.Equal(AppSettingScope.None, resolution.Provenance);
        Assert.Null(resolution.RejectedReason);
    }

    [Fact]
    public void A_blank_value_at_a_level_is_treated_as_not_set_rather_than_as_an_error()
    {
        var resolution = AppSettingResolver.Resolve(TokenDef(), "   ", null, "LIST");

        Assert.Equal("LIST", resolution.Value);
        Assert.Equal(AppSettingScope.Global, resolution.Provenance);
        Assert.Null(resolution.RejectedReason);
    }

    /// <summary>
    /// I3: the definition's AllowedScopes is authoritative. A row at a scope the definition forbids must
    /// never be consulted, even when it holds a perfectly valid value.
    /// </summary>
    [Fact]
    public void A_scope_the_definition_disallows_is_ignored()
    {
        var definition = GlobalOnlyDef();

        // The caller passes a company value, but the definition is Global-only.
        var resolution = AppSettingResolver.Resolve(definition, null, "999", null);

        Assert.True(resolution.IsDefault);
        Assert.Equal("60", resolution.Value);
    }

    /// <summary>
    /// I2 in its most important form: a mistyped stored token must degrade, not throw, and must not win
    /// over a lower level that is valid.
    /// </summary>
    [Fact]
    public void An_unknown_token_falls_through_to_the_next_level()
    {
        var resolution = AppSettingResolver.Resolve(TokenDef(), "NONSENSE", "LIST", "FULL");

        Assert.Equal("LIST", resolution.Value);
        Assert.Equal(AppSettingScope.Company, resolution.Provenance);
    }

    [Fact]
    public void An_unknown_token_with_no_lower_level_falls_to_the_default_and_records_why()
    {
        var resolution = AppSettingResolver.Resolve(TokenDef(), "NONSENSE", null, null);

        Assert.True(resolution.IsDefault);
        Assert.Equal("FULL", resolution.Value);
        Assert.NotNull(resolution.RejectedReason);
        Assert.Contains("NONSENSE", resolution.RejectedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparseable_number_falls_to_the_default_and_records_why()
    {
        var resolution = AppSettingResolver.Resolve(NumberDef(), null, "not-a-number", null);

        Assert.True(resolution.IsDefault);
        Assert.Equal("30", resolution.Value);
        Assert.NotNull(resolution.RejectedReason);
    }

    [Fact]
    public void A_valid_number_is_canonicalised_to_the_invariant_culture_without_trailing_zeros()
    {
        var resolution = AppSettingResolver.Resolve(NumberDef(), null, " 45.50 ", null);

        Assert.Equal("45.5", resolution.Value);
        Assert.False(resolution.IsDefault);

        // The same number spelled three ways must resolve to ONE string, or a stored value's textual
        // form would depend on how the storage engine round-tripped it.
        Assert.Equal("45", AppSettingResolver.Resolve(NumberDef(), null, "45", null).Value);
        Assert.Equal("45", AppSettingResolver.Resolve(NumberDef(), null, "45.0", null).Value);
        Assert.Equal("45", AppSettingResolver.Resolve(NumberDef(), null, "45.00", null).Value);
    }

    [Theory]
    [InlineData("yes", "true")]
    [InlineData("Y", "true")]
    [InlineData("1", "true")]
    [InlineData("no", "false")]
    [InlineData("N", "false")]
    [InlineData("0", "false")]
    public void A_flag_accepts_the_house_yes_no_vocabulary(string stored, string expected)
    {
        var resolution = AppSettingResolver.Resolve(FlagDef(), null, stored, null);

        Assert.Equal(expected, resolution.Value);
        Assert.False(resolution.IsDefault);
    }

    [Fact]
    public void A_date_is_canonicalised_to_iso_and_the_time_is_dropped()
    {
        var definition = new AppSettingDefinition(
            AppSettingModules.Admin,
            "TEST_DATE",
            AppSettingType.Date,
            AppSettingScope.Company,
            "2000-01-01",
            Description: "test");

        var resolution = AppSettingResolver.Resolve(definition, null, "2026-09-17 13:45:00", null);

        Assert.Equal("2026-09-17", resolution.Value);
    }

    [Fact]
    public void The_normaliser_is_applied_to_the_winning_value()
    {
        var resolution = AppSettingResolver.Resolve(TokenDef(), null, "list", null);

        // Stored lower case, reported in the canonical form the engine uses.
        Assert.Equal("LIST", resolution.Value);
    }

    [Fact]
    public void The_normaliser_is_applied_to_the_code_default_too()
    {
        var definition = TokenDef() with { DefaultValue = "full" };

        var resolution = AppSettingResolver.Resolve(definition, null, null, null);

        Assert.Equal("FULL", resolution.Value);
    }

    /// <summary>
    /// A Token definition with no tokens is a wiring error. It must throw rather than accept anything,
    /// because accepting anything would silently make an unvalidated value authoritative (I9).
    /// </summary>
    [Fact]
    public void A_token_definition_without_tokens_is_a_catalogue_error_not_a_quiet_pass()
    {
        var broken = TokenDef() with { AllowedTokens = null };

        var ex = Assert.Throws<InvalidOperationException>(
            () => AppSettingResolver.Resolve(broken, null, "ANYTHING", null));
        Assert.Contains("no allowed tokens", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveColumnBacked_uses_the_supplied_column_value()
    {
        var resolution = AppSettingResolver.ResolveColumnBacked(TokenDef(), "list");

        Assert.Equal("LIST", resolution.Value);
        Assert.Equal(AppSettingScope.Company, resolution.Provenance);
        Assert.False(resolution.IsDefault);
    }

    [Fact]
    public void ResolveColumnBacked_falls_back_when_the_column_is_blank()
    {
        var resolution = AppSettingResolver.ResolveColumnBacked(TokenDef(), "  ");

        Assert.True(resolution.IsDefault);
        Assert.Equal("FULL", resolution.Value);
        Assert.Null(resolution.RejectedReason);
    }

    [Fact]
    public void ResolveColumnBacked_reports_a_bad_column_token_rather_than_correcting_it()
    {
        var resolution = AppSettingResolver.ResolveColumnBacked(TokenDef(), "SOMETHING_ELSE");

        Assert.True(resolution.IsDefault);
        Assert.Equal("FULL", resolution.Value);
        Assert.NotNull(resolution.RejectedReason);
        Assert.Contains("SOMETHING_ELSE", resolution.RejectedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryNormaliseForStorage_accepts_what_the_resolver_accepts()
    {
        var definition = TokenDef();

        Assert.True(AppSettingResolver.TryNormaliseForStorage(definition, "list", out var canonical, out _));
        Assert.Equal("LIST", canonical);

        Assert.False(AppSettingResolver.TryNormaliseForStorage(definition, "nope", out var rejected, out var error));
        Assert.Null(rejected);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryNormaliseForStorage_rejects_a_blank_value_for_a_non_text_type()
    {
        Assert.False(AppSettingResolver.TryNormaliseForStorage(NumberDef(), "   ", out _, out var error));
        Assert.NotNull(error);
    }
}
