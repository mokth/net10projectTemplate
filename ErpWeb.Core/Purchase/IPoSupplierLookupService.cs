using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public interface IPoSupplierLookupService
{
    Task<IReadOnlyList<IvCodeLookupRow>> ListAreasForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListStatesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCountriesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCurrenciesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListTaxGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListPayCodesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListBuyingTermsForAssignmentAsync(CancellationToken cancellationToken = default);

    Task<bool> ValidateAreaAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateStateAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCountryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCurrencyAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateTaxGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidatePayCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateBuyingTermAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
}
