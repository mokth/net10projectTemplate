using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;

namespace ErpWeb.Core.Purchase;

public sealed class PoSupplierLookupRow
{
    public string SuppCode { get; init; } = string.Empty;
    public string? SuppName { get; init; }
    public string? Currency { get; init; }
    public string DisplayText =>
        string.IsNullOrWhiteSpace(SuppName) ? SuppCode : $"{SuppCode} — {SuppName}";
}

public interface IPoSupplierLookupService
{
    Task<IReadOnlyList<IvCodeLookupRow>> ListAreasForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListStatesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCountriesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCurrenciesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListTaxGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListPayCodesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListBuyingTermsForAssignmentAsync(CancellationToken cancellationToken = default);

    /// <summary>Supplier search for inquiry toolbar combos (Sales <c>SearchCustomersAsync</c> shape).</summary>
    Task<IReadOnlyList<IvCodeLookupRow>> SearchSuppliersAsync(
        string? searchText = null,
        int maxRows = 200,
        CancellationToken cancellationToken = default);

    /// <summary>Exact supplier-code resolve for smart lookup. Active, company+branch scoped.</summary>
    Task<LargeLookupResolveResult<PoSupplierLookupRow>> ResolveSupplierAsync(
        string suppCode,
        CancellationToken cancellationToken = default);

    /// <summary>Server-paged active supplier search for smart lookup popups.</summary>
    Task<LargeLookupPage<PoSupplierLookupRow>> SearchSuppliersPagedAsync(
        LargeLookupSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateAreaAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateStateAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCountryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCurrencyAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateTaxGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidatePayCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateBuyingTermAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
}
