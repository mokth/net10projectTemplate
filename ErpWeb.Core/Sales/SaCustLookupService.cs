using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

public sealed class SaCustLookupService : ISaCustLookupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public SaCustLookupService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public Task<IReadOnlyList<IvCodeLookupRow>> ListTypesAsync(CancellationToken cancellationToken = default) =>
        ListTypesForAssignmentAsync(cancellationToken);

    public Task<IReadOnlyList<IvCodeLookupRow>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        ListGroupsForAssignmentAsync(cancellationToken);

    public Task<bool> IsValidTypeAsync(string? code, CancellationToken cancellationToken = default) =>
        ValidateTypeAssignmentAsync(code, null, cancellationToken);

    public Task<bool> IsValidGroupAsync(string? code, CancellationToken cancellationToken = default) =>
        ValidateGroupAssignmentAsync(code, null, cancellationToken);

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListTypesForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SaCustTypes
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive)
            .OrderBy(x => x.CustTypeCode)
            .Select(x => new IvCodeLookupRow { Code = x.CustTypeCode, Desc = x.CustTypeDesc ?? x.CustTypeCode })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListGroupsForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SaCustGroups
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode)
            .OrderBy(x => x.CustGroupCode)
            .Select(x => new IvCodeLookupRow { Code = x.CustGroupCode, Desc = x.CustGroupDesc ?? x.CustGroupCode })
            .ToListAsync(cancellationToken);
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

    public async Task<IReadOnlyList<IvCodeLookupRow>> ListDisGroupsForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SaDisGroups
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode)
            .OrderBy(x => x.GroupName)
            .ThenBy(x => x.PayCode)
            .Select(x => new { x.GroupName, x.Discount })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .Select(g => new IvCodeLookupRow
            {
                Code = g.Key,
                Rate = g.Max(x => x.Discount) is double discount
                    ? (decimal)discount
                    : 0m
            })
            .ToList();
    }

    public Task<IReadOnlyList<IvCodeLookupRow>> ListStatesForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.State, cancellationToken);

    public Task<IReadOnlyList<IvCodeLookupRow>> ListTaxGroupsForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.Tax, cancellationToken);

    public Task<IReadOnlyList<IvCodeLookupRow>> ListPayCodesForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.PayCode, cancellationToken);

    public Task<IReadOnlyList<IvCodeLookupRow>> ListIndustriesForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.Industry, cancellationToken);

    public Task<IReadOnlyList<IvCodeLookupRow>> ListChannelsForAssignmentAsync(CancellationToken cancellationToken = default) =>
        ListMsCodesAsync(IvMsCodeTypes.Channel, cancellationToken);

    public Task<bool> ValidateTypeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListTypesForAssignmentAsync, allowLegacyEmptyBypass: true, cancellationToken);

    public Task<bool> ValidateGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListGroupsForAssignmentAsync, allowLegacyEmptyBypass: true, cancellationToken);

    public Task<bool> ValidateAreaAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListAreasForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateCountryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListCountriesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateCurrencyAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListCurrenciesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateDisGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListDisGroupsForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateStateAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListStatesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateTaxGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListTaxGroupsForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidatePayCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListPayCodesForAssignmentAsync, allowLegacyEmptyBypass: false, cancellationToken);

    public Task<bool> ValidateIndustryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListIndustriesForAssignmentAsync, allowLegacyEmptyBypass: true, cancellationToken);

    public Task<bool> ValidateChannelAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default) =>
        ValidateLegacyOrFailClosed(code, existingCode, ListChannelsForAssignmentAsync, allowLegacyEmptyBypass: true, cancellationToken);

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
