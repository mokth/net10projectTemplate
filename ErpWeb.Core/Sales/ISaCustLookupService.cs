using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public interface ISaCustLookupService
{
    Task<IReadOnlyList<IvCodeLookupRow>> ListTypesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListSubGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListAreasForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCountriesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListCurrenciesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListDisGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListStatesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListTaxGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListPayCodesForAssignmentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Active price lists of the caller's company (D2-23). Retired lists stay out of the picker, but
    /// <see cref="ValidateCustPriceCodeAssignmentAsync"/> still tolerates a value already on the row.
    /// </summary>
    Task<IReadOnlyList<IvCodeLookupRow>> ListPriceGroupsForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListIndustriesForAssignmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListChannelsForAssignmentAsync(CancellationToken cancellationToken = default);

    Task<bool> ValidateTypeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    /// <summary>
    /// Server-side check for <c>SaCust.SubGroupCode</c> (D-6). A lookup widget is not an integrity
    /// boundary: blank is allowed, a non-blank value must exist in the caller's company, and the
    /// value already on the row is tolerated so legacy free text cannot block an unrelated edit.
    /// </summary>
    Task<bool> ValidateSubGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateAreaAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCountryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateCurrencyAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateDisGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateStateAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateTaxGroupAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidatePayCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-side check for <c>SaCust.CustPriceCode</c> (D2-23, the same three-clause contract as D-6):
    /// blank is allowed; a non-blank value must exist as an active price list in the caller's company;
    /// the value already on the row is tolerated so legacy free text cannot block an unrelated edit.
    /// The assignment list fails closed when empty.
    /// </summary>
    Task<bool> ValidateCustPriceCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateIndustryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateChannelAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvCodeLookupRow>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListGroupsAsync(CancellationToken cancellationToken = default);
    Task<bool> IsValidTypeAsync(string? code, CancellationToken cancellationToken = default);
    Task<bool> IsValidGroupAsync(string? code, CancellationToken cancellationToken = default);
}
