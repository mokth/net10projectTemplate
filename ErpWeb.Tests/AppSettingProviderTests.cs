using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// The <c>ExistingColumn</c> contract: a definition backed by an existing column must have exactly one
/// registered provider, no provider may be orphaned, and a MISSING provider must fail loudly rather than
/// quietly resolving to the default (I9).
/// </summary>
public class AppSettingProviderTests
{
    private sealed class StubProvider(string module, string key) : IAppSettingValueProvider
    {
        public string Module { get; } = module;

        public string Key { get; } = key;

        public Task<string?> ReadRawAsync(
            AppSettingScope scope,
            string? companyCode,
            string? branchCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private static AppSettingProviderRegistry RegistryWithSaPriceMethod(AppSettingTestHost host) =>
        new([new SaPriceMethodSettingProvider(host.Factory)]);

    [Fact]
    public void Every_column_backed_definition_has_a_registered_provider()
    {
        var provider = new SaPriceMethodSettingProvider(
            new Mock<IDbContextFactory<AppDbContext>>().Object);

        var registry = new AppSettingProviderRegistry([provider]);

        foreach (var definition in AppSettingCatalogue.ColumnBacked)
        {
            Assert.True(
                registry.TryGet(definition.Module, definition.Key, out _),
                $"{definition.Module}.{definition.Key} is column-backed but has no IAppSettingValueProvider.");
        }
    }

    [Fact]
    public void No_registered_provider_is_orphaned()
    {
        var registry = RegistryWithSaPriceMethodCore();

        foreach (var provider in registry.All.Values)
        {
            var definition = AppSettingCatalogue.Find(provider.Module, provider.Key);

            Assert.NotNull(definition);
            Assert.Equal(AppSettingBacking.ExistingColumn, definition!.Backing);
        }
    }

    [Fact]
    public void Two_providers_for_the_same_setting_is_a_startup_error()
    {
        var definition = AppSettingCatalogue.SalesPriceMethod;

        var ex = Assert.Throws<InvalidOperationException>(() => new AppSettingProviderRegistry(
        [
            new StubProvider(definition.Module, definition.Key),
            new StubProvider(definition.Module.ToLowerInvariant(), definition.Key.ToLowerInvariant())
        ]));

        Assert.Contains("Exactly one provider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGet_is_case_insensitive_and_rejects_a_blank_key()
    {
        var registry = RegistryWithSaPriceMethodCore();

        Assert.True(registry.TryGet("sales", "price_method", out _));
        Assert.False(registry.TryGet("SALES", "NOT_A_KEY", out _));
        Assert.False(registry.TryGet(null, null, out _));
    }

    [Fact]
    public async Task The_price_method_provider_reads_the_company_column_or_null()
    {
        await using var host = AppSettingTestHost.Create();
        await host.SeedCompanyAsync("DEMO", salesPriceMethod: SaCompanyPriceMethod.PriceListOnly);

        var provider = new SaPriceMethodSettingProvider(host.Factory);

        var stored = await provider.ReadRawAsync(AppSettingScope.Company, "DEMO", "HQ");
        Assert.Equal(SaCompanyPriceMethod.PriceListOnly, stored);

        // No company target, or an unknown company: nothing to project.
        Assert.Null(await provider.ReadRawAsync(AppSettingScope.Company, null, "HQ"));
        Assert.Null(await provider.ReadRawAsync(AppSettingScope.Company, "NOPE", "HQ"));
    }

    /// <summary>
    /// A missing provider is a deployment defect, not a data condition: it must throw so the mistake is
    /// visible, instead of silently serving the code default forever.
    /// </summary>
    [Fact]
    public async Task A_missing_provider_fails_loudly_instead_of_silently_using_the_default()
    {
        await using var host = AppSettingTestHost.Create();

        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns("DEMO");

        var serviceWithNoProviders = new AppSettingService(
            host.Factory,
            new TenantScopeContext(current.Object),
            NoAccessRights(),
            new MemoryCache(new MemoryCacheOptions()),
            new AppSettingCacheVersions(),
            new AppSettingProviderRegistry([]),
            NullLogger<AppSettingService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => serviceWithNoProviders.GetValueAsync(
            AppSettingModules.Sales,
            AppSettingCatalogue.SalesKeys.PriceMethod,
            AppSettingScope.Company,
            "DEMO",
            null));

        Assert.Contains("no IAppSettingValueProvider is registered", ex.Message, StringComparison.Ordinal);
    }

    private static AppSettingProviderRegistry RegistryWithSaPriceMethodCore() =>
        new([new SaPriceMethodSettingProvider(new Mock<IDbContextFactory<AppDbContext>>().Object)]);

    private static IAccessRightService NoAccessRights()
    {
        var mock = new Mock<IAccessRightService>();
        mock.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock.Object;
    }
}
