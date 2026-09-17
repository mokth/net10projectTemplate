using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Settings;

/// <summary>
/// Projects the sales pricing method out of <c>Company.SalesPriceMethod</c>, which is where the engine
/// still reads it from.
///
/// <para>
/// This exists so <c>/admin/settings</c> can SHOW the pricing method beside every other setting, with its
/// provenance and its allowed tokens, without creating a second place to write it. The owning screen
/// remains <c>/admin/company</c>, and <c>AppSettingService.SaveAsync</c> refuses this definition.
/// </para>
///
/// <para>
/// The value is deliberately read RAW. Canonicalisation is applied by the resolver through the
/// definition's <c>Normalize</c>, which is <c>SaCompanyPriceMethod.Normalize</c> — so the projection can
/// never disagree with what the pricing engine sees.
/// </para>
/// </summary>
public sealed class SaPriceMethodSettingProvider : IAppSettingValueProvider
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public SaPriceMethodSettingProvider(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public string Module => AppSettingModules.Sales;

    public string Key => AppSettingCatalogue.SalesKeys.PriceMethod;

    public async Task<string?> ReadRawAsync(
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default)
    {
        // Company-only by definition. A branch is never consulted, which is what keeps BranchCode out of
        // every pricing key.
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Companies
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .Select(x => x.SalesPriceMethod)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
