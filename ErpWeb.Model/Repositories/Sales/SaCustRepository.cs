using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Sales;

public interface ISaCustRepository
{
    Task<SaCust?> GetByCodeAsync(
        AppDbContext db,
        string companyCode,
        string custCode,
        bool includeChildren,
        CancellationToken cancellationToken = default);

    Task<SaCust?> GetTrackedAsync(
        AppDbContext db,
        string companyCode,
        string custCode,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string companyCode,
        string custCode,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SaCust> Rows, int TotalCount)> SearchPagedAsync(
        string companyCode,
        SaCustSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaCust>> ListExportAsync(
        string companyCode,
        SaCustSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>> CountReferencesBulkAsync(
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);
}

public sealed class SaCustRepository : ISaCustRepository
{
    public const int MaxExportRows = 50_000;
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SaCust.CustCode),
        nameof(SaCust.CustName),
        nameof(SaCust.CustType),
        nameof(SaCust.CustGroupCode),
        nameof(SaCust.SalesmanCode),
        nameof(SaCust.City),
        nameof(SaCust.Tel),
        nameof(SaCust.IsActive)
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public SaCustRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<SaCust?> GetByCodeAsync(
        AppDbContext db,
        string companyCode,
        string custCode,
        bool includeChildren,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (custCode ?? string.Empty).Trim();

        IQueryable<SaCust> query = db.SaCusts.AsNoTracking();
        if (includeChildren)
        {
            query = query
                .Include(x => x.Addresses.OrderBy(a => a.Line))
                .Include(x => x.Contacts.OrderBy(c => c.Line));
        }

        return await query.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.CustCode == code,
            cancellationToken);
    }

    public Task<SaCust?> GetTrackedAsync(
        AppDbContext db,
        string companyCode,
        string custCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (custCode ?? string.Empty).Trim();
        return db.SaCusts.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.CustCode == code,
            cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        string companyCode,
        string custCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (custCode ?? string.Empty).Trim();
        return await db.SaCusts
            .AsNoTracking()
            .AnyAsync(x => x.CompanyCode == company && x.CustCode == code, cancellationToken);
    }

    public async Task<(IReadOnlyList<SaCust> Rows, int TotalCount)> SearchPagedAsync(
        string companyCode,
        SaCustSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = BuildFilterQuery(db, company, args);
        var total = await query.CountAsync(cancellationToken);
        var rows = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (rows, total);
    }

    public async Task<IReadOnlyList<SaCust>> ListExportAsync(
        string companyCode,
        SaCustSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await ApplySort(BuildFilterQuery(db, company, args), args.SortField, args.SortDescending)
            .Take(MaxExportRows)
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>> CountReferencesBulkAsync(
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        // No confirmed transactional references to SaCust in this codebase yet.
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>>(
            new Dictionary<string, IReadOnlyList<StockMasterReferenceCount>>(StringComparer.OrdinalIgnoreCase));
    }

    private static IQueryable<SaCust> BuildFilterQuery(AppDbContext db, string company, SaCustSearchArgs args)
    {
        var query = db.SaCusts.AsNoTracking().Where(x => x.CompanyCode == company);

        if (args.IsActive is not null)
        {
            query = query.Where(x => x.IsActive == args.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(args.CustType))
        {
            var type = args.CustType.Trim();
            query = query.Where(x => x.CustType == type);
        }

        if (!string.IsNullOrWhiteSpace(args.CustGroupCode))
        {
            var group = args.CustGroupCode.Trim();
            query = query.Where(x => x.CustGroupCode == group);
        }

        if (!string.IsNullOrWhiteSpace(args.SalesmanCode))
        {
            var salesman = args.SalesmanCode.Trim();
            query = query.Where(x => x.SalesmanCode == salesman);
        }

        if (!string.IsNullOrWhiteSpace(args.AreaCode))
        {
            var area = args.AreaCode.Trim();
            query = query.Where(x => x.AreaCode == area);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                EF.Functions.Like(x.CustCode, $"%{term}%")
                || (x.CustName != null && EF.Functions.Like(x.CustName, $"%{term}%"))
                || (x.CustShortName != null && EF.Functions.Like(x.CustShortName, $"%{term}%"))
                || (x.Tel != null && EF.Functions.Like(x.Tel, $"%{term}%"))
                || (x.City != null && EF.Functions.Like(x.City, $"%{term}%")));
        }

        return query;
    }

    private static IQueryable<SaCust> ApplySort(IQueryable<SaCust> query, string? sortField, bool desc)
    {
        var field = string.IsNullOrWhiteSpace(sortField) || !AllowedSortFields.Contains(sortField)
            ? nameof(SaCust.CustCode)
            : sortField;

        return field switch
        {
            nameof(SaCust.CustName) => desc ? query.OrderByDescending(x => x.CustName) : query.OrderBy(x => x.CustName),
            nameof(SaCust.CustType) => desc ? query.OrderByDescending(x => x.CustType) : query.OrderBy(x => x.CustType),
            nameof(SaCust.CustGroupCode) => desc ? query.OrderByDescending(x => x.CustGroupCode) : query.OrderBy(x => x.CustGroupCode),
            nameof(SaCust.SalesmanCode) => desc ? query.OrderByDescending(x => x.SalesmanCode) : query.OrderBy(x => x.SalesmanCode),
            nameof(SaCust.City) => desc ? query.OrderByDescending(x => x.City) : query.OrderBy(x => x.City),
            nameof(SaCust.Tel) => desc ? query.OrderByDescending(x => x.Tel) : query.OrderBy(x => x.Tel),
            nameof(SaCust.IsActive) => desc ? query.OrderByDescending(x => x.IsActive) : query.OrderBy(x => x.IsActive),
            _ => desc ? query.OrderByDescending(x => x.CustCode) : query.OrderBy(x => x.CustCode)
        };
    }
}
