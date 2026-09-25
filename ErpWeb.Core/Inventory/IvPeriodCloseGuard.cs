using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// The one authority that answers "does this stock-driving date fall in a CLOSED inventory period?".
///
/// <para>
/// It is an <b>internal static</b> helper on purpose: the ten posting cores and the D14 document gates
/// live in the same assembly, and no DI surface, constructor change or test-fixture update should be
/// required to call it. The date comparison is done in C# against the <c>date</c> column
/// (<see cref="IvPeriodCloseHdr.PeriodTo"/>) — never translated to SQL — so there is no provider
/// translation risk.
/// </para>
///
/// <para>
/// <b>Keyed on the movement's own date, never on "today".</b> A rollback of an August batch must be
/// refused because August is closed, even when the rollback runs in September (Critical finding 2).
/// Only <see cref="IvPeriodCloseStatuses.Closed"/> rows count; a reopened period guards nothing.
/// </para>
/// </summary>
internal static class IvPeriodCloseGuard
{
    /// <summary>
    /// The latest <c>PeriodTo</c> among CLOSED periods for the tenant, or <c>null</c> when none exist.
    /// </summary>
    public static async Task<DateTime?> ClosedThroughAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        // A single indexed MAX() per call — deliberately not cached: caching a lock state is a
        // correctness risk, not an optimisation.
        var periods = await db.IvPeriodCloseHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.Status == IvPeriodCloseStatuses.Closed)
            .Select(x => x.PeriodTo)
            .ToListAsync(cancellationToken);

        if (periods.Count == 0)
        {
            return null;
        }

        return periods.Max();
    }

    /// <summary>
    /// Returns a refusal message when <paramref name="trxDtTime"/>.Date falls on or before the
    /// closed-through date of a CLOSED period for the tenant, otherwise <c>null</c> (allowed).
    /// </summary>
    public static async Task<string?> EnsureOpenAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        DateTime trxDtTime,
        CancellationToken cancellationToken = default)
    {
        var closedThrough = await ClosedThroughAsync(db, companyCode, branchCode, cancellationToken);
        if (closedThrough is not DateTime through)
        {
            return null;
        }

        var date = trxDtTime.Date;
        if (date <= through.Date)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                IvPeriodCloseLimits.RefusalTemplate,
                date,
                through.Date);
        }

        return null;
    }
}
