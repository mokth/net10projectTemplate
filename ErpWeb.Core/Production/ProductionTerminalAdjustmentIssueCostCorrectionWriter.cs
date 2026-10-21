using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionTerminalAdjustmentIssueCorrectionResult(
    ProductionCostRestatementWriteResult Issue,
    ProductionCostRestatementWriteResult Adjustment,
    decimal RemainingValue);

/// <summary>
/// Reprices one partly or fully consumed material ISSUE when its only consumer is a
/// terminal pool-average Production ADJUST_OUT. The caller owns the root transaction.
/// </summary>
internal static class ProductionTerminalAdjustmentIssueCostCorrectionWriter
{
    public static async Task<ProductionTerminalAdjustmentIssueCorrectionResult> AppendWithinRootAsync(
        StockPostingContext context,
        ProductionEffectiveCost expectedIssue,
        ProductionEffectiveCost expectedAdjustment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedIssue);
        ArgumentNullException.ThrowIfNull(expectedAdjustment);
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null)
            throw Invalid("Production terminal adjustment correction requires the replay root transaction.");
        if (expectedIssue.MovementId <= 0 || expectedAdjustment.MovementId <= 0
            || expectedIssue.MovementId == expectedAdjustment.MovementId
            || db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Invalid("Production terminal adjustment correction has pending pool changes or invalid movement IDs.");

        var authority = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [expectedIssue.MovementId], cancellationToken))[expectedIssue.MovementId];
        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db,
            context.CompanyCode, context.BranchCode,
            [expectedIssue.MovementId], cancellationToken);
        if (graph.OrderedMovementIds.Count != 2
            || graph.Edges.Count != 1
            || graph.Edges[0].ContributorMovementId != expectedIssue.MovementId
            || graph.Edges[0].ConsumerMovementId != expectedAdjustment.MovementId)
            throw Invalid("Production ISSUE has another downstream dependency.");

        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.Uid == expectedIssue.MovementId
                || m.Uid == expectedAdjustment.MovementId)
            .ToDictionaryAsync(m => m.Uid, cancellationToken);
        if (movements.Count != 2)
            throw Invalid("Production terminal adjustment movement is missing.");
        var issue = movements[expectedIssue.MovementId];
        var adjustment = movements[expectedAdjustment.MovementId];
        var lot = await db.ProductionBalLots.SingleOrDefaultAsync(l =>
            l.Uid == issue.ProductionBalLotId, cancellationToken);
        var pool = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(p =>
            p.ProductionBalLotId == issue.ProductionBalLotId, cancellationToken);
        var poolMovementIds = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotId == issue.ProductionBalLotId)
            .Select(m => m.Uid).ToArrayAsync(cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(e => e.MovementId == issue.Uid || e.MovementId == adjustment.Uid)
            .ToArrayAsync(cancellationToken);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(a => a.ReceiptMovementId == issue.Uid
                || a.OutboundMovementId == adjustment.Uid)
            .ToArrayAsync(cancellationToken);
        var adjustmentFacts = await db.ProductionStockAdjustmentFacts.AsNoTracking()
            .Where(f => f.ProductionMovementId == adjustment.Uid)
            .ToArrayAsync(cancellationToken);
        var adjustmentOwner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == adjustment.StockPostingId,
                cancellationToken);
        if (lot is null || pool is null
            || lot.CompanyCode != context.CompanyCode || lot.BranchCode != context.BranchCode
            || lot.Kind != ProductionBalLotKinds.MaterialIn
            || lot.OriginalIssueMovementId != authority.MaterialMovementId
            || issue.MovementType != ProductionBalLotMovementTypes.Issue
            || adjustment.MovementType != ProductionBalLotMovementTypes.AdjustOut
            || adjustment.DocumentType != ProductionDocumentTypes.ProductionStockAdjustment
            || adjustmentOwner is null
            || adjustmentOwner.SourceModule != "PRODUCTION"
            || adjustmentOwner.SourceDocumentType != ProductionDocumentTypes.ProductionStockAdjustment
            || adjustmentOwner.SourceDocumentId != adjustmentFacts.FirstOrDefault()?.BatchId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || issue.OriginalMovementId is not null || adjustment.OriginalMovementId is not null
            || issue.ProductionBalLotId != adjustment.ProductionBalLotId
            || issue.ItemCode != adjustment.ItemCode || issue.ItemCode != lot.ItemCode
            || issue.BaseQty < adjustment.BaseQty || adjustment.BaseQty <= 0m
            || issue.Qty < adjustment.Qty || adjustment.Qty <= 0m
            || issue.BaseQty != authority.BaseQty
            || issue.BaseUom != adjustment.BaseUom || issue.BaseUom != lot.BaseUom
            || issue.Uom != adjustment.Uom || issue.Uom != lot.Uom
            || lot.BaseQty != issue.BaseQty - adjustment.BaseQty
            || lot.Qty != issue.Qty - adjustment.Qty
            || lot.BaseQty < 0m || lot.Qty < 0m
            || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost
            || pool.Generation <= 0
            || poolMovementIds.Length != 2
            || !poolMovementIds.Contains(issue.Uid) || !poolMovementIds.Contains(adjustment.Uid)
            || evidence.Length != 2 || evidence.Any(e =>
                e.ProductionBalLotId != lot.Uid || e.Generation != pool.Generation
                || e.Status != ProductionPoolValuationService.Verified)
            || evidence.Count(e => e.MovementId == issue.Uid) != 1
            || evidence.Count(e => e.MovementId == adjustment.Uid) != 1
            || evidence.First(e => e.MovementId == adjustment.Uid).Basis != "POOLED_AVERAGE"
            || allocations.Length != 1
            || allocations[0].CompanyCode != context.CompanyCode
            || allocations[0].BranchCode != context.BranchCode
            || allocations[0].StockPostingId != adjustment.StockPostingId
            || allocations[0].ReceiptMovementId != issue.Uid
            || allocations[0].OutboundMovementId != adjustment.Uid
            || allocations[0].BaseQty != adjustment.BaseQty
            || adjustmentFacts.Length != 1
            || adjustmentFacts[0].StockPostingId != adjustment.StockPostingId
            || adjustmentFacts[0].BaseQty != adjustment.BaseQty
            || adjustmentFacts[0].TotalValue != adjustment.TotalCost
            || adjustmentFacts[0].UnitCost != adjustment.UnitCost
            || adjustmentFacts[0].CostBasisCode != "POOLED_AVERAGE"
            || adjustmentFacts[0].ReversesFactId is not null)
            throw Invalid("Production material pool is not an isolated pool-average terminal adjustment.");

        var current = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [issue.Uid, adjustment.Uid], cancellationToken);
        var orderedAdjustment = new ProductionTerminalAdjustmentCostStep(
            adjustment.Uid, adjustment.BaseQty);
        var previousProjection = ProductionTerminalAdjustmentCostProjection.Project(
            issue.BaseQty, expectedIssue.TotalCost, [orderedAdjustment]);
        if (current[issue.Uid] != expectedIssue
            || current[adjustment.Uid] != expectedAdjustment
            || issue.TotalCost != expectedIssue.TotalCost && expectedIssue.RestatementFactId is null
            || previousProjection.OutboundValues[adjustment.Uid] != expectedAdjustment.TotalCost
            || previousProjection.RemainingBaseQty != lot.BaseQty
            || previousProjection.RemainingValue != lot.TotalCost
            || pool.TrackedValue != lot.TotalCost
            || lot.BaseQty == 0m && (lot.TotalCost != 0m || lot.AverageUnitCost != 0m)
            || lot.BaseQty > 0m && lot.AverageUnitCost != StockLedgerPrecision.Money(
                lot.TotalCost / lot.BaseQty))
            throw Stale("Production terminal adjustment cost or pool changed after planning.");

        var correctedProjection = ProductionTerminalAdjustmentCostProjection.Project(
            issue.BaseQty, authority.TotalCost, [orderedAdjustment]);
        var correctedAdjustment = correctedProjection.OutboundValues[adjustment.Uid];
        var correctedUnit = StockLedgerPrecision.Money(
            correctedAdjustment / adjustment.BaseQty);
        var remaining = correctedProjection.RemainingValue;
        if (remaining < 0m || StockLedgerPrecision.Money(authority.TotalCost / issue.BaseQty)
                != authority.UnitCost)
            throw Invalid("Production terminal adjustment corrected cost does not reconcile.");

        var issueResult = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expectedIssue, authority.TotalCost, authority.UnitCost,
            cancellationToken);
        var adjustmentResult = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expectedAdjustment, correctedAdjustment, correctedUnit,
            cancellationToken);
        if (issueResult.Reversal is not null || issueResult.Replacement is not null
            || adjustmentResult.Reversal is not null || adjustmentResult.Replacement is not null)
        {
            lot.TotalCost = remaining;
            lot.AverageUnitCost = lot.BaseQty > 0m
                ? StockLedgerPrecision.Money(remaining / lot.BaseQty) : 0m;
            pool.TrackedValue = remaining;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(issueResult, adjustmentResult, remaining);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
