using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Handoff second-stage daily production output identity (optional FG receipt on stage 2).</summary>
internal sealed record HandoffStageCorrection(
    long ConsumeMovementId,
    long ProduceMovementId,
    long? ActiveFinishedGoodId = null,
    decimal ActiveFinishedGoodValue = 0m);

/// <summary>Restates one issue and its isolated Daily Production output inside the receipt root.</summary>
internal static class ProductionOutputIssueCostCorrectionWriter
{
    public static async Task AppendWithinRootAsync(
        StockPostingContext context, long issueId, long consumeId, long produceId,
        bool outputReversed, ProductionEffectiveCost expectedIssue, CancellationToken ct,
        long? activeFinishedGoodId = null, decimal activeFinishedGoodValue = 0m,
        bool allowReversedFinishedGood = false,
        long? activeTransferOutId = null, decimal activeTransferOutValue = 0m,
        IReadOnlyCollection<long>? outputTerminalAdjustmentIds = null,
        decimal? outputTerminalAdjustmentPoolBaseQty = null,
        decimal? outputTerminalAdjustmentPoolValue = null,
        HandoffStageCorrection? handoff = null,
        TransferDownstreamStageCorrection? transferDownstream = null)
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
        var ids = handoff is null
            ? new[] { issueId, consumeId, produceId }
            : new[] { issueId, consumeId, produceId, handoff.ConsumeMovementId, handoff.ProduceMovementId };
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => ids.Contains(m.Uid)).ToDictionaryAsync(m => m.Uid, ct);
        if (ids.Distinct().Count() != ids.Length || movements.Count != ids.Length)
            throw Invalid("Production output correction has invalid movement identities.");
        var issue = movements[issueId];
        var consume = movements[consumeId];
        var produce = movements[produceId];
        var handoffConsume = handoff is null ? null : movements[handoff.ConsumeMovementId];
        var handoffProduce = handoff is null ? null : movements[handoff.ProduceMovementId];
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
        var stage2 = handoffProduce is null ? null
            : await db.ProductionBalLots.SingleAsync(l => l.Uid == handoffProduce.ProductionBalLotId, ct);
        var stage2Pool = handoffProduce is null ? null
            : await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == handoffProduce.ProductionBalLotId, ct);
        var hasOutputAdjustments = outputTerminalAdjustmentIds is { Count: > 0 };
        if ((activeFinishedGoodId is not null ? 1 : 0)
            + (activeTransferOutId is not null ? 1 : 0)
            + (hasOutputAdjustments ? 1 : 0)
            + (handoff is not null ? 1 : 0) > 1)
            throw Invalid("Production output cannot combine finished-good, work-order transfer, handoff, and terminal adjustment replay.");
        if (hasOutputAdjustments && (outputReversed
            || outputTerminalAdjustmentPoolBaseQty is null || outputTerminalAdjustmentPoolValue is null))
            throw Invalid("Production output terminal adjustment replay parameters are incomplete.");
        if (handoff is not null && (outputReversed
            || handoffConsume is null || handoffProduce is null
            || handoffConsume.MovementType != ProductionBalLotMovementTypes.Consume
            || handoffProduce.MovementType != ProductionBalLotMovementTypes.Produce
            || handoffConsume.ProductionBalLotId != target.Uid
            || handoffConsume.ProductionOutputId != handoffProduce.ProductionOutputId
            || handoffConsume.ProductionOutputId == produce.ProductionOutputId
            || handoffConsume.BaseQty <= 0m || handoffConsume.BaseQty > produce.BaseQty
            || handoffProduce.BaseQty <= 0m
            || stage2 is null || stage2Pool is null
            || stage2.Uid == target.Uid || stage2.Uid == source.Uid
            || stage2Pool.Status != ProductionPoolValuationService.Verified
            || stage2Pool.TrackedBaseQty != stage2.BaseQty || stage2Pool.TrackedValue != stage2.TotalCost))
            throw Invalid("Production handoff stage dependencies are inconsistent.");
        var effectiveFinishedGoodId = activeFinishedGoodId ?? handoff?.ActiveFinishedGoodId;
        var effectiveFinishedGoodValue = activeFinishedGoodId is not null
            ? activeFinishedGoodValue : (handoff?.ActiveFinishedGoodValue ?? 0m);
        var finished = effectiveFinishedGoodId is long finishedId
            ? await db.ProductionBalLotMovements.AsNoTracking().SingleOrDefaultAsync(m => m.Uid == finishedId, ct)
            : null;
        var fgExpectedSource = handoffProduce ?? produce;
        if (effectiveFinishedGoodId is not null && (outputReversed
            || context.CostMethod is not (StockCostMethods.MovingAverage or StockCostMethods.Fifo)
            || finished is null || finished.MovementType != ProductionBalLotMovementTypes.FgReceiptOut
            || finished.ProductionBalLotId != fgExpectedSource.ProductionBalLotId
            || finished.BaseQty > fgExpectedSource.BaseQty))
            throw Invalid("Production output has an unsupported finished-good dependency.");
        var transferOut = activeTransferOutId is long transferId
            ? await db.ProductionBalLotMovements.AsNoTracking().SingleOrDefaultAsync(m => m.Uid == transferId, ct)
            : null;
        if (activeTransferOutId is not null && (outputReversed
            || transferOut is null || transferOut.MovementType != ProductionBalLotMovementTypes.TransferOut
            || transferOut.ProductionBalLotId != target.Uid || transferOut.BaseQty != produce.BaseQty))
            throw Invalid("Production output has an unsupported work-order transfer dependency.");
        var downstreamQty = handoffConsume?.BaseQty ?? finished?.BaseQty ?? transferOut?.BaseQty ?? 0m;
        var downstreamValue = handoffConsume is not null ? costs[handoffConsume.Uid].TotalCost
            : finished is not null ? effectiveFinishedGoodValue
            : transferOut is not null ? activeTransferOutValue : 0m;
        var expectedTargetQty = hasOutputAdjustments ? outputTerminalAdjustmentPoolBaseQty!.Value : produce.BaseQty - downstreamQty;
        var expectedTargetValue = hasOutputAdjustments ? outputTerminalAdjustmentPoolValue!.Value : costs[produceId].TotalCost - downstreamValue;
        var expectedStage2Qty = handoffProduce is null ? 0m : handoffProduce.BaseQty - (finished?.BaseQty ?? 0m);
        var expectedStage2Value = handoffProduce is null ? 0m : costs[handoffProduce.Uid].TotalCost - (finished is not null ? effectiveFinishedGoodValue : 0m);
        if (source.Uid == target.Uid || sourcePool.Status != ProductionPoolValuationService.Verified
            || targetPool.Status != ProductionPoolValuationService.Verified
            || sourcePool.TrackedBaseQty != source.BaseQty || sourcePool.TrackedValue != source.TotalCost
            || targetPool.TrackedBaseQty != target.BaseQty || targetPool.TrackedValue != target.TotalCost
            || source.TotalCost != expectedIssue.TotalCost - (outputReversed ? 0m : costs[consumeId].TotalCost)
            || source.BaseQty != issue.BaseQty - (outputReversed ? 0m : consume.BaseQty)
            || target.BaseQty != (outputReversed ? 0m : expectedTargetQty)
            || target.TotalCost != (outputReversed ? 0m : expectedTargetValue)
            || (handoffProduce is not null && (stage2!.BaseQty != expectedStage2Qty || stage2.TotalCost != expectedStage2Value)))
            throw Invalid("Production output pools changed after replay planning.");

        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db,
            context.CompanyCode, context.BranchCode, [issueId], ct);
        if (hasOutputAdjustments)
        {
            var allowedIds = new HashSet<long>(outputTerminalAdjustmentIds!) { issueId, consumeId, produceId };
            if (!graph.Edges.Any(e => e.ContributorMovementId == issueId && e.ConsumerMovementId == consumeId)
                || !graph.Edges.Any(e => e.ContributorMovementId == consumeId && e.ConsumerMovementId == produceId)
                || graph.Edges.Any(e => !allowedIds.Contains(e.ContributorMovementId) || !allowedIds.Contains(e.ConsumerMovementId)))
                throw Invalid("Production output terminal adjustment dependencies changed after replay planning.");
        }
        else if (handoff is not null)
        {
            var expectedOrderedCount = finished is not null ? 6 : 5;
            var expectedEdgeCount = finished is not null ? 5 : 4;
            if (graph.OrderedMovementIds.Count != expectedOrderedCount
                || graph.Edges.Count != expectedEdgeCount
                || !graph.Edges.Any(e => e.ContributorMovementId == issueId && e.ConsumerMovementId == consumeId)
                || !graph.Edges.Any(e => e.ContributorMovementId == consumeId && e.ConsumerMovementId == produceId)
                || !graph.Edges.Any(e => e.ContributorMovementId == produceId && e.ConsumerMovementId == handoffConsume!.Uid)
                || !graph.Edges.Any(e => e.ContributorMovementId == handoffConsume!.Uid && e.ConsumerMovementId == handoffProduce!.Uid)
                || finished is not null && !graph.Edges.Any(e => e.ContributorMovementId == handoffProduce!.Uid
                    && e.ConsumerMovementId == finished.Uid))
                throw Invalid("Production handoff dependencies changed after replay planning.");
        }
        else if (transferDownstream is not null && activeTransferOutId is null)
            throw Invalid("Work-order transfer downstream replay requires an active work-order transfer.");
        else
        {
            var expectedOrderedCount = transferOut is not null
                ? transferDownstream is not null ? (transferDownstream.ActiveFinishedGoodId is not null ? 8 : 7) : 5
                : finished is not null ? 4 : 3;
            var expectedEdgeCount = transferOut is not null
                ? transferDownstream is not null ? (transferDownstream.ActiveFinishedGoodId is not null ? 7 : 6) : 4
                : finished is not null ? 3 : 2;
            if (outputReversed && (graph.OrderedMovementIds.Count != 1 || graph.Edges.Count != 0)
                || !outputReversed && (graph.OrderedMovementIds.Count != expectedOrderedCount
                    || graph.Edges.Count != expectedEdgeCount
                    || !graph.Edges.Any(e => e.ContributorMovementId == issueId && e.ConsumerMovementId == consumeId)
                    || !graph.Edges.Any(e => e.ContributorMovementId == consumeId && e.ConsumerMovementId == produceId)
                    || finished is not null && !graph.Edges.Any(e => e.ContributorMovementId == produceId
                        && e.ConsumerMovementId == finished.Uid)
                    || transferOut is not null && (!graph.Edges.Any(e => e.ContributorMovementId == produceId
                            && e.ConsumerMovementId == transferOut.Uid)
                        || graph.Edges.Count(e => e.ContributorMovementId == transferOut.Uid) != 1)
                    || transferDownstream is not null && (!graph.Edges.Any(e => e.ConsumerMovementId == transferDownstream.ConsumeMovementId)
                        || !graph.Edges.Any(e => e.ContributorMovementId == transferDownstream.ConsumeMovementId
                            && e.ConsumerMovementId == transferDownstream.ProduceMovementId)
                        || transferDownstream.ActiveFinishedGoodId is long transferFgId && !graph.Edges.Any(e =>
                            e.ContributorMovementId == transferDownstream.ProduceMovementId
                            && e.ConsumerMovementId == transferFgId))))
                throw Invalid("Production output dependencies changed after replay planning.");
        }

        var correctedConsume = consume.BaseQty == issue.BaseQty ? authority.TotalCost
            : StockLedgerPrecision.Money(authority.TotalCost * consume.BaseQty / issue.BaseQty);
        var correctedSource = StockLedgerPrecision.Money(authority.TotalCost
            - (outputReversed ? 0m : correctedConsume));
        var correctedProduce = StockLedgerPrecision.Money(costs[produceId].TotalCost
            + correctedConsume - costs[consumeId].TotalCost);
        var correctedConsume2 = handoffConsume is null ? 0m
            : handoffConsume.BaseQty == produce.BaseQty ? correctedProduce
            : StockLedgerPrecision.Money(correctedProduce * handoffConsume.BaseQty / produce.BaseQty);
        var correctedWip = handoffConsume is null ? correctedProduce
            : StockLedgerPrecision.Money(correctedProduce - correctedConsume2);
        var correctedProduce2 = handoffProduce is null ? 0m
            : StockLedgerPrecision.Money(costs[handoffProduce.Uid].TotalCost
                + correctedConsume2 - costs[handoffConsume!.Uid].TotalCost);
        if (correctedSource < 0m || correctedProduce < 0m || correctedWip < 0m || correctedProduce2 < 0m
            || source.BaseQty == 0m && correctedSource != 0m
            || handoff is not null && target.BaseQty == 0m && correctedWip != 0m)
            throw Invalid("Production output correction would leave a negative or stranded pool value.");

        await ProductionCostRestatementWriter.AppendWithinRootAsync(context, expectedIssue,
            authority.TotalCost, authority.UnitCost, ct);
        if (outputReversed && (correctedConsume != costs[consumeId].TotalCost
            || correctedProduce != costs[produceId].TotalCost))
        {
            await ProductionReversedOutputCostCorrectionWriter.AppendWithinRootAsync(context,
                consume, produce, costs[consumeId], costs[produceId],
                correctedConsume, correctedProduce, ct, allowReversedFinishedGood);
        }
        else if (!outputReversed)
        {
            await ProductionCostRestatementWriter.AppendWithinRootAsync(context, costs[consumeId],
                correctedConsume, StockLedgerPrecision.Money(correctedConsume / consume.BaseQty), ct);
            await ProductionCostRestatementWriter.AppendWithinRootAsync(context, costs[produceId],
                correctedProduce, StockLedgerPrecision.Money(correctedProduce / produce.BaseQty), ct);
            if (handoff is not null)
            {
                await ProductionCostRestatementWriter.AppendWithinRootAsync(context, costs[handoffConsume!.Uid],
                    correctedConsume2, StockLedgerPrecision.Money(correctedConsume2 / handoffConsume.BaseQty), ct);
                await ProductionCostRestatementWriter.AppendWithinRootAsync(context, costs[handoffProduce!.Uid],
                    correctedProduce2, StockLedgerPrecision.Money(correctedProduce2 / handoffProduce.BaseQty), ct);
            }
        }
        source.TotalCost = correctedSource;
        source.AverageUnitCost = source.BaseQty > 0m
            ? StockLedgerPrecision.Money(correctedSource / source.BaseQty) : 0m;
        sourcePool.TrackedValue = correctedSource;
        if (!outputReversed && handoff is not null)
        {
            target.TotalCost = correctedWip;
            target.AverageUnitCost = target.BaseQty > 0m
                ? StockLedgerPrecision.Money(correctedWip / target.BaseQty) : 0m;
            targetPool.TrackedValue = correctedWip;
            if (finished is null)
            {
                stage2!.TotalCost = correctedProduce2;
                stage2.AverageUnitCost = StockLedgerPrecision.Money(correctedProduce2 / handoffProduce!.BaseQty);
                stage2Pool!.TrackedValue = correctedProduce2;
            }
        }
        else if (!outputReversed && finished is null && transferOut is null && !hasOutputAdjustments)
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
