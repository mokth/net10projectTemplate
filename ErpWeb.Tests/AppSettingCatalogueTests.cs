using ErpWeb.Core.Sales;
using ErpWeb.Core.Settings;

namespace ErpWeb.Tests;

/// <summary>
/// Integrity of the catalogue itself. These are the tests that stop a half-declared setting shipping:
/// every definition must be resolvable, parseable, scoped and — for a token — enumerable.
/// </summary>
public class AppSettingCatalogueTests
{
    [Fact]
    public void Every_module_key_pair_is_declared_once()
    {
        var duplicates = AppSettingCatalogue.All
            .GroupBy(d => d.LookupKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_definition_targets_a_declared_module()
    {
        var unknown = AppSettingCatalogue.All
            .Where(d => !AppSettingModules.IsKnown(d.Module))
            .Select(d => $"{d.Module}.{d.Key}")
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void Every_definition_declares_at_least_one_scope()
    {
        var unscoped = AppSettingCatalogue.All
            .Where(d => d.AllowedScopes == AppSettingScope.None)
            .Select(d => $"{d.Module}.{d.Key}")
            .ToList();

        Assert.Empty(unscoped);
    }

    [Fact]
    public void Every_definition_has_a_key_and_a_description()
    {
        foreach (var definition in AppSettingCatalogue.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Key), $"{definition.Module} has a blank key.");
            Assert.False(
                string.IsNullOrWhiteSpace(definition.Description),
                $"{definition.Module}.{definition.Key} has no description, so the admin screen cannot explain it.");
        }
    }

    /// <summary>
    /// A default that cannot be parsed as its own type would make the fallback path throw-or-nonsense
    /// the moment a row was deleted.
    /// </summary>
    [Fact]
    public void Every_default_is_a_usable_value_for_its_own_type()
    {
        foreach (var definition in AppSettingCatalogue.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(definition.DefaultValue),
                $"{definition.Module}.{definition.Key} (I1) must declare a code default; resolution relies on it.");

            var ok = AppSettingResolver.TryNormaliseForStorage(
                definition,
                definition.DefaultValue,
                out _,
                out var error);

            Assert.True(ok, $"{definition.Module}.{definition.Key} default '{definition.DefaultValue}' is unusable: {error}");
        }
    }

    /// <summary>
    /// A Token definition with no allowed tokens can never validate anything, so it would silently accept
    /// whatever was stored. That is a catalogue bug and must fail here, not at runtime (I9).
    /// </summary>
    [Fact]
    public void Every_token_definition_declares_its_allowed_tokens()
    {
        var broken = AppSettingCatalogue.All
            .Where(d => d.Type == AppSettingType.Token && !d.HasTokens)
            .Select(d => $"{d.Module}.{d.Key}")
            .ToList();

        Assert.Empty(broken);
    }

    [Fact]
    public void A_non_token_definition_does_not_declare_tokens()
    {
        var suspicious = AppSettingCatalogue.All
            .Where(d => d.Type != AppSettingType.Token && d.AllowedTokens is { Count: > 0 })
            .Select(d => $"{d.Module}.{d.Key}")
            .ToList();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void Find_is_case_insensitive_and_rejects_unknown_keys()
    {
        var found = AppSettingCatalogue.Find("sales", "price_method");

        Assert.NotNull(found);
        Assert.Equal(AppSettingCatalogue.SalesPriceMethod.Key, found!.Key);

        // A row inserted by hand under any other key is NOT a setting.
        Assert.Null(AppSettingCatalogue.Find("SALES", "NOT_A_REAL_KEY"));
        Assert.Null(AppSettingCatalogue.Find("NOT_A_MODULE", AppSettingCatalogue.SalesKeys.PriceMethod));
        Assert.Null(AppSettingCatalogue.Find(null, null));
        Assert.Null(AppSettingCatalogue.Find("SALES", "   "));
    }

    [Fact]
    public void ForModule_returns_only_that_module()
    {
        var sales = AppSettingCatalogue.ForModule(AppSettingModules.Sales);

        Assert.NotEmpty(sales);
        Assert.All(sales, d => Assert.Equal(AppSettingModules.Sales, d.Module, ignoreCase: true));

        Assert.Empty(AppSettingCatalogue.ForModule("NOT_A_MODULE"));
        Assert.Empty(AppSettingCatalogue.ForModule(null));
    }

    /// <summary>
    /// The sales pricing method is a PROJECTION over Company.SalesPriceMethod. Company-only is
    /// load-bearing: BranchCode must never enter a pricing key (sales-price-engine.md §5 rule 9).
    /// </summary>
    [Fact]
    public void Price_method_is_company_only_column_backed_and_uses_the_engine_token_list()
    {
        var definition = AppSettingCatalogue.SalesPriceMethod;

        Assert.Equal(AppSettingBacking.ExistingColumn, definition.Backing);
        Assert.Equal(AppSettingScope.Company, definition.AllowedScopes);
        // BranchCode must never enter a pricing key (sales-price-engine.md §5 rule 9).
        Assert.False(definition.AllowedScopes.HasFlag(AppSettingScope.Branch));
        Assert.Equal(SaCompanyPriceMethod.All, definition.AllowedTokens);
        Assert.Equal(SaCompanyPriceMethod.CustomerItemAndList, definition.DefaultValue);

        // The projection must report the same canonical token the engine uses.
        Assert.Equal(
            SaCompanyPriceMethod.ItemDefaultOnly,
            definition.Normalize!(SaCompanyPriceMethod.ItemDefaultOnly.ToLowerInvariant()));
    }

    [Fact]
    public void Column_backed_definitions_are_exactly_the_read_only_ones()
    {
        Assert.Contains(AppSettingCatalogue.SalesPriceMethod, AppSettingCatalogue.ColumnBacked);
        Assert.All(
            AppSettingCatalogue.ColumnBacked,
            d => Assert.Equal(AppSettingBacking.ExistingColumn, d.Backing));
    }

    /// <summary>
    /// The three reserved modules must already be valid targets, so adding Planning / Production / QA
    /// settings later is a catalogue entry and nothing else.
    /// </summary>
    [Theory]
    [InlineData(AppSettingModules.Planning)]
    [InlineData(AppSettingModules.Production)]
    [InlineData(AppSettingModules.Qa)]
    public void Every_reserved_module_already_has_a_placeholder(string module)
    {
        Assert.NotEmpty(AppSettingCatalogue.ForModule(module));
    }

    [Fact]
    public void Every_declared_module_is_reachable_from_the_catalogue_or_reserved()
    {
        // Guards the other direction: a module constant nobody can select is dead weight.
        foreach (var module in AppSettingModules.All)
        {
            Assert.NotEmpty(AppSettingCatalogue.ForModule(module));
        }
    }
}
