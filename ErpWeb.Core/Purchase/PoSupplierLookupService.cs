using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed class PoSupplierLookupService : IPoSupplierLookupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public PoSupplierLookupService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListAreasForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.IvAreaCodes
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode)
            .OrderBy(x => x.AreaCode)
            .Select(x => new IvCodeLookupRow { Code = x.AreaCode, Desc = x.AreaDesc ?? x.AreaCode })
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyList<IvCodeLookupRow>> ListStatesForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.State, cancellationToken);

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListCountriesForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SaCountries
            .AsNoTracking()
            .OrderBy(x => x.CountryCode)
            .Select(x => new IvCodeLookupRow { Code = x.CountryCode, Desc = x.CountryName ?? x.CountryCode })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListCurrenciesForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SaCurrencies
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && (x.IsActive == null || x.IsActive == true))
            .OrderBy(x => x.CurrCode)
            .Select(x => new IvCodeLookupRow { Code = x.CurrCode, Desc = x.CurrDesc ?? x.CurrCode })
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyList<IvCodeLookupRow>> ListTaxGroupsForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.Tax, cancellationToken);

    public Task<IReadOnlyList<IvCodeLookupRow>> ListPayCodesForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.PayCode, cancellationToken);

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListBuyingTermsForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PoBuyingTerms
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive)
            .OrderBy(x => x.BuyingTerm)
            .Select(x => new IvCodeLookupRow { Code = x.BuyingTerm, Desc = x.Description ?? x.BuyingTerm })
            .ToListAsync(cancellationToken);
    }

    public Task<bool> ValidateAreaAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListAreasForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateStateAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListStatesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateCountryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListCountriesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateCurrencyAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListCurrenciesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateTaxGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListTaxGroupsForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidatePayCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListPayCodesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateBuyingTermAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListBuyingTermsForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    private async Task<IReadOnlyList<IvCodeLookupRow>> ListMsCodesAsync(string codeType, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.IvMsCodes
            .AsNoTracking()
            .Where(x => x.CodeType == codeType)
            .OrderBy(x => x.Code)
            .Select(x => new IvCodeLookupRow { Code = x.Code, Desc = x.Name ?? x.Code })
            .ToListAsync(cancellationToken);
    }

    private static async Task<bool> ValidateLegacyOrFailClosed(
        string? code,
        string? existingCode,
        Func<CancellationToken, Task<IReadOnlyList<IvCodeLookupRow>>> listAsync,
        bool allowLegacyEmptyBypass,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return true;
        }

        var trimmed = code.Trim();
        if (!string.IsNullOrWhiteSpace(existingCode) &&
            string.Equals(trimmed, existingCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var list = await listAsync(cancellationToken);
        if (list.Count == 0)
        {
            return allowLegacyEmptyBypass;
        }

        return list.Any(x => string.Equals(x.Code, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
