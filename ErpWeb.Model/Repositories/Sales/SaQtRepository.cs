using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Sales;

public sealed record SaQtSearchArgs(
    string? SearchText,
    string? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public interface ISaQtRepository
{
    /// <summary>
    /// Locks the current revision (IsCurrent = 1). Valid only on the caller's active mutation
    /// transaction.
    /// </summary>
    Task<SaQt?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken = default);

    /// <summary>Locks a specific revision. Mutators must still verify IsCurrent = 1 before changing.</summary>
    Task<SaQt?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default);

    Task<SaQt?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken = default);

    Task<SaQt?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SaQt> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaQtSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaQt>> ListRevisionsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when a Sales Order already carries this exact quotation revision stamp. Works on both
    /// SQL Server and SQLite: the filtered unique index is a backstop, not the guard.
    /// </summary>
    Task<bool> HasConvertedSalesOrderAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default);

    /// <summary>The SO number created from this quotation revision, or null when there is none.</summary>
    Task<string?> FindConvertedSoNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default);
}

public sealed class SaQtRepository : ISaQtRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SaQt.QtNo),
        nameof(SaQt.QtDate),
        nameof(SaQt.ValidUntil),
        nameof(SaQt.Status),
        nameof(SaQt.CustCode),
        nameof(SaQt.TotAmnt),
        nameof(SaQt.CreatedDate)
    };

    public async Task<SaQt?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (qtNo ?? string.Empty).Trim();
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.SaQts
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.SaQT WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND QTNo = {no}
  AND IsCurrent = 1")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SaQts
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.QtNo == no
                    && x.IsCurrent,
                cancellationToken);
    }

    public async Task<SaQt?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (qtNo ?? string.Empty).Trim();
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.SaQts
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.SaQT WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND QTNo = {no}
  AND CustRel = {custRel}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SaQts
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.QtNo == no
                    && x.CustRel == custRel,
                cancellationToken);
    }

    public Task<SaQt?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (qtNo ?? string.Empty).Trim();
        return db.SaQts
            .Include(x => x.Details)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.QtNo == no
                    && x.IsCurrent,
                cancellationToken);
    }

    public Task<SaQt?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (qtNo ?? string.Empty).Trim();
        return db.SaQts
            .Include(x => x.Details)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.QtNo == no
                    && x.CustRel == custRel,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<SaQt> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaQtSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsCurrent);

        if (!string.IsNullOrWhiteSpace(args.Status))
        {
            var status = args.Status.Trim();
            query = query.Where(x => x.Status == status);
        }

        if (args.DateFrom is DateTime from)
        {
            var fromDate = from.Date;
            query = query.Where(x => x.QtDate >= fromDate);
        }

        if (args.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            query = query.Where(x => x.QtDate < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.QtNo.Contains(term)
                || x.CustCode.Contains(term)
                || (x.CustName != null && x.CustName.Contains(term))
                || (x.CustPo != null && x.CustPo.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    public async Task<IReadOnlyList<SaQt>> ListRevisionsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (qtNo ?? string.Empty).Trim();
        return await db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.QtNo == no)
            .OrderByDescending(x => x.CustRel)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> HasConvertedSalesOrderAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default) =>
        db.SaSos.AsNoTracking().AnyAsync(
            x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.QtNo == qtNo
                && x.QtCustRel == custRel,
            cancellationToken);

    public Task<string?> FindConvertedSoNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default) =>
        db.SaSos.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.QtNo == qtNo
                && x.QtCustRel == custRel)
            .OrderByDescending(x => x.SoNo)
            .Select(x => x.SoNo)
            .FirstOrDefaultAsync(cancellationToken);

    private static IQueryable<SaQt> ApplySort(IQueryable<SaQt> query, string? sortField, bool desc)
    {
        var field = AllowedSortFields.Contains(sortField ?? string.Empty)
            ? sortField!
            : nameof(SaQt.QtDate);

        return (field, desc) switch
        {
            (nameof(SaQt.QtNo), true) => query.OrderByDescending(x => x.QtNo),
            (nameof(SaQt.QtNo), false) => query.OrderBy(x => x.QtNo),
            (nameof(SaQt.ValidUntil), true) => query.OrderByDescending(x => x.ValidUntil),
            (nameof(SaQt.ValidUntil), false) => query.OrderBy(x => x.ValidUntil),
            (nameof(SaQt.Status), true) => query.OrderByDescending(x => x.Status),
            (nameof(SaQt.Status), false) => query.OrderBy(x => x.Status),
            (nameof(SaQt.CustCode), true) => query.OrderByDescending(x => x.CustCode),
            (nameof(SaQt.CustCode), false) => query.OrderBy(x => x.CustCode),
            (nameof(SaQt.TotAmnt), true) => query.OrderByDescending(x => x.TotAmnt),
            (nameof(SaQt.TotAmnt), false) => query.OrderBy(x => x.TotAmnt),
            (nameof(SaQt.CreatedDate), true) => query.OrderByDescending(x => x.CreatedDate),
            (nameof(SaQt.CreatedDate), false) => query.OrderBy(x => x.CreatedDate),
            (_, true) => query.OrderByDescending(x => x.QtDate).ThenByDescending(x => x.QtNo),
            (_, false) => query.OrderBy(x => x.QtDate).ThenBy(x => x.QtNo)
        };
    }
}
