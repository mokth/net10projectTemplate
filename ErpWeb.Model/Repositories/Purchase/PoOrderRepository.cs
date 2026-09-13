using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Purchase;

public interface IPoOrderRepository
{
    Task<PoOrder?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        short poRelNo,
        CancellationToken cancellationToken = default);

    Task<PoOrder?> LockLatestForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        CancellationToken cancellationToken = default);

    Task LockPoHeadersAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<(string PoNo, short PoRelNo)> keys,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoPrDetail>> LockPrDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<(string PrNo, short Line)> keys,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoPr>> LockPrHeadersAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<string> prNos,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoCjDetail>> LockCjDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<(string CjNo, int RelNo, int Line)> keys,
        CancellationToken cancellationToken = default);

    Task<PoOrder?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        short? poRelNo = null,
        CancellationToken cancellationToken = default);

    Task<short?> GetMaxRelNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<PoOrder> Rows, int TotalCount)> SearchLatestPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoOrderSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoOrder>> ListRevisionsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        CancellationToken cancellationToken = default);
}

public sealed class PoOrderRepository : IPoOrderRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PoOrder.PoNo),
        nameof(PoOrder.PoDate),
        nameof(PoOrder.Status),
        nameof(PoOrder.VendCode),
        nameof(PoOrder.Buyer),
        nameof(PoOrder.CreatedDate)
    };

    public async Task<PoOrder?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        short poRelNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (poNo ?? string.Empty).Trim();
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.PoOrders
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.POOrder WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND PONo = {no}
  AND PORelNo = {poRelNo}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.PoOrders
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.PoNo == no
                    && x.PoRelNo == poRelNo,
                cancellationToken);
    }

    public async Task<PoOrder?> LockLatestForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        CancellationToken cancellationToken = default)
    {
        var max = await GetMaxRelNoAsync(db, companyCode, branchCode, poNo, cancellationToken);
        if (max is null)
        {
            return null;
        }

        return await LockForUpdateAsync(db, companyCode, branchCode, poNo, max.Value, cancellationToken);
    }

    public async Task LockPoHeadersAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<(string PoNo, short PoRelNo)> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ordered = keys
            .Select(k => (PoNo: (k.PoNo ?? string.Empty).Trim(), k.PoRelNo))
            .Where(k => k.PoNo.Length > 0)
            .Distinct()
            .OrderBy(k => k.PoNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.PoRelNo)
            .ToList();

        foreach (var key in ordered)
        {
            await LockForUpdateAsync(db, companyCode, branchCode, key.PoNo, key.PoRelNo, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PoPrDetail>> LockPrDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<(string PrNo, short Line)> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var ordered = keys
            .Select(k => (PrNo: (k.PrNo ?? string.Empty).Trim(), k.Line))
            .Where(k => k.PrNo.Length > 0)
            .Distinct()
            .OrderBy(k => k.PrNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.Line)
            .ToList();

        var result = new List<PoPrDetail>();
        foreach (var key in ordered)
        {
            PoPrDetail? row;
            if (db.Database.IsSqlServer())
            {
                row = await db.PoPrDetails
                    .FromSqlInterpolated($@"
SELECT *
FROM dbo.POPRDtl WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND PRNo = {key.PrNo}
  AND Line = {key.Line}")
                    .AsTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                row = await db.PoPrDetails
                    .FirstOrDefaultAsync(
                        x => x.CompanyCode == company
                            && x.BranchCode == branch
                            && x.PrNo == key.PrNo
                            && x.Line == key.Line,
                        cancellationToken);
            }

            if (row is not null)
            {
                result.Add(row);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<PoPr>> LockPrHeadersAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<string> prNos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var ordered = prNos
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<PoPr>();
        foreach (var prNo in ordered)
        {
            PoPr? row;
            if (db.Database.IsSqlServer())
            {
                row = await db.PoPrs
                    .FromSqlInterpolated($@"
SELECT *
FROM dbo.POPR WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND PRNo = {prNo}")
                    .AsTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                row = await db.PoPrs
                    .FirstOrDefaultAsync(
                        x => x.CompanyCode == company
                            && x.BranchCode == branch
                            && x.PrNo == prNo,
                        cancellationToken);
            }

            if (row is not null)
            {
                result.Add(row);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<PoCjDetail>> LockCjDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<(string CjNo, int RelNo, int Line)> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var ordered = keys
            .Select(k => (CjNo: (k.CjNo ?? string.Empty).Trim(), k.RelNo, k.Line))
            .Where(k => k.CjNo.Length > 0)
            .Distinct()
            .OrderBy(k => k.CjNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.RelNo)
            .ThenBy(k => k.Line)
            .ToList();

        var result = new List<PoCjDetail>();
        foreach (var key in ordered)
        {
            PoCjDetail? row;
            if (db.Database.IsSqlServer())
            {
                row = await db.PoCjDetails
                    .FromSqlInterpolated($@"
SELECT *
FROM dbo.POCJDetail WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND CJNo = {key.CjNo}
  AND RelNo = {key.RelNo}
  AND Line = {key.Line}")
                    .AsTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                row = await db.PoCjDetails
                    .FirstOrDefaultAsync(
                        x => x.CompanyCode == company
                            && x.BranchCode == branch
                            && x.CjNo == key.CjNo
                            && x.RelNo == key.RelNo
                            && x.Line == key.Line,
                        cancellationToken);
            }

            if (row is not null)
            {
                result.Add(row);
            }
        }

        return result;
    }

    public async Task<PoOrder?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        short? poRelNo = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (poNo ?? string.Empty).Trim();

        if (poRelNo is null)
        {
            var max = await GetMaxRelNoAsync(db, company, branch, no, cancellationToken);
            if (max is null)
            {
                return null;
            }

            poRelNo = max;
        }

        return await db.PoOrders
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.PoNo == no
                    && x.PoRelNo == poRelNo.Value,
                cancellationToken);
    }

    public async Task<short?> GetMaxRelNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (poNo ?? string.Empty).Trim();
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        var max = await db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.PoNo == no)
            .Select(x => (short?)x.PoRelNo)
            .MaxAsync(cancellationToken);
        return max;
    }

    public async Task<(IReadOnlyList<PoOrder> Rows, int TotalCount)> SearchLatestPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoOrderSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        // Latest revision per PONo: ROW_NUMBER semantics via Max Rel join.
        var maxRelQuery = db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .GroupBy(x => x.PoNo)
            .Select(g => new { PoNo = g.Key, MaxRel = g.Max(x => x.PoRelNo) });

        var query =
            from h in db.PoOrders.AsNoTracking()
            join m in maxRelQuery
                on new { h.PoNo, Rel = h.PoRelNo } equals new { m.PoNo, Rel = m.MaxRel }
            where h.CompanyCode == company && h.BranchCode == branch
            select h;

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
            query = query.Where(x => x.PoDate >= fromDate);
        }

        if (args.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            query = query.Where(x => x.PoDate < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.PoNo.Contains(term)
                || (x.VendCode != null && x.VendCode.Contains(term))
                || (x.VendName != null && x.VendName.Contains(term))
                || (x.Buyer != null && x.Buyer.Contains(term))
                || (x.SiRemark != null && x.SiRemark.Contains(term))
                || x.Details.Any(d =>
                    (d.ICode != null && d.ICode.Contains(term))
                    || (d.IDesc != null && d.IDesc.Contains(term))
                    || (d.PrNo != null && d.PrNo.Contains(term))));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .Include(x => x.Details)
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    public async Task<IReadOnlyList<PoOrder>> ListRevisionsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string poNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (poNo ?? string.Empty).Trim();
        return await db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.PoNo == no)
            .OrderByDescending(x => x.PoRelNo)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<PoOrder> ApplySort(IQueryable<PoOrder> query, string? sortField, bool desc)
    {
        var field = AllowedSortFields.Contains(sortField ?? string.Empty)
            ? sortField!
            : nameof(PoOrder.PoDate);

        return (field, desc) switch
        {
            (nameof(PoOrder.PoNo), true) => query.OrderByDescending(x => x.PoNo),
            (nameof(PoOrder.PoNo), false) => query.OrderBy(x => x.PoNo),
            (nameof(PoOrder.Status), true) => query.OrderByDescending(x => x.Status),
            (nameof(PoOrder.Status), false) => query.OrderBy(x => x.Status),
            (nameof(PoOrder.VendCode), true) => query.OrderByDescending(x => x.VendCode),
            (nameof(PoOrder.VendCode), false) => query.OrderBy(x => x.VendCode),
            (nameof(PoOrder.Buyer), true) => query.OrderByDescending(x => x.Buyer),
            (nameof(PoOrder.Buyer), false) => query.OrderBy(x => x.Buyer),
            (nameof(PoOrder.CreatedDate), true) => query.OrderByDescending(x => x.CreatedDate),
            (nameof(PoOrder.CreatedDate), false) => query.OrderBy(x => x.CreatedDate),
            (_, true) => query.OrderByDescending(x => x.PoDate).ThenByDescending(x => x.PoNo),
            (_, false) => query.OrderBy(x => x.PoDate).ThenBy(x => x.PoNo)
        };
    }
}
