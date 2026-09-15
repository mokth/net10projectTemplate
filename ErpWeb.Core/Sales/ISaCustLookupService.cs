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

    /// <summary>
    /// Ungated, company-scoped customer list for sales-master pickers. <c>ISaCustService.SearchAsync</c>
    /// gates on the customer-master menu and the sales-order lookups on sales-order access, so either
    /// would lock a sales-master user out of their own popup. Bounded by design: the predicates, the
    /// ordering, the projection and the <c>Take</c> all run in the database, so a Blazor Server circuit
    /// never receives the customer table. Blank/whitespace <paramref name="searchText"/> returns the
    /// first <paramref name="maxRows"/> customers by code.
    /// </summary>
    Task<IReadOnlyList<IvCodeLookupRow>> SearchCustomersAsync(
        string? searchText = null,
        int maxRows = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Salesmen of the caller's company that may be assigned to a customer. Ungated: the gated
    /// <c>ISaSalesRefService.ListSalesRepsAsync</c> requires the SALES_SALES_REP menu, which a
    /// customer-only user does not have. Legacy <c>IsActive == NULL</c> is intentionally treated as
    /// <b>active</b>, matching the shipped post-time salesman checks — do not tighten to <c>== true</c>.
    /// </summary>
    Task<IReadOnlyList<IvCodeLookupRow>> ListSalesRepsForAssignmentAsync(CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Server-side check for <c>SaCust.SalesmanCode</c>, the last customer reference field that nothing
    /// validated (same defect class D-6 fixed for SubGroupCode/CustPriceCode). The three clauses are:
    /// blank is allowed; a non-blank value must exist in the caller's company; and the value already on
    /// the row is tolerated, so a legacy free-text salesman code cannot block an unrelated edit.
    /// A new value naming another company's salesman is therefore rejected (clause 2).
    /// </summary>
    Task<bool> ValidateSalesmanCodeAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateIndustryAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);
    Task<bool> ValidateChannelAssignmentAsync(string? code, string? existingCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvCodeLookupRow>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IvCodeLookupRow>> ListGroupsAsync(CancellationToken cancellationToken = default);
    Task<bool> IsValidTypeAsync(string? code, CancellationToken cancellationToken = default);
    Task<bool> IsValidGroupAsync(string? code, CancellationToken cancellationToken = default);
}
