using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed class PoSupplierLookupService : IPoSupplierLookupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPoSupplierRepository _suppliers;

    public PoSupplierLookupService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPoSupplierRepository? suppliers = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _suppliers = suppliers ?? new PoSupplierRepository(dbFactory);
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

    /// <summary>
    /// The supplier's tax group is the SALES tax master, <c>SaTaxGroup.TaxGrCode</c> - NOT
    /// <c>IvMsCode</c> type <c>TAX</c> (that code type is the LHDN tax-type list, a different concept).
    /// This list is also the save gate's source (<see cref="ValidateTaxGroupAssignmentAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<IvCodeLookupRow>> ListTaxGroupsForAssignmentAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SaTaxGroups
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new IvCodeLookupRow { Code = x.TaxGrCode, Desc = x.TaxGrDesc ?? x.TaxGrCode })
            .ToListAsync(cancellationToken);
    }

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

    public async Task<IReadOnlyList<IvCodeLookupRow>> SearchSuppliersAsync(
        string? searchText = null,
        int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return [];
        }

        var limit = Math.Clamp(maxRows, 1, 500);
        var term = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PoSuppliers
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.IsActive);

        if (term is not null)
        {
            query = query.Where(x => x.SuppCode.Contains(term) || x.SuppName.Contains(term));
        }

        return await query
            .OrderBy(x => x.SuppCode)
            .Select(x => new IvCodeLookupRow { Code = x.SuppCode, Desc = x.SuppName })
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<LargeLookupResolveResult<PoSupplierLookupRow>> ResolveSupplierAsync(
        string suppCode,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return LargeLookupResolveResult<PoSupplierLookupRow>.Fail("Invalid company context.");
        }

        var code = (suppCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return LargeLookupResolveResult<PoSupplierLookupRow>.Fail("Supplier code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await _suppliers.GetByCodeAsync(
            db,
            scope.CompanyCode,
            scope.BranchCode!,
            code,
            includeChildren: false,
            cancellationToken);
        if (row is null || !row.IsActive)
        {
            return LargeLookupResolveResult<PoSupplierLookupRow>.Fail($"Supplier '{code}' was not found.");
        }

        return LargeLookupResolveResult<PoSupplierLookupRow>.Ok(MapSupplier(row));
    }

    public async Task<LargeLookupPage<PoSupplierLookupRow>> SearchSuppliersPagedAsync(
        LargeLookupSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return LargeLookupPage<PoSupplierLookupRow>.Fail("Invalid company context.");
        }

        request ??= new LargeLookupSearchRequest();
        var (rows, total) = await _suppliers.SearchPagedAsync(
            scope.CompanyCode,
            scope.BranchCode!,
            new PoSupplierSearchArgs
            {
                SearchText = request.SearchText,
                IsActive = true,
                Skip = request.NormalizedSkip,
                Take = request.NormalizedTake,
                SortField = nameof(PoSupplier.SuppCode),
                SortDescending = false
            },
            cancellationToken);

        return LargeLookupPage<PoSupplierLookupRow>.Ok(rows.Select(MapSupplier).ToList(), total);
    }

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

    private static PoSupplierLookupRow MapSupplier(PoSupplier x) =>
        new()
        {
            SuppCode = x.SuppCode,
            SuppName = x.SuppName,
            Currency = x.Currency
        };
}
