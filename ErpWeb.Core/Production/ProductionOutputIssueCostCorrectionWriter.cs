using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Restates one issue and its isolated Daily Production output inside the receipt root.</summary>
internal static class ProductionOutputIssueCostCorrectionWriter
{
    public static async Task AppendWithinRootAsync(
        StockPostingContext context, long issueId, long consumeId, long produceId,
        bool outputReversed, ProductionEffectiveCost expectedIssue, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var db = context.Db;
        if (context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || context.Posting.PostingRole is not ("PRIMARY" or "REVERSAL")
            || context.CostMethod is not (StockCostMethods.MovingAverage or StockCostMethods.Fifo)
            || db.Database.CurrentTransaction is null
            || db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Invalid("Production output correction needs an unchanged pool and the replay transaction.");
        var ids = new[] { issueId, consumeId, produceId };
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => ids.Contains(m.Uid)).ToDictionaryAsync(m => m.Uid, ct);
        if (ids.Distinct().Count() != 3 || movements.Count != 3)
            throw Invalid("Production output correction has invalid movement identities.");
        var issue = movements[issueId];
        var consume = movements[consumeId];
        var produce = movements[produceId];
        var costs = await ProductionReplayEffectiveCostReader.ReadAsync(db,
            context.CompanyCode, context.BranchCode, ids, ct);
        var authority = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [issueId], ct))[issueId];
        if (costs[issueId] != expectedIssue || issue.BaseQty <= 0m
            || issue.MovementType != ProductionBalLotMovementTypes.Issue
            || consume.MovementType != ProductionBalLotMovementTypes.Consume
            || produce.MovementType != ProductionBalLotMovementTypes.Produce
            || consume.ProductionBalLotId != issue.ProductionBalLotId
            || consume.ProductionOutputId != produce.ProductionOutputId
            || consume.BaseQty <= 0m || consume.BaseQty > issue.BaseQty
            || authority.BaseQty != issue.BaseQty
            || authority.UnitCost != StockLedgerPrecision.Money(authority.TotalCost / issue.BaseQty))
            throw Invalid("Production output correction differs from its verified issue.");
        var source = await db.ProductionBalLots.SingleAsync(l => l.Uid == issue.ProductionBalLotId, ct);
        var sourcePool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == source.Uid, ct);
        var target = await db.ProductionBalLots.SingleAsync(l => l.Uid == produce.ProductionBalLotId, ct);
        var targetPool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == target.Uid, ct);
        if (source.Uid == target.Uid || sourcePool.Status != ProductionPoolValuationService.Verified
            || targetPool.Status != ProductionPoolValuationService.Verified
            || sourcePool.TrackedBaseQty != source.BaseQty || sourcePool.TrackedValue != source.TotalCost
            || targetPool.TrackedBaseQty != target.BaseQty || targetPool.TrackedValue != target.TotalCost
            || source.TotalCost != expectedIssue.TotalCost - (outputReversed ? 0m : costs[consumeId].TotalCost)
            || source.BaseQty != issue.BaseQty - (outputReversed ? 0m : consume.BaseQty)
            || target.BaseQty != (outputReversed ? 0m : produce.BaseQty)
            || target.TotalCost != (outputReversed ? 0m : costs[produceId].TotalCost))
            throw Invalid("Production output pools changed after replay planning.");

        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db,
            context.CompanyCode, context.BranchCode, [issueId], ct);
        if (outputReversed && (graph.OrderedMovementIds.Count != 1 || graph.Edges.Count != 0)
            || !outputReversed && (graph.OrderedMovementIds.Count != 3 || graph.Edges.Count != 2
                || !graph.Edges.Any(e => e.ContributorMovementId == issueId && e.ConsumerMovementId == consumeId)
                || !graph.Edges.Any(e => e.ContributorMovementId == consumeId && e.ConsumerMovementId == produceId)))
            throw Invalid("Production output dependencies changed after replay planning.");

        var correctedConsume = outputReversed ? costs[consumeId].TotalCost
            : consume.BaseQty == issue.BaseQty ? authority.TotalCost
            : StockLedgerPrecision.Money(authority.TotalCost * consume.BaseQty / issue.BaseQty);
        var correctedSource = StockLedgerPrecision.Money(authority.TotalCost
            - (outputReversed ? 0m : correctedConsume));
        var correctedProduce = outputReversed ? costs[produceId].TotalCost
            : StockLedgerPrecision.Money(costs[produceId].TotalCost
                + correctedConsume - costs[consumeId].TotalCost);
        if (correctedSource < 0m || correctedProduce < 0m
            || source.BaseQty == 0m && correctedSource != 0m)
            throw Invalid("Production output correction would leave a negative or stranded pool value.");

        await ProductionCostRestatementWriter.AppendWithinRootAsync(context, expectedIssue,
            authority.TotalCost, authority.UnitCost, ct);
        if (!outputReversed)
        {
            await ProductionCostRestatementWriter.AppendWithinRootAsync(context, costs[consumeId],
                correctedConsume, StockLedgerPrecision.Money(correctedConsume / consume.BaseQty), ct);
            await ProductionCostRestatementWriter.AppendWithinRootAsync(context, costs[produceId],
                correctedProduce, StockLedgerPrecision.Money(correctedProduce / produce.BaseQty), ct);
        }
        source.TotalCost = correctedSource;
        source.AverageUnitCost = source.BaseQty > 0m
            ? StockLedgerPrecision.Money(correctedSource / source.BaseQty) : 0m;
        sourcePool.TrackedValue = correctedSource;
        if (!outputReversed)
        {
            target.TotalCost = correctedProduce;
            target.AverageUnitCost = StockLedgerPrecision.Money(correctedProduce / produce.BaseQty);
            targetPool.TrackedValue = correctedProduce;
        }
        await db.SaveChangesAsync(ct);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
