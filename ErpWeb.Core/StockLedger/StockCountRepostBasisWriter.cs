using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Stages first-post bases and verifies immutable bases on repost.</summary>
internal static class StockCountRepostBasisWriter
{
    public static async Task StageOrVerifyAsync(AppDbContext db,
        StockCountSheetWritePermit permit,
        IReadOnlyCollection<StockCountCostBasis> proposed,
        CancellationToken cancellationToken)
    {
        var ids = permit.Lines.Select(l => l.LineId).ToArray();
        if (db.ChangeTracker.Entries<StockCountCostBasis>().Any(e =>
                ids.Contains(e.Entity.StockCountLineId)
                && e.State is EntityState.Added or EntityState.Modified
                    or EntityState.Deleted))
            throw Invalid("A count cost basis has pending changes.");
        var existing = await db.StockCountCostBases.AsNoTracking()
            .Where(b => ids.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        if (permit.CountStatus == IvStockCountStatuses.Counted)
        {
            if (existing.Length != 0 || proposed.Any(b => b.Id != 0))
                throw Invalid("A first count post already has immutable cost bases.");
            db.StockCountCostBases.AddRange(proposed);
            return;
        }
        if (permit.CountStatus != IvStockCountStatuses.RolledBack
            || existing.Length != proposed.Count)
            throw Invalid("A repost lacks its original immutable cost bases.");
        var byLine = existing.ToDictionary(b => b.StockCountLineId);
        if (proposed.Any(b => !byLine.TryGetValue(b.StockCountLineId,
                    out var saved)
                || saved.Id != b.Id
                || saved.CompanyCode != b.CompanyCode
                || saved.BranchCode != b.BranchCode
                || saved.FrozenFallbackUnitCost != b.FrozenFallbackUnitCost
                || saved.CostBasisKind != b.CostBasisKind
                || saved.StandardCostRevisionId != b.StandardCostRevisionId
                || saved.CostBasisWatermark != b.CostBasisWatermark
                || saved.CostBasisHash != b.CostBasisHash
                || saved.CostEvidenceType != b.CostEvidenceType
                || saved.CostOverrideReason != b.CostOverrideReason
                || saved.CostApprovedBy != b.CostApprovedBy
                || saved.CostApprovedAtUtc != b.CostApprovedAtUtc
                || saved.CreatedAtUtc != b.CreatedAtUtc))
            throw Invalid("A repost cost basis differs from the immutable original.");
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
