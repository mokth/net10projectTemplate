using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionTerminalAdjustmentIssueSetCorrectionResult(
    ProductionCostRestatementWriteResult Issue,
    IReadOnlyDictionary<long, ProductionCostRestatementWriteResult> Adjustments,
    decimal RemainingValue);

/// <summary>
/// Reprices one material ISSUE with one or more terminal ADJUST_OUT / ADJUST_IN
/// descendants in their sealed business posting order across all approved PADJ
/// cost authorities.
/// </summary>
internal static class ProductionTerminalAdjustmentIssueSetCorrectionWriter
{
    public static async Task<ProductionTerminalAdjustmentIssueSetCorrectionResult>
        AppendWithinRootAsync(StockPostingContext context,
            ProductionEffectiveCost expectedIssue,
            IReadOnlyCollection<ProductionEffectiveCost> expectedAdjustments,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedIssue);
        ArgumentNullException.ThrowIfNull(expectedAdjustments);
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null)
            throw Invalid("Production terminal adjustment set needs the replay root transaction.");
        var expected = expectedAdjustments.ToArray();
        var adjustmentIds = expected.Select(x => x.MovementId).ToArray();
        if (expectedIssue.MovementId <= 0 || expected.Length < 1
            || adjustmentIds.Any(id => id <= 0 || id == expectedIssue.MovementId)
            || adjustmentIds.Distinct().Count() != expected.Length
            || db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Invalid("Production terminal adjustment set has invalid IDs or pending pool edits.");
        var authority = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [expectedIssue.MovementId], cancellationToken))[expectedIssue.MovementId];
        var ids = adjustmentIds.Append(expectedIssue.MovementId).ToArray();
        var idSet = ids.ToHashSet();
        var activeDependencies = await ProductionActiveEdgeResolver.Dependencies(db).AsNoTracking()
            .Where(e => ids.Contains(e.ContributorMovementId) || ids.Contains(e.ConsumerMovementId))
            .ToArrayAsync(cancellationToken);
        if (activeDependencies.Any(e => !idSet.Contains(e.ContributorMovementId) || !idSet.Contains(e.ConsumerMovementId)))
            throw Invalid("Production ISSUE has another downstream dependency.");

        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => ids.Contains(m.Uid))
            .ToDictionaryAsync(m => m.Uid, cancellationToken);
        if (movements.Count != ids.Length)
            throw Invalid("Production terminal adjustment set has a missing movement.");
        var issue = movements[expectedIssue.MovementId];
        var lot = await db.ProductionBalLots.SingleOrDefaultAsync(l =>
            l.Uid == issue.ProductionBalLotId, cancellationToken);
        var pool = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(p =>
            p.ProductionBalLotId == issue.ProductionBalLotId, cancellationToken);
        var poolIds = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotId == issue.ProductionBalLotId)
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
            || lot.Kind != ProductionBalLotKinds.MaterialIn
            || lot.OriginalIssueMovementId != authority.MaterialMovementId
            || issue.MovementType != ProductionBalLotMovementTypes.Issue
            || issue.OriginalMovementId is not null
            || issue.BaseQty <= 0m || issue.Qty <= 0m
            || issue.BaseQty != authority.BaseQty
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
            throw Invalid("Production material pool is not an isolated terminal adjustment set.");

        var issueSequence = postings[issue.StockPostingId!.Value].PostingSequence;
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
                || movement.ProductionBalLotId != issue.ProductionBalLotId
                || movement.ItemCode != issue.ItemCode || issue.ItemCode != lot.ItemCode
                || movement.BaseQty <= 0m || movement.Qty <= 0m
                || movement.BaseUom != issue.BaseUom || issue.BaseUom != lot.BaseUom
                || movement.Uom != issue.Uom || issue.Uom != lot.Uom
                || movement.PostingLineNo is not > 0
                || movement.MovementDate < issue.MovementDate
                || posting.PostingSequence <= issueSequence
                || posting.SourceModule != "PRODUCTION"
                || posting.SourceDocumentType != ProductionDocumentTypes.ProductionStockAdjustment
                || fact is null || fact.StockPostingId != posting.Id
                || posting.SourceDocumentId != fact.BatchId.ToString(CultureInfo.InvariantCulture)
                || fact.BaseQty != movement.BaseQty
                || fact.TotalValue != movement.TotalCost
                || fact.UnitCost != movement.UnitCost
                || fact.ReversesFactId is not null
                || facts.Count(f => f.ProductionMovementId == id) != 1)
                throw Invalid($"Production PADJ {id} lacks exact terminal adjustment authority.");

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
                    throw Invalid($"Production ADJUST_OUT {id} lacks exact pool-average authority.");
            }
            else if (!IsValidInboundAdjustmentAuthority(fact, ev.Basis, authority.MaterialMovementId, allowOriginalIssueCost: true)
                || outAllocs.Length != 0)
            {
                throw Invalid($"Production ADJUST_IN {id} lacks exact inbound cost authority.");
            }
        }
        var ordered = adjustmentIds.Select(id => movements[id])
            .OrderBy(m => m.MovementDate)
            .ThenBy(m => postings[m.StockPostingId!.Value].PostingSequence)
            .ThenBy(m => m.PostingLineNo).ThenBy(m => m.Uid).ToArray();
        if (ordered.GroupBy(m => (m.StockPostingId, m.PostingLineNo))
            .Any(g => g.Count() > 1))
            throw Invalid("Production terminal adjustments have ambiguous posting lines.");

        var current = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, ids, cancellationToken);
        if (current[issue.Uid] != expectedIssue
            || expected.Any(x => current[x.MovementId] != x))
            throw Stale("Production terminal adjustment effective costs changed after planning.");
        var steps = ordered.Select(m => BuildCostStep(m, factByMovementId[m.Uid])).ToArray();
        var previous = ProductionTerminalAdjustmentCostProjection.Project(
            issue.BaseQty, expectedIssue.TotalCost, steps);
        var sourceGen = evidence.Single(e => e.MovementId == issue.Uid).Generation;
        var expectedQty = StockLedgerPrecision.Quantity(issue.Qty
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
            throw Stale("Production terminal adjustment pool changed after planning.");

        var corrected = ProductionTerminalAdjustmentCostProjection.Project(
            issue.BaseQty, authority.TotalCost, steps);
        if (StockLedgerPrecision.Money(authority.TotalCost / issue.BaseQty)
            != authority.UnitCost)
            throw Invalid("Corrected Production ISSUE unit cost does not reconcile.");
        var issueResult = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expectedIssue, authority.TotalCost, authority.UnitCost,
            cancellationToken);
        var results = new Dictionary<long, ProductionCostRestatementWriteResult>();
        foreach (var movement in ordered)
        {
            var value = corrected.OutboundValues[movement.Uid];
            var unit = ProductionStockAdjustmentMath.UnitCostFromTotal(
                movement.BaseQty, value);
            var prior = expected.Single(x => x.MovementId == movement.Uid);
            if (prior.TotalCost == value && prior.UnitCost == unit)
            {
                results.Add(movement.Uid, new(null, null));
                continue;
            }
            results.Add(movement.Uid,
                await ProductionCostRestatementWriter.AppendWithinRootAsync(
                    context, prior, value, unit, cancellationToken));
        }
        if (issueResult.Reversal is not null || issueResult.Replacement is not null
            || results.Values.Any(r => r.Reversal is not null || r.Replacement is not null)
            || lot.TotalCost != corrected.RemainingValue)
        {
            lot.TotalCost = corrected.RemainingValue;
            lot.AverageUnitCost = lot.BaseQty > 0m
                ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;
            pool.TrackedValue = corrected.RemainingValue;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(issueResult, results, corrected.RemainingValue);
    }

    internal static bool IsValidInboundAdjustmentAuthority(
        ProductionStockAdjustmentFact fact, string evidenceBasis,
        long? expectedSourceMaterialMovementId, bool allowOriginalIssueCost)
    {
        if (evidenceBasis != fact.CostBasisCode)
            return false;
        return fact.CostBasisCode switch
        {
            ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost => true,
            ProductionStockAdjustmentCostBasisCodes.OriginalIssueCost =>
                allowOriginalIssueCost && expectedSourceMaterialMovementId is > 0
                && fact.OriginalIssueMovementId == expectedSourceMaterialMovementId,
            ProductionStockAdjustmentCostBasisCodes.StandardCost =>
                fact.StandardCostRevisionId is > 0 && fact.TotalValue >= 0m,
            InventoryCostEvidenceTypes.ManualApproved =>
                fact.CostEvidenceType == InventoryCostEvidenceTypes.ManualApproved
                && !string.IsNullOrWhiteSpace(fact.CostApprovedBy)
                && fact.CostApprovedAtUtc is not null
                && !string.IsNullOrWhiteSpace(fact.CostOverrideReason)
                && fact.TotalValue > 0m,
            InventoryCostEvidenceTypes.ZeroCostApproved =>
                fact.CostEvidenceType == InventoryCostEvidenceTypes.ZeroCostApproved
                && !string.IsNullOrWhiteSpace(fact.CostApprovedBy)
                && fact.CostApprovedAtUtc is not null
                && !string.IsNullOrWhiteSpace(fact.CostOverrideReason)
                && fact.TotalValue == 0m,
            _ => false
        };
    }

    internal static ProductionTerminalAdjustmentCostStep BuildCostStep(
        ProductionBalLotMovement movement, ProductionStockAdjustmentFact fact)
    {
        var isInbound = movement.MovementType == ProductionBalLotMovementTypes.AdjustIn;
        decimal? fixedTotal = isInbound && fact.CostBasisCode is ProductionStockAdjustmentCostBasisCodes.StandardCost
            or InventoryCostEvidenceTypes.ManualApproved or InventoryCostEvidenceTypes.ZeroCostApproved
            ? fact.TotalValue
            : null;
        return new ProductionTerminalAdjustmentCostStep(
            movement.Uid, movement.BaseQty, isInbound, fact.CostBasisCode, fixedTotal);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
