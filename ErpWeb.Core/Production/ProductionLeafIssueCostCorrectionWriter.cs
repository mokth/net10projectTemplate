using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Corrects the live value of one unconsumed material ISSUE pool after its
/// Inventory valuation has been replaced within the same replay root.
/// Descendant pools need ordered forward propagation and remain excluded.
/// </summary>
internal static class ProductionLeafIssueCostCorrectionWriter
{
    public static async Task<ProductionCostRestatementWriteResult> AppendWithinRootAsync(
        StockPostingContext context,
        ProductionEffectiveCost expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expected);
        context.EnsureUnsealed();
        if (expected.MovementId <= 0)
            throw Invalid("Production leaf ISSUE correction requires a movement identity.");
        var db = context.Db;
        if (db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Stale("Production pool has pending changes before cost correction.");

        var authority = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [expected.MovementId], cancellationToken))[expected.MovementId];
        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db,
            context.CompanyCode, context.BranchCode,
            [expected.MovementId], cancellationToken);
        if (graph.OrderedMovementIds.Count != 1
            || graph.OrderedMovementIds[0] != expected.MovementId
            || graph.Edges.Count != 0
            || await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
                .AnyAsync(a => a.ReceiptMovementId == expected.MovementId
                    || a.OutboundMovementId == expected.MovementId,
                    cancellationToken))
            throw Invalid("Production ISSUE has downstream cost dependencies.");

        var movement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(m => m.Uid == expected.MovementId, cancellationToken);
        var lot = await db.ProductionBalLots
            .SingleOrDefaultAsync(l => l.Uid == movement.ProductionBalLotId,
                cancellationToken);
        var pool = await db.ProductionPoolValuationRows
            .SingleOrDefaultAsync(p => p.ProductionBalLotId == movement.ProductionBalLotId,
                cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .SingleOrDefaultAsync(e => e.MovementId == movement.Uid,
                cancellationToken);
        var poolMovements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotId == movement.ProductionBalLotId)
            .Select(m => m.Uid).ToArrayAsync(cancellationToken);
        if (lot is null || pool is null || evidence is null
            || lot.CompanyCode != context.CompanyCode
            || lot.BranchCode != context.BranchCode
            || lot.Kind != ProductionBalLotKinds.MaterialIn
            || lot.OriginalIssueMovementId != authority.MaterialMovementId
            || lot.ItemCode != movement.ItemCode
            || lot.BaseQty != authority.BaseQty
            || lot.Qty != movement.Qty
            || pool.Generation <= 0
            || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty
            || evidence.ProductionBalLotId != lot.Uid
            || evidence.Generation != pool.Generation
            || evidence.Status != ProductionPoolValuationService.Verified
            || poolMovements.Length != 1
            || poolMovements[0] != movement.Uid)
            throw Invalid("Production ISSUE is not an isolated verified material pool.");
        if (lot.TotalCost != expected.TotalCost
            || pool.TrackedValue != expected.TotalCost
            || lot.AverageUnitCost != expected.UnitCost)
            throw Stale("Production ISSUE pool value changed after planning.");
        if (StockLedgerPrecision.Money(authority.TotalCost / lot.BaseQty)
            != authority.UnitCost)
            throw Invalid("Production ISSUE valuation unit cost does not reconcile.");

        var result = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expected, authority.TotalCost, authority.UnitCost,
            cancellationToken);
        if (result.Reversal is null && result.Replacement is null)
            return result;
        lot.TotalCost = authority.TotalCost;
        lot.AverageUnitCost = authority.UnitCost;
        pool.TrackedValue = authority.TotalCost;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
