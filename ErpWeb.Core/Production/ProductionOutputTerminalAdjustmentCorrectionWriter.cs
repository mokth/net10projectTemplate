using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Reprices terminal ADJUST_OUT / ADJUST_IN movements on an isolated Daily Production
/// output WIP pool after the produce movement's effective cost has been restated inside
/// the same replay root transaction. Preserves fixed-authority inbound adjustments
/// (STANDARD_COST, MANUAL_APPROVED, ZERO_COST_APPROVED) and restates pool-average
/// adjustments (POOLED_AVERAGE / CURRENT_PRODUCTION_POOL_COST) in canonical posting order.
/// </summary>
internal static class ProductionOutputTerminalAdjustmentCorrectionWriter
{
    public static async Task AppendWithinRootAsync(
        StockPostingContext context,
        long produceMovementId,
        ProductionEffectiveCost expectedProduce,
        IReadOnlyCollection<ProductionEffectiveCost> expectedAdjustments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedProduce);
        ArgumentNullException.ThrowIfNull(expectedAdjustments);
        context.EnsureUnsealed();
        var db = context.Db;
        var expected = expectedAdjustments.ToArray();
        var adjustmentIds = expected.Select(x => x.MovementId).ToArray();
        if (db.Database.CurrentTransaction is null
            || produceMovementId <= 0 || expectedProduce.MovementId != produceMovementId
            || expected.Length == 0
            || adjustmentIds.Any(id => id <= 0 || id == produceMovementId)
            || adjustmentIds.Distinct().Count() != expected.Length
            || db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Invalid("Production output terminal adjustment correction has invalid inputs or pending pool edits.");

        var ids = adjustmentIds.Append(produceMovementId).ToArray();
        var idSet = ids.ToHashSet();
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => ids.Contains(m.Uid))
            .ToDictionaryAsync(m => m.Uid, cancellationToken);
        if (movements.Count != ids.Length)
            throw Invalid("Production output terminal adjustment movement is missing.");

