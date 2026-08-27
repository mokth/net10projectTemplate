using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Inventory;

public sealed class IvReferenceCount
{
    public string ReferenceType { get; init; } = string.Empty;
    public int Count { get; init; }
}

public readonly record struct IvLocationRefKey(string WarehouseCode, string LocCode);

public interface IIvStockCommonRepository
{
    Task<IReadOnlyList<IvWarehouse>> ListActiveWarehousesAsync(
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvLocation>> ListActiveLocationsAsync(
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvClass>> ListActiveClassesAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvSubClass>> ListActiveSubClassesAsync(
        string companyCode,
        string iClassCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MsUom>> ListActiveUomsAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvStatus>> ListActiveStatusesAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvType>> ListActiveTypesAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<IvOnHandBalanceRow> Rows, int TotalCount)> SearchOnHandPagedAsync(
        string companyCode,
        string branchCode,
        string? iCode,
        string? searchText,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<IvOnHandBalanceRow?> GetOnHandByIdAsync(
        string companyCode,
        string branchCode,
        int balLocId,
        CancellationToken cancellationToken = default);

    Task<IvWarehouse?> GetActiveWarehouseAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<bool> HasActiveLocationsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<IvLocation?> GetActiveLocationAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string locCode,
        CancellationToken cancellationToken = default);

    Task<IvClass?> GetActiveClassAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        CancellationToken cancellationToken = default);

    Task<IvSubClass?> GetActiveSubClassAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        string iSubClassCode,
        CancellationToken cancellationToken = default);

    Task<MsUom?> GetActiveUomAsync(
        AppDbContext db,
        string companyCode,
        string uomCode,
        CancellationToken cancellationToken = default);

    Task<IvStatus?> GetActiveStatusAsync(
        AppDbContext db,
        string companyCode,
        string iStatus,
        CancellationToken cancellationToken = default);

    Task<IvType?> GetActiveTypeAsync(
        AppDbContext db,
        string companyCode,
        string typeCode,
        CancellationToken cancellationToken = default);

    // --- Master CRUD lists (include inactive) ---
    Task<IReadOnlyList<IvWarehouse>> ListWarehousesAsync(
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvLocation>> ListLocationsAsync(
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvStatus>> ListStatusesAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MsUom>> ListUomsAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvType>> ListTypesAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvClass>> ListClassesAsync(
        string companyCode,
        CancellationToken cancellationToken = default);

    // --- Tracked gets ---
    Task<IvWarehouse?> GetWarehouseTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<IvLocation?> GetLocationTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string locCode,
        CancellationToken cancellationToken = default);

    Task<IvStatus?> GetStatusTrackedAsync(
        AppDbContext db,
        string companyCode,
        string iStatus,
        CancellationToken cancellationToken = default);

    Task<MsUom?> GetUomTrackedAsync(
        AppDbContext db,
        string companyCode,
        string uomCode,
        CancellationToken cancellationToken = default);

    Task<IvType?> GetTypeTrackedAsync(
        AppDbContext db,
        string companyCode,
        string typeCode,
        CancellationToken cancellationToken = default);

    Task<IvClass?> GetClassTrackedAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        bool includeSubClasses,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvWarehouse>> GetWarehousesTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvLocation>> GetLocationsTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<IvLocationRefKey> keys,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvStatus>> GetStatusesTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MsUom>> GetUomsTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvType>> GetTypesTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvClass>> GetClassesTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        bool includeSubClasses,
        CancellationToken cancellationToken = default);

    // --- Batched delete probes ---
    Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountWarehouseReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<string> warehouseCodes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountLocationReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<IvLocationRefKey> keys,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountStatusReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> statuses,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountUomReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> uomCodes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountTypeReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> typeCodes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountClassReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> classCodes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, int>> CountItemSubclassUsageBulkAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        IReadOnlyList<string> subClassCodes,
        CancellationToken cancellationToken = default);
}

