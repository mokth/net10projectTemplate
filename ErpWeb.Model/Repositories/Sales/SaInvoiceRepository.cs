using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Sales;

/// <summary>
/// Paging and filter arguments for the sales-invoice list.
///
/// <para>
/// <paramref name="IrbmStatus"/> filters on the e-Invoice lifecycle status (<c>IRBMStatus</c>) and is
/// compared with plain equality: the column is collated <c>SQL_Latin1_General_CP1_CI_AS</c>
/// (case-insensitive, verified against the live ERPWeb database on 2026-09-22), so a legacy
/// title-case value such as <c>Submitted</c> matches the uppercase <c>EInvoiceStatuses</c> constant
/// without wrapping the column in a function. <c>NULL</c> means "no e-Invoice filter".
/// </para>
/// </summary>
public sealed record SaInvoiceSearchArgs(
    string? SearchText,
    string? Status,
    string? IrbmStatus,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public interface ISaInvoiceRepository
{
    Task<SaInvoice?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        CancellationToken cancellationToken = default);

    Task<SaInvoice?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SaInvoice> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaInvoiceSearchArgs args,
        CancellationToken cancellationToken = default);
}

public sealed class SaInvoiceRepository : ISaInvoiceRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SaInvoice.InvNo),
        nameof(SaInvoice.InvDate),
        nameof(SaInvoice.Status),
        nameof(SaInvoice.CustCode),
        nameof(SaInvoice.TotAmnt),
        nameof(SaInvoice.CreatedDate)
    };

    public async Task<SaInvoice?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (invNo ?? string.Empty).Trim();

        if (db.Database.IsSqlServer())
        {
            return await db.SaInvoices
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.SaInvoice WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND InvNo = {no}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SaInvoices
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.InvNo == no,
                cancellationToken);
    }

    public Task<SaInvoice?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (invNo ?? string.Empty).Trim();
        return db.SaInvoices
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.InvNo == no,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<SaInvoice> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaInvoiceSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DeletedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(args.Status))
        {
            var status = args.Status.Trim();
            query = query.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(args.IrbmStatus))
        {
            // Plain equality on purpose - the column's collation (SQL_Latin1_General_CP1_CI_AS) already
            // makes the comparison case-insensitive, so legacy title-case values such as "Submitted"
            // match. Wrapping the column in UPPER() would make the index unusable for no benefit.
            var irbmStatus = args.IrbmStatus.Trim();
            query = query.Where(x => x.IrbmStatus == irbmStatus);
        }

        if (args.DateFrom is DateTime from)
        {
            var fromDate = from.Date;
            query = query.Where(x => x.InvDate >= fromDate);
        }

        if (args.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            query = query.Where(x => x.InvDate < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.InvNo.Contains(term)
                || x.CustCode.Contains(term)
                || (x.DoNo != null && x.DoNo.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    private static IQueryable<SaInvoice> ApplySort(IQueryable<SaInvoice> query, string? sortField, bool desc)
    {
        var field = AllowedSortFields.Contains(sortField ?? string.Empty)
            ? sortField!
            : nameof(SaInvoice.InvDate);

        return (field, desc) switch
        {
            (nameof(SaInvoice.InvNo), true) => query.OrderByDescending(x => x.InvNo),
            (nameof(SaInvoice.InvNo), false) => query.OrderBy(x => x.InvNo),
            (nameof(SaInvoice.Status), true) => query.OrderByDescending(x => x.Status),
            (nameof(SaInvoice.Status), false) => query.OrderBy(x => x.Status),
            (nameof(SaInvoice.CustCode), true) => query.OrderByDescending(x => x.CustCode),
            (nameof(SaInvoice.CustCode), false) => query.OrderBy(x => x.CustCode),
            (nameof(SaInvoice.TotAmnt), true) => query.OrderByDescending(x => x.TotAmnt),
            (nameof(SaInvoice.TotAmnt), false) => query.OrderBy(x => x.TotAmnt),
            (nameof(SaInvoice.CreatedDate), true) => query.OrderByDescending(x => x.CreatedDate),
            (nameof(SaInvoice.CreatedDate), false) => query.OrderBy(x => x.CreatedDate),
            (_, true) => query.OrderByDescending(x => x.InvDate).ThenByDescending(x => x.InvNo),
            (_, false) => query.OrderBy(x => x.InvDate).ThenBy(x => x.InvNo)
        };
    }
}