        var produce = movements[produceMovementId];
        var lot = await db.ProductionBalLots.SingleOrDefaultAsync(l =>
            l.Uid == produce.ProductionBalLotId, cancellationToken);
        var pool = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(p =>
            p.ProductionBalLotId == produce.ProductionBalLotId, cancellationToken);
        var poolIds = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotId == produce.ProductionBalLotId)
            .Select(m => m.Uid).ToArrayAsync(cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(e => ids.Contains(e.MovementId))
            .ToArrayAsync(cancellationToken);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(a => ids.Contains(a.ReceiptMovementId) || ids.Contains(a.OutboundMovementId))
            .ToArrayAsync(cancellationToken);
        var facts = await db.ProductionStockAdjustmentFacts.AsNoTracking()
            .Where(f => adjustmentIds.Contains(f.ProductionMovementId))
            .ToArrayAsync(cancellationToken);
        var postingIds = ids.Select(id => movements[id].StockPostingId ?? 0)
            .Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (lot is null || pool is null
            || lot.CompanyCode != context.CompanyCode || lot.BranchCode != context.BranchCode
            || produce.MovementType != ProductionBalLotMovementTypes.Produce
            || produce.OriginalMovementId is not null
            || produce.BaseQty <= 0m || produce.Qty <= 0m
            || postingIds.Contains(0) || postings.Count != postingIds.Length
            || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost
            || pool.Generation <= 0
            || poolIds.Length != ids.Length || ids.Any(id => !poolIds.Contains(id))
            || evidence.Length != ids.Length || ids.Any(id =>
                evidence.Count(e => e.MovementId == id
                    && e.ProductionBalLotId == lot.Uid
                    && e.Generation > 0
                    && e.Generation <= pool.Generation
                    && e.Status == ProductionPoolValuationService.Verified) != 1)
            || allocations.Any(a => !idSet.Contains(a.ReceiptMovementId) || !idSet.Contains(a.OutboundMovementId))
            || facts.Length != expected.Length)
            throw Invalid("Production output pool is not an isolated terminal adjustment set.");

        var produceSequence = postings[produce.StockPostingId!.Value].PostingSequence;
        var factByMovementId = facts.ToDictionary(f => f.ProductionMovementId);
        foreach (var id in adjustmentIds)
        {
            var movement = movements[id];
            var posting = postings[movement.StockPostingId!.Value];
            var fact = factByMovementId.GetValueOrDefault(id);
            var ev = evidence.Single(e => e.MovementId == id);
            var outAllocs = allocations.Where(a => a.OutboundMovementId == id).ToArray();
            var isInbound = movement.MovementType == ProductionBalLotMovementTypes.AdjustIn;
            var isOutbound = movement.MovementType == ProductionBalLotMovementTypes.AdjustOut;
            if ((!isInbound && !isOutbound)
                || movement.DocumentType != ProductionDocumentTypes.ProductionStockAdjustment
                || movement.OriginalMovementId is not null
                || movement.ProductionBalLotId != produce.ProductionBalLotId
                || movement.ItemCode != produce.ItemCode || produce.ItemCode != lot.ItemCode
                || movement.BaseQty <= 0m || movement.Qty <= 0m
                || movement.BaseUom != produce.BaseUom || produce.BaseUom != lot.BaseUom
                || movement.Uom != produce.Uom || produce.Uom != lot.Uom
                || movement.PostingLineNo is not > 0
                || movement.MovementDate < produce.MovementDate
                || posting.PostingSequence <= produceSequence
                || posting.SourceModule != "PRODUCTION"
                || posting.SourceDocumentType != ProductionDocumentTypes.ProductionStockAdjustment
                || fact is null || fact.StockPostingId != posting.Id
                || posting.SourceDocumentId != fact.BatchId.ToString(CultureInfo.InvariantCulture)
                || fact.BaseQty != movement.BaseQty
                || fact.TotalValue != movement.TotalCost
                || fact.UnitCost != movement.UnitCost
                || fact.ReversesFactId is not null
                || facts.Count(f => f.ProductionMovementId == id) != 1)
                throw Invalid($"Production output PADJ {id} lacks exact terminal adjustment authority.");

            if (isOutbound)
            {
                if (fact.CostBasisCode is not ("POOLED_AVERAGE"
                        or ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost)
                    || ev.Basis != "POOLED_AVERAGE"
                    || outAllocs.Length == 0
                    || outAllocs.Any(a => a.CompanyCode != context.CompanyCode
                        || a.BranchCode != context.BranchCode
                        || a.StockPostingId != posting.Id)
                    || StockLedgerPrecision.Quantity(outAllocs.Sum(a => a.BaseQty)) != movement.BaseQty)
                    throw Invalid($"Production output ADJUST_OUT {id} lacks exact pool-average authority.");
            }
            else if (!ProductionTerminalAdjustmentIssueSetCorrectionWriter.IsValidInboundAdjustmentAuthority(
                    fact, ev.Basis, expectedSourceMaterialMovementId: null, allowOriginalIssueCost: false)
                || outAllocs.Length != 0)
            {
                throw Invalid($"Production output ADJUST_IN {id} lacks exact inbound cost authority.");
            }
        }

        var ordered = adjustmentIds.Select(id => movements[id])
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => postings[m.StockPostingId!.Value].PostingSequence)
            .ThenBy(m => m.PostingLineNo).ThenBy(m => m.Uid).ToArray();
        if (ordered.GroupBy(m => (m.StockPostingId, m.PostingLineNo))
            .Any(g => g.Count() > 1))
            throw Invalid("Production output terminal adjustments have ambiguous posting lines.");

        var current = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, ids, cancellationToken);
        if (expected.Any(x => current[x.MovementId] != x))
            throw Stale("Production output terminal adjustment effective costs changed after planning.");

        var steps = ordered.Select(m =>
            ProductionTerminalAdjustmentIssueSetCorrectionWriter.BuildCostStep(m, factByMovementId[m.Uid])).ToArray();
        var previous = ProductionTerminalAdjustmentCostProjection.Project(
            produce.BaseQty, expectedProduce.TotalCost, steps);
        var sourceGen = evidence.Single(e => e.MovementId == produce.Uid).Generation;
        var expectedQty = StockLedgerPrecision.Quantity(produce.Qty
            + ordered.Where(m => m.MovementType == ProductionBalLotMovementTypes.AdjustIn).Sum(m => m.Qty)
            - ordered.Where(m => m.MovementType == ProductionBalLotMovementTypes.AdjustOut).Sum(m => m.Qty));
        if (sourceGen + previous.FinalGenerationOffset != pool.Generation
            || ordered.Any(m => evidence.Single(e => e.MovementId == m.Uid).Generation
                != sourceGen + previous.GenerationOffsets[m.Uid])
            || previous.RemainingBaseQty != lot.BaseQty
            || previous.RemainingValue != lot.TotalCost
            || lot.Qty != expectedQty
            || lot.BaseQty < 0m || lot.Qty < 0m
            || lot.BaseQty == 0m && (lot.TotalCost != 0m || lot.AverageUnitCost != 0m)
            || lot.BaseQty > 0m && lot.AverageUnitCost != StockLedgerPrecision.Money(
                lot.TotalCost / lot.BaseQty)
            || expected.Any(x => previous.OutboundValues[x.MovementId] != x.TotalCost))
            throw Stale("Production output terminal adjustment pool changed after planning.");

        var newProduceCost = current[produceMovementId].TotalCost;
        var corrected = ProductionTerminalAdjustmentCostProjection.Project(
            produce.BaseQty, newProduceCost, steps);
        var anyRestated = false;
        foreach (var movement in ordered)
        {
            var value = corrected.OutboundValues[movement.Uid];
            var unit = ProductionStockAdjustmentMath.UnitCostFromTotal(movement.BaseQty, value);
            var prior = expected.Single(x => x.MovementId == movement.Uid);
            if (prior.TotalCost == value && prior.UnitCost == unit)
                continue;
            var written = await ProductionCostRestatementWriter.AppendWithinRootAsync(
                context, prior, value, unit, cancellationToken);
            if (written.Reversal is not null || written.Replacement is not null)
                anyRestated = true;
        }
        if (anyRestated || lot.TotalCost != corrected.RemainingValue)
        {
            lot.TotalCost = corrected.RemainingValue;
            lot.AverageUnitCost = lot.BaseQty > 0m
                ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;
            pool.TrackedValue = corrected.RemainingValue;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
