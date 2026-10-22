using System.Globalization;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionTerminalAdjustmentIssueSetCorrectionResult(
    ProductionCostRestatementWriteResult Issue,
    IReadOnlyDictionary<long, ProductionCostRestatementWriteResult> Adjustments,
    decimal RemainingValue);

/// <summary>
/// Reprices one material ISSUE with several terminal pool-average ADJUST_OUT
/// descendants in their sealed business posting order.
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
        if (expectedIssue.MovementId <= 0 || expected.Length < 2
            || adjustmentIds.Any(id => id <= 0 || id == expectedIssue.MovementId)
            || adjustmentIds.Distinct().Count() != expected.Length
            || db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Invalid("Production terminal adjustment set has invalid IDs or pending pool edits.");
        var expectedById = expected.ToDictionary(x => x.MovementId);
        var authority = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [expectedIssue.MovementId], cancellationToken))[expectedIssue.MovementId];
        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db,
            context.CompanyCode, context.BranchCode,
            [expectedIssue.MovementId], cancellationToken);
        if (graph.OrderedMovementIds.Count != expected.Length + 1
            || graph.Edges.Count != expected.Length
            || graph.Edges.Any(e => e.ContributorMovementId != expectedIssue.MovementId
                || !expectedById.ContainsKey(e.ConsumerMovementId))
            || graph.Edges.Select(e => e.ConsumerMovementId).Distinct().Count() != expected.Length)
            throw Invalid("Production ISSUE has another downstream dependency.");

        var ids = adjustmentIds.Append(expectedIssue.MovementId).ToArray();
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
            .Where(a => a.ReceiptMovementId == issue.Uid
                || adjustmentIds.Contains(a.OutboundMovementId))
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
                    && e.Generation == pool.Generation
                    && e.Status == ProductionPoolValuationService.Verified) != 1)
            || allocations.Length != expected.Length || facts.Length != expected.Length)
            throw Invalid("Production material pool is not an isolated terminal adjustment set.");

        var issueSequence = postings[issue.StockPostingId!.Value].PostingSequence;
        foreach (var id in adjustmentIds)
        {
            var movement = movements[id];
            var posting = postings[movement.StockPostingId!.Value];
            var fact = facts.FirstOrDefault(f => f.ProductionMovementId == id);
            var allocation = allocations.FirstOrDefault(a => a.OutboundMovementId == id);
            if (movement.MovementType != ProductionBalLotMovementTypes.AdjustOut
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
                || fact.CostBasisCode != "POOLED_AVERAGE"
                || fact.ReversesFactId is not null
                || evidence.Single(e => e.MovementId == id).Basis != "POOLED_AVERAGE"
                || allocation is null
                || allocation.CompanyCode != context.CompanyCode
                || allocation.BranchCode != context.BranchCode
                || allocation.StockPostingId != posting.Id
                || allocation.ReceiptMovementId != issue.Uid
                || allocation.BaseQty != movement.BaseQty
                || allocations.Count(a => a.OutboundMovementId == id) != 1
                || facts.Count(f => f.ProductionMovementId == id) != 1)
                throw Invalid($"Production ADJUST_OUT {id} lacks exact pool-average authority.");
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
        var steps = ordered.Select(m => new ProductionTerminalAdjustmentCostStep(
            m.Uid, m.BaseQty)).ToArray();
        var previous = ProductionTerminalAdjustmentCostProjection.Project(
            issue.BaseQty, expectedIssue.TotalCost, steps);
        if (previous.RemainingBaseQty != lot.BaseQty
            || previous.RemainingValue != lot.TotalCost
            || lot.Qty != issue.Qty - ordered.Sum(m => m.Qty)
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
            results.Add(movement.Uid,
                await ProductionCostRestatementWriter.AppendWithinRootAsync(
                    context, expected.Single(x => x.MovementId == movement.Uid),
                    value, unit, cancellationToken));
        }
        if (issueResult.Reversal is not null || issueResult.Replacement is not null
            || results.Values.Any(r => r.Reversal is not null || r.Replacement is not null))
        {
            lot.TotalCost = corrected.RemainingValue;
            lot.AverageUnitCost = lot.BaseQty > 0m
                ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;
            pool.TrackedValue = corrected.RemainingValue;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(issueResult, results, corrected.RemainingValue);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