public sealed class IvStockCommonRepository : IIvStockCommonRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public IvStockCommonRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<IvWarehouse>> ListActiveWarehousesAsync(
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        return await db.IvWarehouses
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsActive)
            .OrderBy(x => x.WarehouseCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvLocation>> ListActiveLocationsAsync(
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var wh = (warehouseCode ?? string.Empty).Trim();
        return await db.IvLocations
            .AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                x.BranchCode == branch &&
                x.WarehouseCode == wh &&
                x.IsActive)
            .OrderBy(x => x.LocCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvClass>> ListActiveClassesAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.IvClasses
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.IClassCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvSubClass>> ListActiveSubClassesAsync(
        string companyCode,
        string iClassCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var classCode = (iClassCode ?? string.Empty).Trim();
        return await db.IvSubClasses
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IClassCode == classCode && x.IsActive)
            .OrderBy(x => x.ISubClassCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MsUom>> ListActiveUomsAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.MsUoms
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.UomCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvStatus>> ListActiveStatusesAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.IvStatuses
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.IStatus)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvType>> ListActiveTypesAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.IvTypes
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .OrderBy(x => x.TypeCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<IvOnHandBalanceRow> Rows, int TotalCount)> SearchOnHandPagedAsync(
        string companyCode,
        string branchCode,
        string? iCode,
        string? searchText,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);

        var query =
            from bal in db.IvBalLocs.AsNoTracking()
            join sm in db.IvStockMasters.AsNoTracking()
                on new { bal.CompanyCode, bal.ICode } equals new { sm.CompanyCode, sm.ICode }
            join lot in db.IvLots.AsNoTracking()
                on bal.LotId equals lot.Id into lots
            from lot in lots.DefaultIfEmpty()
            where bal.CompanyCode == company
                  && bal.BranchCode == branch
                  && bal.StdQty > 0m
                  && sm.IsActive
                  && sm.StockControl
            select new { bal, sm, lot };

        if (!string.IsNullOrWhiteSpace(iCode))
        {
            var code = iCode.Trim();
            query = query.Where(x => x.bal.ICode == code);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var term = searchText.Trim();
            query = query.Where(x =>
                x.bal.ICode.Contains(term)
                || (x.sm.IDesc != null && x.sm.IDesc.Contains(term))
                || x.bal.WhCode.Contains(term)
                || x.bal.LocCode.Contains(term)
                || x.bal.LotNo.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(x => x.bal.ICode)
            .ThenBy(x => x.bal.WhCode)
            .ThenBy(x => x.bal.LocCode)
            .ThenBy(x => x.bal.LotNo)
            .ThenBy(x => x.bal.Id)
            .Skip(skip)
            .Take(take)
            .Select(x => new IvOnHandBalanceRow
            {
                Id = x.bal.Id,
                ICode = x.bal.ICode,
                IDesc = x.sm.IDesc,
                WhCode = x.bal.WhCode,
                LocCode = x.bal.LocCode,
                LotNo = x.bal.LotNo,
                StdQty = x.bal.StdQty,
                StdUom = x.bal.StdUom ?? x.sm.StdUom,
                IStatus = x.bal.IStatus,
                ExpiryDate = x.lot != null ? x.lot.ExpiryDate : null,
                IClassCode = x.sm.IClassCode,
                LotControl = x.sm.LotControl,
                PurchasePrice = x.sm.PurchasePrice,
                LotId = x.bal.LotId
            })
            .ToListAsync(cancellationToken);

        return (rows, total);
    }

    public async Task<IvOnHandBalanceRow?> GetOnHandByIdAsync(
        string companyCode,
        string branchCode,
        int balLocId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        if (balLocId <= 0)
        {
            return null;
        }

        return await (
            from bal in db.IvBalLocs.AsNoTracking()
            join sm in db.IvStockMasters.AsNoTracking()
                on new { bal.CompanyCode, bal.ICode } equals new { sm.CompanyCode, sm.ICode }
            join lot in db.IvLots.AsNoTracking()
                on bal.LotId equals lot.Id into lots
            from lot in lots.DefaultIfEmpty()
            where bal.Id == balLocId
                  && bal.CompanyCode == company
                  && bal.BranchCode == branch
            select new IvOnHandBalanceRow
            {
                Id = bal.Id,
                ICode = bal.ICode,
                IDesc = sm.IDesc,
                WhCode = bal.WhCode,
                LocCode = bal.LocCode,
                LotNo = bal.LotNo,
                StdQty = bal.StdQty,
                StdUom = bal.StdUom ?? sm.StdUom,
                IStatus = bal.IStatus,
                ExpiryDate = lot != null ? lot.ExpiryDate : null,
                IClassCode = sm.IClassCode,
                LotControl = sm.LotControl,
                PurchasePrice = sm.PurchasePrice,
                LotId = bal.LotId
            }).FirstOrDefaultAsync(cancellationToken);
    }

    public Task<IvWarehouse?> GetActiveWarehouseAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var wh = (warehouseCode ?? string.Empty).Trim();
        return db.IvWarehouses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.CompanyCode == company &&
                    x.BranchCode == branch &&
                    x.WarehouseCode == wh &&
                    x.IsActive,
                cancellationToken);
    }

    public Task<bool> HasActiveLocationsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var wh = (warehouseCode ?? string.Empty).Trim();
        return db.IvLocations
            .AsNoTracking()
            .AnyAsync(
                x =>
                    x.CompanyCode == company &&
                    x.BranchCode == branch &&
                    x.WarehouseCode == wh &&
                    x.IsActive,
                cancellationToken);
    }

    public Task<IvLocation?> GetActiveLocationAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string locCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var wh = (warehouseCode ?? string.Empty).Trim();
        var loc = (locCode ?? string.Empty).Trim();
        return db.IvLocations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.CompanyCode == company &&
                    x.BranchCode == branch &&
                    x.WarehouseCode == wh &&
                    x.LocCode == loc &&
                    x.IsActive,
                cancellationToken);
    }

    public Task<IvClass?> GetActiveClassAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (iClassCode ?? string.Empty).Trim();
        return db.IvClasses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.IClassCode == code && x.IsActive,
                cancellationToken);
    }

    public Task<IvSubClass?> GetActiveSubClassAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        string iSubClassCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var classCode = (iClassCode ?? string.Empty).Trim();
        var sub = (iSubClassCode ?? string.Empty).Trim();
        return db.IvSubClasses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.CompanyCode == company &&
                    x.IClassCode == classCode &&
                    x.ISubClassCode == sub &&
                    x.IsActive,
                cancellationToken);
    }

    public Task<MsUom?> GetActiveUomAsync(
        AppDbContext db,
        string companyCode,
        string uomCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (uomCode ?? string.Empty).Trim();
        return db.MsUoms
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.UomCode == code && x.IsActive,
                cancellationToken);
    }

    public Task<IvStatus?> GetActiveStatusAsync(
        AppDbContext db,
        string companyCode,
        string iStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var status = (iStatus ?? string.Empty).Trim();
        return db.IvStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.IStatus == status && x.IsActive,
                cancellationToken);
    }

    public Task<IvType?> GetActiveTypeAsync(
        AppDbContext db,
        string companyCode,
        string typeCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (typeCode ?? string.Empty).Trim();
        return db.IvTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.TypeCode == code && x.IsActive,
                cancellationToken);
    }

    public async Task<IReadOnlyList<IvWarehouse>> ListWarehousesAsync(
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        return await db.IvWarehouses
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .OrderBy(x => x.WarehouseCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvLocation>> ListLocationsAsync(
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var wh = (warehouseCode ?? string.Empty).Trim();
        return await db.IvLocations
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.WarehouseCode == wh)
            .OrderBy(x => x.LocCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvStatus>> ListStatusesAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.IvStatuses
            .AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.IStatus)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MsUom>> ListUomsAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.MsUoms
            .AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.UomCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvType>> ListTypesAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.IvTypes
            .AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.TypeCode)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvClass>> ListClassesAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = (companyCode ?? string.Empty).Trim();
        return await db.IvClasses
            .AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.IClassCode)
            .ToListAsync(cancellationToken);
    }

    public Task<IvWarehouse?> GetWarehouseTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var wh = (warehouseCode ?? string.Empty).Trim();
        return db.IvWarehouses.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.BranchCode == branch && x.WarehouseCode == wh,
            cancellationToken);
    }

    public Task<IvLocation?> GetLocationTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string locCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var wh = (warehouseCode ?? string.Empty).Trim();
        var loc = (locCode ?? string.Empty).Trim();
        return db.IvLocations.FirstOrDefaultAsync(
            x =>
                x.CompanyCode == company &&
                x.BranchCode == branch &&
                x.WarehouseCode == wh &&
                x.LocCode == loc,
            cancellationToken);
    }

    public Task<IvStatus?> GetStatusTrackedAsync(
        AppDbContext db,
        string companyCode,
        string iStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var status = (iStatus ?? string.Empty).Trim();
        return db.IvStatuses.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.IStatus == status,
            cancellationToken);
    }

    public Task<MsUom?> GetUomTrackedAsync(
        AppDbContext db,
        string companyCode,
        string uomCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (uomCode ?? string.Empty).Trim();
        return db.MsUoms.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.UomCode == code,
            cancellationToken);
    }

    public Task<IvType?> GetTypeTrackedAsync(
        AppDbContext db,
        string companyCode,
        string typeCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (typeCode ?? string.Empty).Trim();
        return db.IvTypes.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.TypeCode == code,
            cancellationToken);
    }

    public async Task<IvClass?> GetClassTrackedAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        bool includeSubClasses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (iClassCode ?? string.Empty).Trim();
        IQueryable<IvClass> query = db.IvClasses;
        if (includeSubClasses)
        {
            query = query.Include(x => x.SubClasses);
        }

        return await query.FirstOrDefaultAsync(
            x => x.CompanyCode == company && x.IClassCode == code,
            cancellationToken);
    }

    public async Task<IReadOnlyList<IvWarehouse>> GetWarehousesTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var set = NormalizeCodes(codes);
        if (set.Count == 0)
        {
            return [];
        }

        return await db.IvWarehouses
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && set.Contains(x.WarehouseCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvLocation>> GetLocationsTrackedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<IvLocationRefKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var normalized = keys
            .Select(k => new IvLocationRefKey(
                (k.WarehouseCode ?? string.Empty).Trim(),
                (k.LocCode ?? string.Empty).Trim()))
            .Where(k => k.WarehouseCode.Length > 0 && k.LocCode.Length > 0)
            .Distinct()
            .ToList();
        if (normalized.Count == 0)
        {
            return [];
        }

        var warehouses = normalized.Select(k => k.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var locs = normalized.Select(k => k.LocCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = await db.IvLocations
            .Where(x =>
                x.CompanyCode == company &&
                x.BranchCode == branch &&
                warehouses.Contains(x.WarehouseCode) &&
                locs.Contains(x.LocCode))
            .ToListAsync(cancellationToken);

        var wanted = new HashSet<string>(
            normalized.Select(FormatLocationKey),
            StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(x => wanted.Contains(FormatLocationKey(x.WarehouseCode, x.LocCode)))
            .ToList();
    }

    public async Task<IReadOnlyList<IvStatus>> GetStatusesTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var set = NormalizeCodes(codes);
        if (set.Count == 0)
        {
            return [];
        }

        return await db.IvStatuses
            .Where(x => x.CompanyCode == company && set.Contains(x.IStatus))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MsUom>> GetUomsTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var set = NormalizeCodes(codes);
        if (set.Count == 0)
        {
            return [];
        }

        return await db.MsUoms
            .Where(x => x.CompanyCode == company && set.Contains(x.UomCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvType>> GetTypesTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var set = NormalizeCodes(codes);
        if (set.Count == 0)
        {
            return [];
        }

        return await db.IvTypes
            .Where(x => x.CompanyCode == company && set.Contains(x.TypeCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IvClass>> GetClassesTrackedAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        bool includeSubClasses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var set = NormalizeCodes(codes);
        if (set.Count == 0)
        {
            return [];
        }

        IQueryable<IvClass> query = db.IvClasses;
        if (includeSubClasses)
        {
            query = query.Include(x => x.SubClasses);
        }

        return await query
            .Where(x => x.CompanyCode == company && set.Contains(x.IClassCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountWarehouseReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<string> warehouseCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var codes = NormalizeCodes(warehouseCodes);
        var map = InitRefMap(codes);
        if (codes.Count == 0)
        {
            return FreezeMap(map);
        }

        AddHits(
            map,
            "Location",
            await db.IvLocations.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && codes.Contains(x.WarehouseCode))
                .GroupBy(x => x.WarehouseCode)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken));

        AddHits(
            map,
            "Balance",
            await db.IvBalLocs.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch && codes.Contains(x.WhCode))
                .GroupBy(x => x.WhCode)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken));

        AddHits(
            map,
            "ItemDefault",
            await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.DefWarehouse != null && codes.Contains(x.DefWarehouse))
                .GroupBy(x => x.DefWarehouse!)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken));

        var batchWh = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                x.BranchCode == branch &&
                ((x.FrWarehouse != null && codes.Contains(x.FrWarehouse)) ||
                 (x.ToWarehouse != null && codes.Contains(x.ToWarehouse))))
            .Select(x => new { x.FrWarehouse, x.ToWarehouse })
            .ToListAsync(cancellationToken);
        AccumulateCodes(map, codes, batchWh.SelectMany(x => new[] { x.FrWarehouse, x.ToWarehouse }), "Batch");

        var historyWh = await db.IvTrxHistories.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                x.BranchCode == branch &&
                ((x.FrWarehouse != null && codes.Contains(x.FrWarehouse)) ||
                 (x.ToWarehouse != null && codes.Contains(x.ToWarehouse))))
            .Select(x => new { x.FrWarehouse, x.ToWarehouse })
            .ToListAsync(cancellationToken);
        AccumulateCodes(map, codes, historyWh.SelectMany(x => new[] { x.FrWarehouse, x.ToWarehouse }), "History");

        return FreezeMap(map);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountLocationReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<IvLocationRefKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var normalized = keys
            .Select(k => new IvLocationRefKey(
                (k.WarehouseCode ?? string.Empty).Trim(),
                (k.LocCode ?? string.Empty).Trim()))
            .Where(k => k.WarehouseCode.Length > 0 && k.LocCode.Length > 0)
            .Distinct()
            .ToList();
        var keySet = new HashSet<string>(normalized.Select(FormatLocationKey), StringComparer.OrdinalIgnoreCase);
        var map = InitRefMap(keySet);
        if (normalized.Count == 0)
        {
            return FreezeMap(map);
        }

        var warehouses = normalized.Select(k => k.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var locs = normalized.Select(k => k.LocCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var balRows = await db.IvBalLocs.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                x.BranchCode == branch &&
                warehouses.Contains(x.WhCode) &&
                locs.Contains(x.LocCode))
            .GroupBy(x => new { x.WhCode, x.LocCode })
            .Select(g => new { g.Key.WhCode, g.Key.LocCode, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in balRows)
        {
            var key = FormatLocationKey(row.WhCode, row.LocCode);
            if (keySet.Contains(key))
            {
                map[key].Add(new IvReferenceCount { ReferenceType = "Balance", Count = row.Count });
            }
        }

        var itemRows = await db.IvStockMasters.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                x.DefWarehouse != null &&
                x.DefLocation != null &&
                warehouses.Contains(x.DefWarehouse) &&
                locs.Contains(x.DefLocation))
            .GroupBy(x => new { Wh = x.DefWarehouse!, Loc = x.DefLocation! })
            .Select(g => new { g.Key.Wh, g.Key.Loc, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var row in itemRows)
        {
            var key = FormatLocationKey(row.Wh, row.Loc);
            if (keySet.Contains(key))
            {
                map[key].Add(new IvReferenceCount { ReferenceType = "ItemDefault", Count = row.Count });
            }
        }

        var batchPairs = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new
            {
                FrWh = x.FrWarehouse,
                FrLoc = x.FrLocation,
                ToWh = x.ToWarehouse,
                ToLoc = x.ToLocation
            })
            .ToListAsync(cancellationToken);
        AccumulateLocationPairs(map, keySet, batchPairs.SelectMany(x => new[]
        {
            (x.FrWh, x.FrLoc),
            (x.ToWh, x.ToLoc)
        }), "Batch");

        var historyPairs = await db.IvTrxHistories.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new
            {
                FrWh = x.FrWarehouse,
                FrLoc = x.FrLocation,
                ToWh = x.ToWarehouse,
                ToLoc = x.ToLocation
            })
            .ToListAsync(cancellationToken);
        AccumulateLocationPairs(map, keySet, historyPairs.SelectMany(x => new[]
        {
            (x.FrWh, x.FrLoc),
            (x.ToWh, x.ToLoc)
        }), "History");

        return FreezeMap(map);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountStatusReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> statuses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var codes = NormalizeCodes(statuses);
        var map = InitRefMap(codes);
        if (codes.Count == 0)
        {
            return FreezeMap(map);
        }

        await AddGroupedAsync(
            map,
            "Balance",
            db.IvBalLocs.AsNoTracking()
                .Where(x => x.CompanyCode == company && codes.Contains(x.IStatus))
                .GroupBy(x => x.IStatus)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        await AddGroupedAsync(
            map,
            "Batch",
            db.IvTrxBatchDetails.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.IStatus != null && codes.Contains(x.IStatus))
                .GroupBy(x => x.IStatus!)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        await AddGroupedAsync(
            map,
            "History",
            db.IvTrxHistories.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.IStatus != null && codes.Contains(x.IStatus))
                .GroupBy(x => x.IStatus!)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        return FreezeMap(map);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountUomReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> uomCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var codes = NormalizeCodes(uomCodes);
        var map = InitRefMap(codes);
        if (codes.Count == 0)
        {
            return FreezeMap(map);
        }

        var itemHits = await db.IvStockMasters.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                ((x.StdUom != null && codes.Contains(x.StdUom)) ||
                 (x.PurUom != null && codes.Contains(x.PurUom)) ||
                 (x.SellingUom != null && codes.Contains(x.SellingUom))))
            .Select(x => new { x.StdUom, x.PurUom, x.SellingUom })
            .ToListAsync(cancellationToken);
        AccumulateCodes(map, codes, itemHits.SelectMany(x => new[] { x.StdUom, x.PurUom, x.SellingUom }), "Item");

        await AddGroupedAsync(
            map,
            "Balance",
            db.IvBalLocs.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.StdUom != null && codes.Contains(x.StdUom))
                .GroupBy(x => x.StdUom!)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        var batchHits = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                ((x.FrStdUom != null && codes.Contains(x.FrStdUom)) ||
                 (x.ToStdUom != null && codes.Contains(x.ToStdUom)) ||
                 (x.FrPurUom != null && codes.Contains(x.FrPurUom)) ||
                 (x.ToPurUom != null && codes.Contains(x.ToPurUom))))
            .Select(x => new { x.FrStdUom, x.ToStdUom, x.FrPurUom, x.ToPurUom })
            .ToListAsync(cancellationToken);
        AccumulateCodes(map, codes, batchHits.SelectMany(x => new[] { x.FrStdUom, x.ToStdUom, x.FrPurUom, x.ToPurUom }), "Batch");

        var historyHits = await db.IvTrxHistories.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                ((x.FrStdUom != null && codes.Contains(x.FrStdUom)) ||
                 (x.ToStdUom != null && codes.Contains(x.ToStdUom)) ||
                 (x.FrPurUom != null && codes.Contains(x.FrPurUom)) ||
                 (x.ToPurUom != null && codes.Contains(x.ToPurUom))))
            .Select(x => new { x.FrStdUom, x.ToStdUom, x.FrPurUom, x.ToPurUom })
            .ToListAsync(cancellationToken);
        AccumulateCodes(map, codes, historyHits.SelectMany(x => new[] { x.FrStdUom, x.ToStdUom, x.FrPurUom, x.ToPurUom }), "History");

        return FreezeMap(map);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountTypeReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> typeCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var codes = NormalizeCodes(typeCodes);
        var map = InitRefMap(codes);
        if (codes.Count == 0)
        {
            return FreezeMap(map);
        }

        await AddGroupedAsync(
            map,
            "Item",
            db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.IType != null && codes.Contains(x.IType))
                .GroupBy(x => x.IType!)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        return FreezeMap(map);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountClassReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> classCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var codes = NormalizeCodes(classCodes);
        var map = InitRefMap(codes);
        if (codes.Count == 0)
        {
            return FreezeMap(map);
        }

        await AddGroupedAsync(
            map,
            "SubClass",
            db.IvSubClasses.AsNoTracking()
                .Where(x => x.CompanyCode == company && codes.Contains(x.IClassCode))
                .GroupBy(x => x.IClassCode)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        await AddGroupedAsync(
            map,
            "Item",
            db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.IClassCode != null && codes.Contains(x.IClassCode))
                .GroupBy(x => x.IClassCode!)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        await AddGroupedAsync(
            map,
            "Batch",
            db.IvTrxBatchDetails.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.IClassCode != null && codes.Contains(x.IClassCode))
                .GroupBy(x => x.IClassCode!)
                .Select(g => new CodeCount { Code = g.Key, Count = g.Count() }),
            cancellationToken);

        return FreezeMap(map);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountItemSubclassUsageBulkAsync(
        AppDbContext db,
        string companyCode,
        string iClassCode,
        IReadOnlyList<string> subClassCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var classCode = (iClassCode ?? string.Empty).Trim();
        var codes = NormalizeCodes(subClassCodes);
        if (codes.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await db.IvStockMasters.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company &&
                x.IClassCode == classCode &&
                x.ISubClassCode != null &&
                codes.Contains(x.ISubClassCode))
            .GroupBy(x => x.ISubClassCode!)
            .Select(g => new CodeCount { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.Code, x => x.Count, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> NormalizeCodes(IReadOnlyList<string> codes) =>
        codes
            .Select(c => (c ?? string.Empty).Trim())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, List<IvReferenceCount>> InitRefMap(IEnumerable<string> codes) =>
        codes.ToDictionary(c => c, _ => new List<IvReferenceCount>(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>> FreezeMap(
        Dictionary<string, List<IvReferenceCount>> map) =>
        map.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<IvReferenceCount>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

    private static async Task AddGroupedAsync(
        Dictionary<string, List<IvReferenceCount>> map,
        string referenceType,
        IQueryable<CodeCount> query,
        CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken);
        AddHits(map, referenceType, rows);
    }

    private static void AddHits(
        Dictionary<string, List<IvReferenceCount>> map,
        string referenceType,
        IReadOnlyList<CodeCount> rows)
    {
        foreach (var row in rows)
        {
            if (row.Count <= 0 || !map.TryGetValue(row.Code, out var list))
            {
                continue;
            }

            list.Add(new IvReferenceCount { ReferenceType = referenceType, Count = row.Count });
        }
    }

    private static string FormatLocationKey(IvLocationRefKey key) =>
        FormatLocationKey(key.WarehouseCode, key.LocCode);

    internal static string FormatLocationKey(string warehouseCode, string locCode) =>
        $"{warehouseCode.Trim()}|{locCode.Trim()}";

    private static void AccumulateLocationPairs(
        Dictionary<string, List<IvReferenceCount>> map,
        HashSet<string> keySet,
        IEnumerable<(string? Wh, string? Loc)> pairs,
        string referenceType)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (wh, loc) in pairs)
        {
            if (string.IsNullOrWhiteSpace(wh) || string.IsNullOrWhiteSpace(loc))
            {
                continue;
            }

            var key = FormatLocationKey(wh, loc);
            if (!keySet.Contains(key))
            {
                continue;
            }

            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var (key, count) in counts)
        {
            map[key].Add(new IvReferenceCount { ReferenceType = referenceType, Count = count });
        }
    }

    private static void AccumulateCodes(
        Dictionary<string, List<IvReferenceCount>> map,
        HashSet<string> codes,
        IEnumerable<string?> values,
        string referenceType)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !codes.Contains(value))
            {
                continue;
            }

            var key = value.Trim();
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var (key, count) in counts)
        {
            map[key].Add(new IvReferenceCount { ReferenceType = referenceType, Count = count });
        }
    }

    private sealed class CodeCount
    {
        public string Code { get; init; } = string.Empty;
        public int Count { get; init; }
    }
}
