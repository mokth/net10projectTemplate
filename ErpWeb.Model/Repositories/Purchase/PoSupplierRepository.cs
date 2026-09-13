using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Purchase;

public interface IPoSupplierRepository
{
    Task<PoSupplier?> GetByCodeAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string suppCode,
        bool includeChildren,
        CancellationToken cancellationToken = default);

    Task<PoSupplier?> GetTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string suppCode,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string companyCode,
        string branchCode,
        string suppCode,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<PoSupplier> Rows, int TotalCount)> SearchPagedAsync(
        string companyCode,
        string branchCode,
        PoSupplierSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoSupplier>> ListExportAsync(
        string companyCode,
        string branchCode,
        PoSupplierSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>> CountReferencesBulkAsync(
        string companyCode,
        string branchCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);
}

public sealed class PoSupplierRepository : IPoSupplierRepository
{
    public const int MaxExportRows = 50_000;
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PoSupplier.SuppCode),
        nameof(PoSupplier.SuppName),
        nameof(PoSupplier.SuppType),
        nameof(PoSupplier.CategoryCode),
        nameof(PoSupplier.AreaCode),
        nameof(PoSupplier.City),
        nameof(PoSupplier.Tel),
        nameof(PoSupplier.IsActive)
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PoSupplierRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<PoSupplier?> GetByCodeAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string suppCode,
        bool includeChildren,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var code = (suppCode ?? string.Empty).Trim();

        IQueryable<PoSupplier> query = db.PoSuppliers.AsNoTracking();
        if (includeChildren)
        {
            query = query.Include(x => x.Addresses.OrderBy(a => a.Line));
        }

        return await query.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.BranchCode == branch && x.SuppCode == code,
            cancellationToken);
    }

    public Task<PoSupplier?> GetTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string suppCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var code = (suppCode ?? string.Empty).Trim();
        return db.PoSuppliers.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.BranchCode == branch && x.SuppCode == code,
            cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        string companyCode,
        string branchCode,
        string suppCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var code = (suppCode ?? string.Empty).Trim();
        return await db.PoSuppliers
            .AsNoTracking()
            .AnyAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.SuppCode == code,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<PoSupplier> Rows, int TotalCount)> SearchPagedAsync(
        string companyCode,
        string branchCode,
        PoSupplierSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = BuildFilterQuery(db, company, branch, args);
        var total = await query.CountAsync(cancellationToken);
        var rows = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (rows, total);
    }

    public async Task<IReadOnlyList<PoSupplier>> ListExportAsync(
        string companyCode,
        string branchCode,
        PoSupplierSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        return await ApplySort(BuildFilterQuery(db, company, branch, args), args.SortField, args.SortDescending)
            .Take(MaxExportRows)
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>> CountReferencesBulkAsync(
        string companyCode,
        string branchCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        // Reference checking is not active in the current phase.
        // Future transactional reference fields (do not query yet):
        //   POOrder.VendCode, PoInvoice.VendorCode, PoPrDetail.VendorCd
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>>(
            new Dictionary<string, IReadOnlyList<StockMasterReferenceCount>>(StringComparer.OrdinalIgnoreCase));
    }

    private static IQueryable<PoSupplier> BuildFilterQuery(
        AppDbContext db,
        string company,
        string branch,
        PoSupplierSearchArgs args)
    {
        var query = db.PoSuppliers.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        if (args.IsActive is not null)
        {
            query = query.Where(x => x.IsActive == args.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(args.SuppType))
        {
            var type = args.SuppType.Trim();
            query = query.Where(x => x.SuppType == type);
        }

        if (!string.IsNullOrWhiteSpace(args.CategoryCode))
        {
            var category = args.CategoryCode.Trim();
            query = query.Where(x => x.CategoryCode == category);
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
                EF.Functions.Like(x.SuppCode, $"%{term}%")
                || (x.SuppName != null && EF.Functions.Like(x.SuppName, $"%{term}%"))
                || (x.SuppShortName != null && EF.Functions.Like(x.SuppShortName, $"%{term}%"))
                || (x.City != null && EF.Functions.Like(x.City, $"%{term}%"))
                || (x.Tel != null && EF.Functions.Like(x.Tel, $"%{term}%"))
                || (x.Email != null && EF.Functions.Like(x.Email, $"%{term}%")));
        }

        return query;
    }

    private static IQueryable<PoSupplier> ApplySort(IQueryable<PoSupplier> query, string? sortField, bool desc)
    {
        var field = string.IsNullOrWhiteSpace(sortField) || !AllowedSortFields.Contains(sortField)
            ? nameof(PoSupplier.SuppCode)
            : sortField;

        return field switch
        {
            nameof(PoSupplier.SuppName) => desc ? query.OrderByDescending(x => x.SuppName) : query.OrderBy(x => x.SuppName),
            nameof(PoSupplier.SuppType) => desc ? query.OrderByDescending(x => x.SuppType) : query.OrderBy(x => x.SuppType),
            nameof(PoSupplier.CategoryCode) => desc ? query.OrderByDescending(x => x.CategoryCode) : query.OrderBy(x => x.CategoryCode),
            nameof(PoSupplier.AreaCode) => desc ? query.OrderByDescending(x => x.AreaCode) : query.OrderBy(x => x.AreaCode),
            nameof(PoSupplier.City) => desc ? query.OrderByDescending(x => x.City) : query.OrderBy(x => x.City),
            nameof(PoSupplier.Tel) => desc ? query.OrderByDescending(x => x.Tel) : query.OrderBy(x => x.Tel),
            nameof(PoSupplier.IsActive) => desc ? query.OrderByDescending(x => x.IsActive) : query.OrderBy(x => x.IsActive),
            _ => desc ? query.OrderByDescending(x => x.SuppCode) : query.OrderBy(x => x.SuppCode)
        };
    }
}
