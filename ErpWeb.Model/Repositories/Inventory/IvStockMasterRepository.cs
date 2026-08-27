using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Inventory;

public interface IIvStockMasterRepository
{
    Task<IvStockMaster?> GetByCodeAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default);

    Task<IvStockMaster?> GetByCodeAsync(
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default);

    Task<IvStockMaster?> GetTrackedAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<IvStockMaster> Rows, int TotalCount)> SearchPagedAsync(
        string companyCode,
        StockMasterSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<int> CountExportAsync(
        string companyCode,
        StockMasterSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvStockMaster>> ListExportAsync(
        string companyCode,
        StockMasterSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>> CountReferencesBulkAsync(
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvStockMaster>> ListActiveForLookupAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvStockMaster>> SearchActiveAsync(
        string companyCode,
        string? iCode,
        string? iDesc,
        string? iType,
        string? iClassCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exact barcode match for the company. Returns 0, 1, or many rows; callers must reject many.
    /// </summary>
    Task<IReadOnlyList<IvStockMaster>> GetByBarcodeAsync(
        string companyCode,
        string barcode,
        CancellationToken cancellationToken = default);
}

public sealed class IvStockMasterRepository : IIvStockMasterRepository
{
    public const int MaxExportRows = 50_000;
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvStockMaster.ICode),
        nameof(IvStockMaster.IDesc),
        nameof(IvStockMaster.IType),
        nameof(IvStockMaster.IClassCode),
        nameof(IvStockMaster.ISubClassCode),
        nameof(IvStockMaster.Brand),
        nameof(IvStockMaster.StdUom),
        nameof(IvStockMaster.DefWarehouse),
        nameof(IvStockMaster.SellingPrice),
        nameof(IvStockMaster.PurchasePrice),
        nameof(IvStockMaster.IsActive)
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public IvStockMasterRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public Task<IvStockMaster?> GetByCodeAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (iCode ?? string.Empty).Trim();
        return db.IvStockMasters
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.ICode == code,
                cancellationToken);
    }

    public async Task<IvStockMaster?> GetByCodeAsync(
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await GetByCodeAsync(db, companyCode, iCode, cancellationToken);
    }

    public Task<IvStockMaster?> GetTrackedAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (iCode ?? string.Empty).Trim();
        return db.IvStockMasters
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.ICode == code,
                cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        string companyCode,
        string iCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (iCode ?? string.Empty).Trim();
        return await db.IvStockMasters
            .AsNoTracking()
            .AnyAsync(x => x.CompanyCode == company && x.ICode == code, cancellationToken);
    }

    public async Task<(IReadOnlyList<IvStockMaster> Rows, int TotalCount)> SearchPagedAsync(
        string companyCode,
        StockMasterSearchArgs args,
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

    public async Task<int> CountExportAsync(
        string companyCode,
        StockMasterSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await BuildFilterQuery(db, company, args).CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvStockMaster>> ListExportAsync(
        string companyCode,
        StockMasterSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var query = BuildFilterQuery(db, company, args);
        return await ApplySort(query, args.SortField, args.SortDescending)
            .Take(MaxExportRows)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StockMasterReferenceCount>>> CountReferencesBulkAsync(
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var company = (companyCode ?? string.Empty).Trim();
        var codeList = (codes ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var map = codeList.ToDictionary(
            c => c,
            _ => new List<StockMasterReferenceCount>(),
            StringComparer.OrdinalIgnoreCase);

        if (codeList.Count == 0)
        {
            return map.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<StockMasterReferenceCount>)kv.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var lotCounts = await db.IvLots.AsNoTracking()
            .Where(x => x.CompanyCode == company && codeList.Contains(x.ICode))
            .GroupBy(x => x.ICode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var balCounts = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && codeList.Contains(x.ICode))
            .GroupBy(x => x.ICode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var batchCounts = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode != null && codeList.Contains(x.ICode))
            .GroupBy(x => x.ICode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var histCounts = await db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == company && codeList.Contains(x.ICode))
            .GroupBy(x => x.ICode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        foreach (var row in lotCounts)
        {
            AddCount(map, row.Code, "IvLot", row.Count);
        }

        foreach (var row in balCounts)
        {
            AddCount(map, row.Code, "IvBalLoc", row.Count);
        }

        foreach (var row in batchCounts)
        {
            AddCount(map, row.Code, "IvTrxBatchDetail", row.Count);
        }

        foreach (var row in histCounts)
        {
            AddCount(map, row.Code, "IvTrxHistory", row.Count);
        }

        return map.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<StockMasterReferenceCount>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<IvStockMaster>> ListActiveForLookupAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.IvStockMasters
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.ICode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvStockMaster>> SearchActiveAsync(
        string companyCode,
        string? iCode,
        string? iDesc,
        string? iType,
        string? iClassCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var query = db.IvStockMasters
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive);

        if (!string.IsNullOrWhiteSpace(iCode))
        {
            var term = iCode.Trim();
            query = query.Where(x => x.ICode.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(iDesc))
        {
            var term = iDesc.Trim();
            query = query.Where(x => x.IDesc != null && x.IDesc.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(iType))
        {
            var term = iType.Trim();
            query = query.Where(x => x.IType != null && x.IType.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(iClassCode))
        {
            var term = iClassCode.Trim();
            query = query.Where(x => x.IClassCode != null && x.IClassCode.Contains(term));
        }

        return await query
            .OrderBy(x => x.ICode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvStockMaster>> GetByBarcodeAsync(
        string companyCode,
        string barcode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (barcode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return [];
        }

        return await db.IvStockMasters
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive && x.Barcode == code)
            .OrderBy(x => x.ICode)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<IvStockMaster> BuildFilterQuery(
        AppDbContext db,
        string company,
        StockMasterSearchArgs args)
    {
        // Company only — never BranchCode on item master.
        var query = db.IvStockMasters
            .AsNoTracking()
            .Where(x => x.CompanyCode == company);

        if (args.IsActive is not null)
        {
            var active = args.IsActive.Value;
            query = query.Where(x => x.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.ICode.Contains(term) ||
                (x.IDesc != null && x.IDesc.Contains(term)) ||
                (x.Barcode != null && x.Barcode.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(args.IClassCode))
        {
            var value = args.IClassCode.Trim();
            query = query.Where(x => x.IClassCode == value);
        }

        if (!string.IsNullOrWhiteSpace(args.ISubClassCode))
        {
            var value = args.ISubClassCode.Trim();
            query = query.Where(x => x.ISubClassCode == value);
        }

        if (!string.IsNullOrWhiteSpace(args.IType))
        {
            var value = args.IType.Trim();
            query = query.Where(x => x.IType == value);
        }

        if (!string.IsNullOrWhiteSpace(args.DefWarehouse))
        {
            var value = args.DefWarehouse.Trim();
            query = query.Where(x => x.DefWarehouse == value);
        }

        if (!string.IsNullOrWhiteSpace(args.Brand))
        {
            var value = args.Brand.Trim();
            query = query.Where(x => x.Brand == value);
        }

        return query;
    }

    private static IQueryable<IvStockMaster> ApplySort(
        IQueryable<IvStockMaster> query,
        string? sortField,
        bool sortDescending)
    {
        if (string.IsNullOrWhiteSpace(sortField) || !AllowedSortFields.Contains(sortField))
        {
            return query.OrderBy(x => x.ICode);
        }

        return sortField.Trim().ToLowerInvariant() switch
        {
            "icode" => sortDescending ? query.OrderByDescending(x => x.ICode) : query.OrderBy(x => x.ICode),
            "idesc" => sortDescending ? query.OrderByDescending(x => x.IDesc) : query.OrderBy(x => x.IDesc),
            "itype" => sortDescending ? query.OrderByDescending(x => x.IType) : query.OrderBy(x => x.IType),
            "iclasscode" => sortDescending
                ? query.OrderByDescending(x => x.IClassCode)
                : query.OrderBy(x => x.IClassCode),
            "isubclasscode" => sortDescending
                ? query.OrderByDescending(x => x.ISubClassCode)
                : query.OrderBy(x => x.ISubClassCode),
            "brand" => sortDescending ? query.OrderByDescending(x => x.Brand) : query.OrderBy(x => x.Brand),
            "stduom" => sortDescending ? query.OrderByDescending(x => x.StdUom) : query.OrderBy(x => x.StdUom),
            "defwarehouse" => sortDescending
                ? query.OrderByDescending(x => x.DefWarehouse)
                : query.OrderBy(x => x.DefWarehouse),
            "sellingprice" => sortDescending
                ? query.OrderByDescending(x => x.SellingPrice)
                : query.OrderBy(x => x.SellingPrice),
            "purchaseprice" => sortDescending
                ? query.OrderByDescending(x => x.PurchasePrice)
                : query.OrderBy(x => x.PurchasePrice),
            "isactive" => sortDescending
                ? query.OrderByDescending(x => x.IsActive)
                : query.OrderBy(x => x.IsActive),
            _ => query.OrderBy(x => x.ICode)
        };
    }

    private static void AddCount(
        Dictionary<string, List<StockMasterReferenceCount>> map,
        string code,
        string referenceType,
        int count)
    {
        if (count <= 0 || !map.TryGetValue(code, out var list))
        {
            return;
        }

        list.Add(new StockMasterReferenceCount(referenceType, count));
    }
}
