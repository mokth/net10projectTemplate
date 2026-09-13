using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Purchase;

public interface IPoPrRepository
{
    Task<PoPr?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string prNo,
        CancellationToken cancellationToken = default);

    Task<PoPr?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string prNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<PoPr> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoPrSearchArgs args,
        CancellationToken cancellationToken = default);
}

public sealed class PoPrRepository : IPoPrRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PoPr.PrNo),
        nameof(PoPr.CreateDt),
        nameof(PoPr.Status),
        nameof(PoPr.Requester),
        nameof(PoPr.CreatedDate)
    };

    public async Task<PoPr?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string prNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (prNo ?? string.Empty).Trim();
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.PoPrs
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.POPR WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND PRNo = {no}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.PoPrs
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.PrNo == no,
                cancellationToken);
    }

    public Task<PoPr?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string prNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (prNo ?? string.Empty).Trim();
        return db.PoPrs
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.PrNo == no,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<PoPr> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoPrSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.PoPrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        if (!string.IsNullOrWhiteSpace(args.Status))
        {
            var status = args.Status.Trim();
            query = query.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(args.CreatedBy))
        {
            var createdBy = args.CreatedBy.Trim();
            query = query.Where(x => x.CreatedBy == createdBy);
        }

        if (args.DateFrom is DateTime from)
        {
            var fromDate = from.Date;
            query = query.Where(x => x.CreateDt >= fromDate);
        }

        if (args.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            query = query.Where(x => x.CreateDt < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.PrNo.Contains(term)
                || (x.Requester != null && x.Requester.Contains(term))
                || (x.Remarks != null && x.Remarks.Contains(term))
                || x.Details.Any(d =>
                    (d.ICode != null && d.ICode.Contains(term))
                    || (d.IDesc != null && d.IDesc.Contains(term))
                    || (d.VendorCd != null && d.VendorCd.Contains(term))
                    || (d.VendNm != null && d.VendNm.Contains(term))));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    private static IQueryable<PoPr> ApplySort(IQueryable<PoPr> query, string? sortField, bool desc)
    {
        var field = AllowedSortFields.Contains(sortField ?? string.Empty)
            ? sortField!
            : nameof(PoPr.CreateDt);

        return (field, desc) switch
        {
            (nameof(PoPr.PrNo), true) => query.OrderByDescending(x => x.PrNo),
            (nameof(PoPr.PrNo), false) => query.OrderBy(x => x.PrNo),
            (nameof(PoPr.Status), true) => query.OrderByDescending(x => x.Status),
            (nameof(PoPr.Status), false) => query.OrderBy(x => x.Status),
            (nameof(PoPr.Requester), true) => query.OrderByDescending(x => x.Requester),
            (nameof(PoPr.Requester), false) => query.OrderBy(x => x.Requester),
            (nameof(PoPr.CreatedDate), true) => query.OrderByDescending(x => x.CreatedDate),
            (nameof(PoPr.CreatedDate), false) => query.OrderBy(x => x.CreatedDate),
            (_, true) => query.OrderByDescending(x => x.CreateDt).ThenByDescending(x => x.PrNo),
            (_, false) => query.OrderBy(x => x.CreateDt).ThenBy(x => x.PrNo)
        };
    }
}
