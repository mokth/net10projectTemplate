using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class LotIntegrityIssue
{
    public long WarehouseId { get; init; }
    public long LocationId { get; init; }
    public long ItemVariantId { get; init; }
    public decimal StockBalanceQty { get; init; }
    public decimal LotBalanceSum { get; init; }
    public string Message { get; init; } = null!;
}

public interface ILotReconciliationService
{
    Task<IReadOnlyList<LotIntegrityIssue>> FindLotBalanceMismatchesAsync(
        long? warehouseId = null, CancellationToken ct = default);
}

public sealed class LotReconciliationService : ILotReconciliationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;

    public LotReconciliationService(IDbContextFactory<AppDbContext> dbFactory, ICompanyContext companyContext)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
    }

    public async Task<IReadOnlyList<LotIntegrityIssue>> FindLotBalanceMismatchesAsync(
        long? warehouseId = null, CancellationToken ct = default)
    {
        await _companyContext.ResolveAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;

        var batchVariantIds = await (
            from v in db.ItemVariants.AsNoTracking()
            join i in db.Items.AsNoTracking() on v.ItemId equals i.Id
            where v.CompanyId == companyId && i.IsBatchItem
            select v.Id).ToListAsync(ct);

        var balances = await db.StockBalances.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.BranchId == branchId
                        && batchVariantIds.Contains(b.ItemVariantId)
                        && (warehouseId == null || b.WarehouseId == warehouseId))
            .ToListAsync(ct);

        var issues = new List<LotIntegrityIssue>();
        foreach (var bal in balances)
        {
            var lotSum = await (
                from lb in db.LotBalances.AsNoTracking()
                join lot in db.Lots.AsNoTracking() on lb.LotId equals lot.Id
                where lb.CompanyId == companyId && lb.BranchId == branchId
                      && lb.WarehouseId == bal.WarehouseId
                      && lb.LocationId == bal.LocationId
                      && lot.ItemVariantId == bal.ItemVariantId
                select (decimal?)lb.QtyOnHand).SumAsync(ct) ?? 0m;

            if (lotSum != bal.QtyOnHand)
            {
                issues.Add(new LotIntegrityIssue
                {
                    WarehouseId = bal.WarehouseId,
                    LocationId = bal.LocationId,
                    ItemVariantId = bal.ItemVariantId,
                    StockBalanceQty = bal.QtyOnHand,
                    LotBalanceSum = lotSum,
                    Message = $"LotBalance sum {lotSum} != StockBalance {bal.QtyOnHand}"
                });
            }
        }

        return issues;
    }
}
