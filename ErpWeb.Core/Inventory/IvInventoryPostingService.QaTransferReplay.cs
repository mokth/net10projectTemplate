using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record QaTransferPile(int Id, decimal CurrentQuantity,
        byte[] RowVersion, decimal FinalQuantity);

    private async Task<string?> AssertQaTransferRemovalAsync(
        AppDbContext db, string companyCode, string branchCode, int batchNo,
        StockReplayPhysicalRemovalPlan source, StockReplayPhysicalRemovalPlan destination,
        CancellationToken cancellationToken)
    {
        QaTransferPile[] piles =
        [
            new(source.BalLocId, source.CurrentQuantity, source.BalanceRowVersion, source.Projection.FinalQuantity),
            new(destination.BalLocId, destination.CurrentQuantity, destination.BalanceRowVersion, destination.Projection.FinalQuantity)
        ];
        var error = await LockQaTransferPilesAsync(db, companyCode, branchCode, piles, cancellationToken);
        if (error is not null) return error;
        error = await _qaPhysical.RestoreStockOutAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (error is not null) return error;
        foreach (var pile in piles)
        {
            error = await _qaPhysical.AssertRemainingCoversHoldsAsync(db, companyCode, branchCode,
                pile.Id, pile.FinalQuantity, cancellationToken);
            if (error is not null) return error;
        }
        return null;
    }

    private Task<string?> LockQaTransferPilesAsync(
        AppDbContext db, string companyCode, string branchCode,
        StockReplayPhysicalPlan source, StockReplayPhysicalPlan destination,
        CancellationToken cancellationToken) =>
        LockQaTransferPilesAsync(db, companyCode, branchCode,
        [
            new(source.BalLocId, source.CurrentQuantity, source.BalanceRowVersion, source.Projection.FinalQuantity),
            new(destination.BalLocId, destination.CurrentQuantity, destination.BalanceRowVersion, destination.Projection.FinalQuantity)
        ], cancellationToken);

    private async Task<string?> LockQaTransferPilesAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyList<QaTransferPile> plans, CancellationToken cancellationToken)
    {
        const string stale = "The transfer stock balances changed before QA validation.";
        var ids = plans.Select(p => p.Id).ToArray();
        if (ids.Length != 2 || ids.Distinct().Count() != 2) return stale;
        var snapshots = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode && b.BranchCode == branchCode && ids.Contains(b.Id))
            .ToArrayAsync(cancellationToken);
        if (snapshots.Length != plans.Count) return stale;
        var ordered = snapshots.Select(p => new
            {
                Pile = p,
                Slice = IvStockSliceKey.Create(p.CompanyCode, p.BranchCode, p.ICode,
                    p.WhCode, p.LocCode, p.LotNo, p.IStatus)
            }).OrderBy(x => x.Slice).ToArray();
        if (ordered.Select(x => x.Slice).Distinct().Count() != plans.Count) return stale;
        foreach (var row in ordered)
        {
            var plan = plans.Single(p => p.Id == row.Pile.Id);
            var locked = await _posting.LockBalLocByIdForTenantAsync(
                db, plan.Id, companyCode, branchCode, cancellationToken);
            if (locked is null || IvStockSliceKey.Create(locked.CompanyCode, locked.BranchCode,
                    locked.ICode, locked.WhCode, locked.LocCode, locked.LotNo, locked.IStatus) != row.Slice
                || locked.StdQty != plan.CurrentQuantity)
                return stale;
            var current = await db.IvBalLocs.AsNoTracking()
                .Where(b => b.Id == plan.Id && b.CompanyCode == companyCode && b.BranchCode == branchCode)
                .Select(b => new { b.StdQty, b.RowVersion }).SingleOrDefaultAsync(cancellationToken);
            if (current is null || current.StdQty != plan.CurrentQuantity
                || !current.RowVersion.SequenceEqual(plan.RowVersion)) return stale;
            var tracked = db.ChangeTracker.Entries<IvBalLoc>()
                .SingleOrDefault(e => e.Entity.Id == plan.Id)?.Entity;
            if (tracked is not null && (tracked.StdQty != current.StdQty
                || !tracked.RowVersion.SequenceEqual(current.RowVersion))) return stale;
        }
        return null;
    }
}
