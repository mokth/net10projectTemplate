using ErpWeb.Model.Repositories.Sales;

namespace ErpWeb.Core.Sales;

/// <summary>
/// The ONE translation from a <see cref="SaCdnListQuery"/> — the filter object the credit/debit-note grid
/// holds and the e-Invoice refresh-all scope receives — to the repository's <see cref="SaCdnSearchArgs"/>.
///
/// <para>
/// Keeping it in a single place is what makes "the filter the grid is showing" and "the set the
/// refresh-all will refresh" the same definition rather than two lookalike copies that drift apart.
/// Any new filter field must be added here and nowhere else, or that guarantee is lost.
/// </para>
///
/// <para>
/// Mirrors <see cref="SaInvoiceQueryMapper"/> deliberately: the two families must answer the same
/// question the same way.
/// </para>
/// </summary>
public static class SaCdnQueryMapper
{
    /// <summary>
    /// Maps the query, blanking whitespace-only values so they mean "no filter" rather than
    /// "match the empty string". <paramref name="skip"/>/<paramref name="take"/> are supplied by the
    /// caller because the grid and the refresh-all enumerate the same filter differently.
    /// </summary>
    public static SaCdnSearchArgs ToSearchArgs(SaCdnListQuery? query, int skip, int take)
    {
        query ??= new SaCdnListQuery();

        return new SaCdnSearchArgs(
            Type: (query.Type ?? string.Empty).Trim().ToUpperInvariant(),
            SearchText: string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim(),
            Status: string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
            IrbmStatus: string.IsNullOrWhiteSpace(query.IrbmStatus) ? null : query.IrbmStatus.Trim(),
            DateFrom: query.DateFrom,
            DateTo: query.DateTo,
            SortField: query.SortField,
            SortDescending: query.SortDescending,
            Skip: skip,
            Take: take);
    }
}
