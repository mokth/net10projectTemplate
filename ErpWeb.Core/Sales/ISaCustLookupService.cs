using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public interface ISaCustLookupService
{
    Task<IReadOnlyList<IvCodeLookupRow>> ListTypesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListAreasForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCountriesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCurrenciesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListDisGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListStatesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListTaxGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListPayCodesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListIndustriesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListChannelsForAssignmentAsync(CancellationToken cancellationToken = default);

    Task<bool> ValidateTypeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateAreaAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCountryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCurrencyAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateDisGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateStateAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateTaxGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidatePayCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateIndustryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateChannelAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvCodeLookupRow>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListGroupsAsync(CancellationToken cancellationToken = default);
    Task<bool> IsValidTypeAsync(string? code, CancellationToken cancellationToken = default);
    Task<bool> IsValidGroupAsync(string? code, CancellationToken cancellationToken = default);
}
