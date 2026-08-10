using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class StockBalanceRowDto
{
    public long WarehouseId { get; init; }
    public string WarehouseCode { get; init; } = "";
    public long LocationId { get; init; }
    public string LocationCode { get; init; } = "";
    public long ItemVariantId { get; init; }
    public string SKU { get; init; } = "";
    public decimal QtyOnHand { get; init; }
    public decimal? AverageCost { get; init; }
    public decimal? Value { get; init; }
}

public sealed class StockCardRowDto
{
    public DateTime TransactionDate { get; init; }
    public long LedgerSequence { get; init; }
    public DocumentType DocType { get; init; }
    public string DocNo { get; init; } = "";
    public decimal QtyInBase { get; init; }
    public decimal QtyOutBase { get; init; }
    public decimal? UnitCost { get; init; }
    public decimal? Amount { get; init; }
    public string? LotNo { get; init; }
    public string WarehouseCode { get; init; } = "";
    public string LocationCode { get; init; } = "";
}

public interface IStockInquiryService
{
    Task<IReadOnlyList<StockBalanceRowDto>> GetBalancesAsync(long? warehouseId = null, CancellationToken ct = default);
    Task<IReadOnlyList<StockCardRowDto>> GetStockCardAsync(long itemVariantId, long? warehouseId = null, CancellationToken ct = default);
}

public sealed class StockInquiryService : IStockInquiryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentUserService _currentUser;

    public StockInquiryService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        IAccessRightService accessRights,
        ICurrentUserService currentUser)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _accessRights = accessRights;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<StockBalanceRowDto>> GetBalancesAsync(
        long? warehouseId = null, CancellationToken ct = default)
    {
        await _companyContext.ResolveAsync(ct);
        if (!await _accessRights.CanAsync(MenuCodes.InvStockBalance, PermissionCodes.Access, ct))
            return [];

        var canViewCost = await CanViewCostAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;

        var rows = await (
            from b in db.StockBalances.AsNoTracking()
            join w in db.Warehouses.AsNoTracking() on b.WarehouseId equals w.Id
            join loc in db.WarehouseLocations.AsNoTracking() on b.LocationId equals loc.Id
            join v in db.ItemVariants.AsNoTracking() on b.ItemVariantId equals v.Id
            join c in db.ItemCosts.AsNoTracking()
                on new { b.CompanyId, b.BranchId, b.WarehouseId, b.ItemVariantId }
                equals new { c.CompanyId, c.BranchId, c.WarehouseId, c.ItemVariantId }
                into costs
            from c in costs.DefaultIfEmpty()
            where b.CompanyId == companyId && b.BranchId == branchId
                  && (warehouseId == null || b.WarehouseId == warehouseId)
            orderby w.WarehouseCode, loc.LocationCode, v.SKU
            select new StockBalanceRowDto
            {
                WarehouseId = b.WarehouseId,
                WarehouseCode = w.WarehouseCode,
                LocationId = b.LocationId,
                LocationCode = loc.LocationCode,
                ItemVariantId = b.ItemVariantId,
                SKU = v.SKU,
                QtyOnHand = b.QtyOnHand,
                AverageCost = canViewCost ? (c != null ? c.AverageCost : 0m) : null,
                Value = canViewCost ? Math.Round(b.QtyOnHand * (c != null ? c.AverageCost : 0m), 4) : null
            }).ToListAsync(ct);

        return rows;
    }

    public async Task<IReadOnlyList<StockCardRowDto>> GetStockCardAsync(
        long itemVariantId, long? warehouseId = null, CancellationToken ct = default)
    {
        await _companyContext.ResolveAsync(ct);
        if (!await _accessRights.CanAsync(MenuCodes.InvStockCard, PermissionCodes.Access, ct))
            return [];

        var canViewCost = await CanViewCostAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;

        var rows = await db.StockLedgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BranchId == branchId
                        && l.ItemVariantId == itemVariantId
                        && (warehouseId == null || l.WarehouseId == warehouseId))
            .OrderBy(l => l.TransactionDate)
            .ThenBy(l => l.LedgerSequence)
            .ThenBy(l => l.Id)
            .Select(l => new StockCardRowDto
            {
                TransactionDate = l.TransactionDate,
                LedgerSequence = l.LedgerSequence,
                DocType = l.DocType,
                DocNo = l.DocNo,
                QtyInBase = l.QtyInBase,
                QtyOutBase = l.QtyOutBase,
                UnitCost = canViewCost ? l.UnitCost : null,
                Amount = canViewCost ? l.Amount : null,
                LotNo = l.LotNo,
                WarehouseCode = l.WarehouseCode,
                LocationCode = l.LocationCode
            })
            .ToListAsync(ct);

        return rows;
    }

    private async Task<bool> CanViewCostAsync(CancellationToken ct)
    {
        if (string.Equals(_currentUser.UserLevel, "SYSTEM_ADMIN", StringComparison.OrdinalIgnoreCase))
            return true;
        return await _accessRights.CanAsync(MenuCodes.Inventory, PermissionCodes.ViewCost, ct);
    }
}
