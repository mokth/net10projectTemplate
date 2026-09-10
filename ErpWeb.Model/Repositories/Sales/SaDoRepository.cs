using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Sales;

public sealed record SaDoSearchArgs(
    string? SearchText,
    string? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public interface ISaDoRepository
{
    Task<SaDo?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string doNo,
        CancellationToken cancellationToken = default);

    Task<SaDo?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string doNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SaDo> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaDoSearchArgs args,
        CancellationToken cancellationToken = default);
}

public sealed class SaDoRepository : ISaDoRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SaDo.DoNo),
        nameof(SaDo.DoDate),
        nameof(SaDo.Status),
        nameof(SaDo.CustCode),
        nameof(SaDo.TotAmnt),
        nameof(SaDo.CreatedDate)
    };

    public async Task<SaDo?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string doNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (doNo ?? string.Empty).Trim();

        if (db.Database.IsSqlServer())
        {
            return await db.SaDos
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.SaDo WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND DoNo = {no}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SaDos
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DoNo == no,
                cancellationToken);
    }

    public Task<SaDo?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string doNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (doNo ?? string.Empty).Trim();
        return db.SaDos
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DoNo == no,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<SaDo> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaDoSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.SaDos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        if (!string.IsNullOrWhiteSpace(args.Status))
        {
            var status = args.Status.Trim();
            query = query.Where(x => x.Status == status);
        }

        if (args.DateFrom is DateTime from)
        {
            var fromDate = from.Date;
            query = query.Where(x => x.DoDate >= fromDate);
        }

        if (args.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            query = query.Where(x => x.DoDate < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.DoNo.Contains(term)
                || x.CustCode.Contains(term)
                || (x.CustName != null && x.CustName.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    private static IQueryable<SaDo> ApplySort(IQueryable<SaDo> query, string? sortField, bool desc)
    {
        var field = AllowedSortFields.Contains(sortField ?? string.Empty)
            ? sortField!
            : nameof(SaDo.DoDate);

        return (field, desc) switch
        {
            (nameof(SaDo.DoNo), true) => query.OrderByDescending(x => x.DoNo),
            (nameof(SaDo.DoNo), false) => query.OrderBy(x => x.DoNo),
            (nameof(SaDo.Status), true) => query.OrderByDescending(x => x.Status),
            (nameof(SaDo.Status), false) => query.OrderBy(x => x.Status),
            (nameof(SaDo.CustCode), true) => query.OrderByDescending(x => x.CustCode),
            (nameof(SaDo.CustCode), false) => query.OrderBy(x => x.CustCode),
            (nameof(SaDo.TotAmnt), true) => query.OrderByDescending(x => x.TotAmnt),
            (nameof(SaDo.TotAmnt), false) => query.OrderBy(x => x.TotAmnt),
            (nameof(SaDo.CreatedDate), true) => query.OrderByDescending(x => x.CreatedDate),
            (nameof(SaDo.CreatedDate), false) => query.OrderBy(x => x.CreatedDate),
            (_, true) => query.OrderByDescending(x => x.DoDate).ThenByDescending(x => x.DoNo),
            (_, false) => query.OrderBy(x => x.DoDate).ThenBy(x => x.DoNo)
        };
    }
}
