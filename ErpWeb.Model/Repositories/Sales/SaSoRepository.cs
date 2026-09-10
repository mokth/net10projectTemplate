using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Sales;

public sealed record SaSoSearchArgs(
    string? SearchText,
    string? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public interface ISaSoRepository
{
    /// <summary>
    /// Locks the current revision (IsCurrent = 1). Valid only on the caller's active mutation transaction.
    /// </summary>
    Task<SaSo?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks a specific revision. Mutators must still verify IsCurrent = 1 before changing.
    /// </summary>
    Task<SaSo?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        short custRel,
        CancellationToken cancellationToken = default);

    Task<SaSo?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken = default);

    Task<SaSo?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        short custRel,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SaSo> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaSoSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaSo>> ListRevisionsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken = default);
}

public sealed class SaSoRepository : ISaSoRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SaSo.SoNo),
        nameof(SaSo.SoDate),
        nameof(SaSo.Status),
        nameof(SaSo.CustCode),
        nameof(SaSo.TotAmnt),
        nameof(SaSo.CreatedDate)
    };

    public async Task<SaSo?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (soNo ?? string.Empty).Trim();
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.SaSos
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.SaSO WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND SONo = {no}
  AND IsCurrent = 1")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SaSos
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.SoNo == no
                    && x.IsCurrent,
                cancellationToken);
    }

    public async Task<SaSo?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        short custRel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (soNo ?? string.Empty).Trim();
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.SaSos
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.SaSO WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND SONo = {no}
  AND CustRel = {custRel}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SaSos
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.SoNo == no
                    && x.CustRel == custRel,
                cancellationToken);
    }

    public Task<SaSo?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (soNo ?? string.Empty).Trim();
        return db.SaSos
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.SoNo == no
                    && x.IsCurrent,
                cancellationToken);
    }

    public Task<SaSo?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        short custRel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (soNo ?? string.Empty).Trim();
        return db.SaSos
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.SoNo == no
                    && x.CustRel == custRel,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<SaSo> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaSoSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.SaSos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsCurrent);

        if (!string.IsNullOrWhiteSpace(args.Status))
        {
            var status = args.Status.Trim();
            query = query.Where(x => x.Status == status);
        }

        if (args.DateFrom is DateTime from)
        {
            var fromDate = from.Date;
            query = query.Where(x => x.SoDate >= fromDate);
        }

        if (args.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            query = query.Where(x => x.SoDate < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.SoNo.Contains(term)
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

    public async Task<IReadOnlyList<SaSo>> ListRevisionsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (soNo ?? string.Empty).Trim();
        return await db.SaSos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.SoNo == no)
            .OrderByDescending(x => x.CustRel)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<SaSo> ApplySort(IQueryable<SaSo> query, string? sortField, bool desc)
    {
        var field = AllowedSortFields.Contains(sortField ?? string.Empty)
            ? sortField!
            : nameof(SaSo.SoDate);

        return (field, desc) switch
        {
            (nameof(SaSo.SoNo), true) => query.OrderByDescending(x => x.SoNo),
            (nameof(SaSo.SoNo), false) => query.OrderBy(x => x.SoNo),
            (nameof(SaSo.Status), true) => query.OrderByDescending(x => x.Status),
            (nameof(SaSo.Status), false) => query.OrderBy(x => x.Status),
            (nameof(SaSo.CustCode), true) => query.OrderByDescending(x => x.CustCode),
            (nameof(SaSo.CustCode), false) => query.OrderBy(x => x.CustCode),
            (nameof(SaSo.TotAmnt), true) => query.OrderByDescending(x => x.TotAmnt),
            (nameof(SaSo.TotAmnt), false) => query.OrderBy(x => x.TotAmnt),
            (nameof(SaSo.CreatedDate), true) => query.OrderByDescending(x => x.CreatedDate),
            (nameof(SaSo.CreatedDate), false) => query.OrderBy(x => x.CreatedDate),
            (_, true) => query.OrderByDescending(x => x.SoDate).ThenByDescending(x => x.SoNo),
            (_, false) => query.OrderBy(x => x.SoDate).ThenBy(x => x.SoNo)
        };
    }
}
