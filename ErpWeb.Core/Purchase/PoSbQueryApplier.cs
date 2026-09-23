using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// The ONE definition of the self-billed list filters, shared by the two things that must never drift:
///
/// <list type="bullet">
/// <item>the GRID — <see cref="PoSbInvoiceService.SearchAsync"/> / <see cref="PoSbCdnService.SearchAsync"/>;</item>
/// <item>the no-selection E-STATUS refresh-all — <c>SaEInvoiceService.LoadSbRefreshCandidatesAsync</c>,
/// which resolves its candidate set through this same applier so "what the operator sees" and "what gets
/// refreshed" cannot drift apart.</item>
/// </list>
///
/// <para>
/// This is the self-billed twin of <c>SaInvoiceQueryMapper</c> / <c>SaCdnQueryMapper</c>. The self-billed
/// lists have no repository (the services query <c>AppDbContext</c> directly), so the shared definition
/// lives here instead. <b>Any new self-billed list filter field must be added HERE</b>, or the
/// grid-vs-refresh-all guarantee silently breaks. Same rule as the two sales mappers.
/// </para>
///
/// <para>
/// Tenant predicates are applied by the applier, not the caller, so a caller cannot forget one. Both
/// appliers return the ORDERED query: the default sort is <c>DocNo</c> ascending, matching
/// <see cref="PoSbQuery.SortField"/>'s default.
/// </para>
/// </summary>
public static class PoSbQueryApplier
{
    /// <summary>
    /// Applies the shared self-billed filter set plus the default sort to a self-billed invoice query.
    /// <paramref name="query"/>'s paging is NOT applied here — the caller pages (the grid per request,
    /// the refresh-all by cap).
    /// </summary>
    public static IQueryable<PoSbInvoice> Apply(
        IQueryable<PoSbInvoice> source, PoSbQuery query, string companyCode, string branchCode)
    {
        var q = source.Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToUpperInvariant();
            q = q.Where(x => x.Status == status);
        }

        // The e-Invoice status filter is deliberately SEPARATE from the ERP Status filter above: the two
        // read DIFFERENT columns and must never be swapped (the note origin picker relies on this).
        if (!string.IsNullOrWhiteSpace(query.IrbmStatus))
        {
            var irbmStatus = query.IrbmStatus.Trim().ToUpperInvariant();
            q = q.Where(x => x.IrbmStatus == irbmStatus);
        }

        if (!string.IsNullOrWhiteSpace(query.VendorCode))
        {
            var vendor = query.VendorCode.Trim();
            q = q.Where(x => x.VendorCode == vendor);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            q = q.Where(x => x.DocNo.Contains(term)
                             || x.VendorCode.Contains(term)
                             || (x.VendorName != null && x.VendorName.Contains(term)));
        }

        if (query.DateFrom is { } from)
        {
            q = q.Where(x => x.DocDate >= from.Date);
        }

        if (query.DateTo is { } to)
        {
            q = q.Where(x => x.DocDate < to.Date.AddDays(1));
        }

        return (query.SortField ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "docdate" => query.SortDescending ? q.OrderByDescending(x => x.DocDate) : q.OrderBy(x => x.DocDate),
            "totamnt" => query.SortDescending ? q.OrderByDescending(x => x.TotAmnt) : q.OrderBy(x => x.TotAmnt),
            _ => query.SortDescending ? q.OrderByDescending(x => x.DocNo) : q.OrderBy(x => x.DocNo)
        };
    }

    /// <summary>
    /// The credit/debit-note twin of <see cref="Apply(IQueryable{PoSbInvoice}, PoSbQuery, string, string)"/>.
    /// Identical filters plus two differences: the family predicate <c>x.Type == type</c> (the note grid
    /// only ever shows one of CN / DN), and the origin invoice number in the free-text search.
    /// </summary>
    /// <param name="type">
    /// The ALREADY normalized and validated <c>CN</c> / <c>DN</c> token — the caller owns that check
    /// (see <c>PoSbCalc.NormalizeType</c> / <c>IsValidType</c>) so an invalid value is a validation
    /// refusal, never an empty list.
    /// </param>
    public static IQueryable<PoSbCdn> Apply(
        IQueryable<PoSbCdn> source, PoSbQuery query, string companyCode, string branchCode, string type)
    {
        var q = source.Where(
            x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.Type == type);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToUpperInvariant();
            q = q.Where(x => x.Status == status);
        }

        // See the invoice overload: `Status` (ERP) and `IrbmStatus` (e-Invoice) read different columns.
        if (!string.IsNullOrWhiteSpace(query.IrbmStatus))
        {
            var irbmStatus = query.IrbmStatus.Trim().ToUpperInvariant();
            q = q.Where(x => x.IrbmStatus == irbmStatus);
        }

        if (!string.IsNullOrWhiteSpace(query.VendorCode))
        {
            var vendor = query.VendorCode.Trim();
            q = q.Where(x => x.VendorCode == vendor);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            q = q.Where(x => x.DocNo.Contains(term)
                             || x.VendorCode.Contains(term)
                             || (x.VendorName != null && x.VendorName.Contains(term))
                             || (x.OriginSbInvNo != null && x.OriginSbInvNo.Contains(term)));
        }

        if (query.DateFrom is { } from)
        {
            q = q.Where(x => x.DocDate >= from.Date);
        }

        if (query.DateTo is { } to)
        {
            q = q.Where(x => x.DocDate < to.Date.AddDays(1));
        }

        return (query.SortField ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "docdate" => query.SortDescending ? q.OrderByDescending(x => x.DocDate) : q.OrderBy(x => x.DocDate),
            "totamnt" => query.SortDescending ? q.OrderByDescending(x => x.TotAmnt) : q.OrderBy(x => x.TotAmnt),
            _ => query.SortDescending ? q.OrderByDescending(x => x.DocNo) : q.OrderBy(x => x.DocNo)
        };
    }
}
