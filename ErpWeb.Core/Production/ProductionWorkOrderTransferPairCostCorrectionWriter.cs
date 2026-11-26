using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record TransferDownstreamStageCorrection(
    long ConsumeMovementId,
    long ProduceMovementId,
    long? ActiveFinishedGoodId = null,
    decimal ActiveFinishedGoodValue = 0m);

internal sealed record ProductionWorkOrderTransferPairCorrectionResult(
    ProductionCostRestatementWriteResult Outbound,
    ProductionCostRestatementWriteResult Inbound,
    decimal TargetValue);

/// <summary>
/// Corrects both legs of a terminal Work Order Transfer within the
/// caller's replay transaction. The upstream WIP cost authority belongs to the caller.
/// </summary>
internal static class ProductionWorkOrderTransferPairCostCorrectionWriter
{
    public static async Task<ProductionWorkOrderTransferPairCorrectionResult> AppendFromSourceReceiptsWithinRootAsync(
        StockPostingContext context,
        long transferId,
        ProductionEffectiveCost expectedOut,
        ProductionEffectiveCost expectedIn,
        CancellationToken cancellationToken = default,
        TransferDownstreamStageCorrection? downstream = null)
    {
        var correctedValue = await ProductionWorkOrderTransferSourceCostAuthority.ReadWithinRootAsync(
            context, transferId, cancellationToken);
        return await AppendWithinRootAsync(context, transferId, expectedOut, expectedIn,
            correctedValue, cancellationToken, downstream);
    }

    public static async Task<ProductionWorkOrderTransferPairCorrectionResult> AppendWithinRootAsync(
        StockPostingContext context,
        long transferId,
        ProductionEffectiveCost expectedOut,
        ProductionEffectiveCost expectedIn,
        decimal correctedValue,
        CancellationToken cancellationToken = default,
        TransferDownstreamStageCorrection? downstream = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expectedOut);
        ArgumentNullException.ThrowIfNull(expectedIn);
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null || transferId <= 0
            || expectedOut.MovementId <= 0 || expectedIn.MovementId <= 0
            || expectedOut.MovementId == expectedIn.MovementId
            || correctedValue < 0m || StockLedgerPrecision.Money(correctedValue) != correctedValue
            || db.ChangeTracker.Entries<ProductionBalLot>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || db.ChangeTracker.Entries<ProductionPoolValuation>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw Invalid("Work Order Transfer correction needs valid IDs, cost and an unchanged root transaction.");

        var transfer = await db.ProductionWorkOrderTransfers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == transferId && x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode, cancellationToken);
        var fact = transfer?.StockPostingId is long postedOwnerId
            ? await db.ProductionWorkOrderTransferFacts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TransferId == transferId
                    && x.DocumentRevision == transfer.DocumentRevision
                    && x.StockPostingId == postedOwnerId && x.ReversesFactId == null,
                    cancellationToken)
            : null;
        if (transfer is null || fact is null
            || transfer.Status != ProductionWorkOrderTransferStatuses.Posted
            || transfer.ReversalPostingId is not null || transfer.StockPostingId is not long ownerId
            || transfer.TransferOutMovementId != expectedOut.MovementId
            || transfer.TransferInMovementId != expectedIn.MovementId
            || transfer.DestinationProductionBalLotId is null
            || fact.StockPostingId != ownerId || fact.DocumentRevision != transfer.DocumentRevision
            || fact.SourceLegMovementId != expectedOut.MovementId
            || fact.TargetLegMovementId != expectedIn.MovementId
            || fact.SourceProductionBalLotId != transfer.SourceProductionBalLotId
            || fact.DestinationProductionBalLotId != transfer.DestinationProductionBalLotId
            || fact.MovementGroupId != transfer.MovementGroupId
            || transfer.TransferTotalCost != fact.TotalValue)
            throw Invalid("Work Order Transfer correction has no active forward generation.");

        var ids = new[] { expectedOut.MovementId, expectedIn.MovementId };
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => ids.Contains(x.Uid)).ToDictionaryAsync(x => x.Uid, cancellationToken);
        if (movements.Count != 2)
            throw Invalid("Work Order Transfer correction has a missing movement.");
        var outbound = movements[expectedOut.MovementId];
        var inbound = movements[expectedIn.MovementId];
        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db,
            context.CompanyCode, context.BranchCode, [outbound.Uid], cancellationToken);
        var expectedOrderedCount = downstream is null ? 2 : downstream.ActiveFinishedGoodId is not null ? 5 : 4;
        var expectedEdgeCount = downstream is null ? 1 : downstream.ActiveFinishedGoodId is not null ? 4 : 3;
        if (graph.OrderedMovementIds.Count != expectedOrderedCount || graph.Edges.Count != expectedEdgeCount
            || !graph.Edges.Any(e => e.ContributorMovementId == outbound.Uid && e.ConsumerMovementId == inbound.Uid)
            || downstream is not null && (!graph.Edges.Any(e => e.ContributorMovementId == inbound.Uid
                    && e.ConsumerMovementId == downstream.ConsumeMovementId)
                || !graph.Edges.Any(e => e.ContributorMovementId == downstream.ConsumeMovementId
                    && e.ConsumerMovementId == downstream.ProduceMovementId)
                || downstream.ActiveFinishedGoodId is long expectedFgId && !graph.Edges.Any(e =>
                    e.ContributorMovementId == downstream.ProduceMovementId
                    && e.ConsumerMovementId == expectedFgId))
            || outbound.MovementType != ProductionBalLotMovementTypes.TransferOut
            || inbound.MovementType != ProductionBalLotMovementTypes.TransferIn
            || outbound.DocumentType != ProductionDocumentTypes.WorkOrderTransfer
            || inbound.DocumentType != ProductionDocumentTypes.WorkOrderTransfer
            || outbound.StockPostingId != ownerId || inbound.StockPostingId != ownerId
            || outbound.PostingLinkId != fact.ProductionPostingLinkId
            || inbound.PostingLinkId != fact.ProductionPostingLinkId
            || outbound.MovementGroupId != fact.MovementGroupId
            || inbound.MovementGroupId != fact.MovementGroupId
            || outbound.ProductionBalLotId != transfer.SourceProductionBalLotId
            || inbound.ProductionBalLotId != transfer.DestinationProductionBalLotId
            || outbound.OriginalMovementId is not null || inbound.OriginalMovementId is not null
            || outbound.BaseQty <= 0m || outbound.BaseQty != inbound.BaseQty
            || outbound.Qty != inbound.Qty || outbound.BaseUom != inbound.BaseUom
            || outbound.ItemCode != inbound.ItemCode
            || outbound.BaseQty != fact.BaseQty || outbound.Qty != fact.Qty
            || outbound.TotalCost != fact.TotalValue || inbound.TotalCost != fact.TotalValue
            || outbound.UnitCost != fact.UnitCost || inbound.UnitCost != fact.UnitCost
            || outbound.ValuationStatus != ProductionPoolValuationService.Verified
            || inbound.ValuationStatus != ProductionPoolValuationService.Verified)
            throw Invalid("Work Order Transfer correction lacks one exact verified OUT/IN pair.");

        var source = await db.ProductionBalLots.SingleOrDefaultAsync(x =>
            x.Uid == outbound.ProductionBalLotId, cancellationToken);
        var target = await db.ProductionBalLots.SingleOrDefaultAsync(x =>
            x.Uid == inbound.ProductionBalLotId, cancellationToken);
        var sourceValuation = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(x =>
            x.ProductionBalLotId == outbound.ProductionBalLotId, cancellationToken);
        var targetValuation = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(x =>
            x.ProductionBalLotId == inbound.ProductionBalLotId, cancellationToken);
        var targetMovementIds = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => x.ProductionBalLotId == inbound.ProductionBalLotId)
            .Select(x => x.Uid).ToArrayAsync(cancellationToken);
        var otherTargetIds = targetMovementIds.Where(id => id != inbound.Uid).ToArray();
        var otherTargetMovements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => otherTargetIds.Contains(x.Uid)).ToArrayAsync(cancellationToken);
        var otherTargetCosts = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, otherTargetIds, cancellationToken);
        var otherOwnerIds = otherTargetMovements.Select(x => x.StockPostingId ?? 0)
            .Distinct().ToArray();
        var otherOwners = await db.StockPostings.AsNoTracking()
            .Where(x => otherOwnerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var otherEvidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(x => otherTargetIds.Contains(x.MovementId))
            .ToArrayAsync(cancellationToken);
        var movementRegistry = new StockMovementRegistry();
        var inboundDependencies = await ProductionActiveEdgeResolver.Dependencies(db)
            .AsNoTracking().Where(x => x.ConsumerMovementId == inbound.Uid)
            .Select(x => x.ContributorMovementId).ToArrayAsync(cancellationToken);
        var ownerSequence = await db.StockPostings.AsNoTracking()
            .Where(x => x.Id == ownerId).Select(x => x.PostingSequence)
            .SingleAsync(cancellationToken);
        var laterSourceMovement = await (from movement in db.ProductionBalLotMovements.AsNoTracking()
            join posting in db.StockPostings.AsNoTracking()
                on movement.StockPostingId equals posting.Id
            where movement.ProductionBalLotId == outbound.ProductionBalLotId
                && movement.OriginalMovementId == null
                && posting.SealedAtUtc != null && posting.PostingSequence > ownerSequence
            select movement.Uid).AnyAsync(cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(x => ids.Contains(x.MovementId)).ToArrayAsync(cancellationToken);
        var current = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, ids, cancellationToken);
        if (source is null || target is null || sourceValuation is null || targetValuation is null
            || source.CompanyCode != context.CompanyCode || source.BranchCode != context.BranchCode
            || target.CompanyCode != context.CompanyCode || target.BranchCode != context.BranchCode
            || source.BaseQty != 0m || source.TotalCost != 0m || source.Qty != 0m
            || targetMovementIds.Length != otherTargetMovements.Length + 1
            || !targetMovementIds.Contains(inbound.Uid)
            || inboundDependencies.Length != 1 || inboundDependencies[0] != outbound.Uid
            || laterSourceMovement
            || fact.SourceBaseBefore != outbound.BaseQty || fact.SourceValueBefore != fact.TotalValue
            || fact.SourceBaseAfter != 0m || fact.SourceValueAfter != 0m
            || sourceValuation.Status != ProductionPoolValuationService.Verified
            || sourceValuation.TrackedBaseQty != 0m || sourceValuation.TrackedValue != 0m
            || targetValuation.Status != ProductionPoolValuationService.Verified
            || targetValuation.TrackedBaseQty != target.BaseQty
            || targetValuation.TrackedValue != target.TotalCost
            || evidence.Length != 2
            || evidence.Count(x => x.MovementId == outbound.Uid
                && x.ProductionBalLotId == source.Uid
                && x.Generation == sourceValuation.Generation
                && x.Status == ProductionPoolValuationService.Verified) != 1
            || evidence.Count(x => x.MovementId == inbound.Uid
                && x.ProductionBalLotId == target.Uid
                && x.Generation == targetValuation.Generation
                && x.Status == ProductionPoolValuationService.Verified) != 1
            || current[outbound.Uid] != expectedOut || current[inbound.Uid] != expectedIn
            || expectedOut.TotalCost != expectedIn.TotalCost
            || expectedOut.UnitCost != expectedIn.UnitCost)
            throw Stale("Work Order Transfer pair or terminal target pool changed after planning.");

        if (downstream is null)
        {
            var beforeMovements = otherTargetMovements.Where(x => x.StockPostingId is long id
                && otherOwners.TryGetValue(id, out var posting)
                && posting.PostingSequence < ownerSequence).ToArray();
            var beforeBase = beforeMovements.Sum(x => x.BaseQty);
            var beforeOriginalValue = StockLedgerPrecision.Money(beforeMovements.Sum(x => x.TotalCost));
            var otherBase = otherTargetMovements.Sum(x => x.BaseQty);
            var otherQty = otherTargetMovements.Sum(x => x.Qty);
            var otherEffectiveValue = StockLedgerPrecision.Money(otherTargetMovements.Sum(x =>
                otherTargetCosts[x.Uid].TotalCost));
            if (target.BaseQty != otherBase + inbound.BaseQty
                || target.Qty != otherQty + inbound.Qty
                || fact.TargetBaseBefore != beforeBase || fact.TargetValueBefore != beforeOriginalValue
                || fact.TargetBaseAfter != beforeBase + inbound.BaseQty
                || fact.TargetValueAfter != StockLedgerPrecision.Money(beforeOriginalValue + fact.TotalValue)
                || otherOwnerIds.Contains(0) || otherOwners.Count != otherOwnerIds.Length
                || otherEvidence.Length != otherTargetMovements.Length
                || otherTargetMovements.Any(x =>
                    x.ProductionBalLotId != target.Uid || x.OriginalMovementId is not null
                    || x.LedgerVersion != 2 || x.LedgerEpochId != context.Epoch.Id
                    || x.ValuationStatus != ProductionPoolValuationService.Verified
                    || x.BaseQty <= 0m || x.Qty <= 0m
                    || x.ItemCode != inbound.ItemCode || x.BaseUom != inbound.BaseUom
                    || x.Uom != target.Uom || x.ConversionFactorToBase != target.ConversionFactorToBase
                    || x.WorkOrderId != target.WorkOrderId
                    || x.RouteStepId != target.ProducingRouteStepId
                    || x.WorkOrderOperationId != target.WorkOrderOperationId
                    || movementRegistry.GetRequired(x.MovementType).Direction <= 0
                    || !otherOwners.TryGetValue(x.StockPostingId!.Value, out var otherOwner)
                    || otherOwner.CompanyCode != context.CompanyCode
                    || otherOwner.BranchCode != context.BranchCode
                    || otherOwner.LedgerEpochId != context.Epoch.Id
                    || otherOwner.SealedAtUtc is null
                    || otherOwner.PostingSequence == ownerSequence
                    || x.MovementDate != otherOwner.EffectiveAt
                    || otherEvidence.Count(e => e.MovementId == x.Uid
                        && e.ProductionBalLotId == target.Uid
                        && e.Generation == targetValuation.Generation
                        && e.Status == ProductionPoolValuationService.Verified) != 1)
                || target.AverageUnitCost != StockLedgerPrecision.Money(target.TotalCost / target.BaseQty)
                || target.TotalCost != StockLedgerPrecision.Money(otherEffectiveValue + expectedIn.TotalCost))
                throw Stale("Work Order Transfer pair or terminal target pool changed after planning.");

            var correctedUnit = StockLedgerPrecision.Money(correctedValue / outbound.BaseQty);
            var outResult = await ProductionCostRestatementWriter.AppendWithinRootAsync(
                context, expectedOut, correctedValue, correctedUnit, cancellationToken);
            var inResult = await ProductionCostRestatementWriter.AppendWithinRootAsync(
                context, expectedIn, correctedValue, correctedUnit, cancellationToken);
            if (outResult.Reversal is not null || outResult.Replacement is not null
                || inResult.Reversal is not null || inResult.Replacement is not null)
            {
                target.TotalCost = StockLedgerPrecision.Money(otherEffectiveValue + correctedValue);
                target.AverageUnitCost = StockLedgerPrecision.Money(target.TotalCost / target.BaseQty);
                targetValuation.TrackedValue = target.TotalCost;
                await db.SaveChangesAsync(cancellationToken);
            }
            return new(outResult, inResult, StockLedgerPrecision.Money(otherEffectiveValue + correctedValue));
        }

        if (otherTargetMovements.Length != 1 || otherTargetMovements[0].Uid != downstream.ConsumeMovementId
            || fact.TargetBaseBefore != 0m || fact.TargetValueBefore != 0m
            || fact.TargetBaseAfter != inbound.BaseQty || fact.TargetValueAfter != fact.TotalValue
            || downstream.ConsumeMovementId == downstream.ProduceMovementId
            || otherTargetMovements[0].MovementType != ProductionBalLotMovementTypes.Consume
            || otherTargetMovements[0].OriginalMovementId is not null
            || otherTargetMovements[0].BaseQty <= 0m || otherTargetMovements[0].BaseQty > inbound.BaseQty
            || target.BaseQty != inbound.BaseQty - otherTargetMovements[0].BaseQty
            || target.Qty != inbound.Qty - otherTargetMovements[0].Qty)
            throw Stale("Work Order Transfer downstream target pool changed after planning.");

        var consume2 = otherTargetMovements[0];
        var produce2 = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == downstream.ProduceMovementId, cancellationToken);
        var finished = downstream.ActiveFinishedGoodId is long fgId
            ? await db.ProductionBalLotMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == fgId, cancellationToken)
            : null;
        if (produce2 is null || produce2.MovementType != ProductionBalLotMovementTypes.Produce
            || produce2.OriginalMovementId is not null
            || produce2.ProductionOutputId != consume2.ProductionOutputId
            || produce2.WorkOrderId != target.WorkOrderId
            || produce2.BaseQty <= 0m
            || downstream.ActiveFinishedGoodId is not null && (finished is null
                || finished.MovementType != ProductionBalLotMovementTypes.FgReceiptOut
                || finished.ProductionBalLotId != produce2.ProductionBalLotId
                || finished.BaseQty > produce2.BaseQty))
            throw Stale("Work Order Transfer downstream output changed after planning.");

        var stage2 = await db.ProductionBalLots.SingleOrDefaultAsync(
            x => x.Uid == produce2.ProductionBalLotId, cancellationToken);
        var stage2Pool = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(
            x => x.ProductionBalLotId == produce2.ProductionBalLotId, cancellationToken);
        var stage2Costs = await ProductionEffectiveCostResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, [produce2.Uid], cancellationToken);
        var consume2Cost = otherTargetCosts[consume2.Uid];
        var produce2Cost = stage2Costs[produce2.Uid];
        var currentTargetPoolValue = StockLedgerPrecision.Money(expectedIn.TotalCost - consume2Cost.TotalCost);
        var expectedStage2Base = produce2.BaseQty - (finished?.BaseQty ?? 0m);
        var expectedStage2Value = StockLedgerPrecision.Money(
            produce2Cost.TotalCost - (finished is not null ? downstream.ActiveFinishedGoodValue : 0m));
        if (stage2 is null || stage2Pool is null
            || stage2.Uid == target.Uid || stage2.Uid == source.Uid
            || stage2Pool.Status != ProductionPoolValuationService.Verified
            || stage2Pool.TrackedBaseQty != stage2.BaseQty || stage2Pool.TrackedValue != stage2.TotalCost
            || target.TotalCost != currentTargetPoolValue
            || target.AverageUnitCost != (target.BaseQty > 0m ? StockLedgerPrecision.Money(target.TotalCost / target.BaseQty) : 0m)
            || stage2.BaseQty != expectedStage2Base || stage2.TotalCost != expectedStage2Value)
            throw Stale("Work Order Transfer downstream target or output pool changed after planning.");

        var correctedConsume2 = consume2.BaseQty == inbound.BaseQty ? correctedValue
            : StockLedgerPrecision.Money(correctedValue * consume2.BaseQty / inbound.BaseQty);
        var correctedTargetValue = StockLedgerPrecision.Money(correctedValue - correctedConsume2);
        var correctedProduce2 = StockLedgerPrecision.Money(
            produce2Cost.TotalCost + correctedConsume2 - consume2Cost.TotalCost);
        if (correctedTargetValue < 0m || correctedProduce2 < 0m
            || target.BaseQty == 0m && correctedTargetValue != 0m)
            throw Invalid("Work Order Transfer downstream correction would leave a negative or stranded pool value.");

        var correctedTransferUnit = StockLedgerPrecision.Money(correctedValue / outbound.BaseQty);
        var downstreamOut = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expectedOut, correctedValue, correctedTransferUnit, cancellationToken);
        var downstreamIn = await ProductionCostRestatementWriter.AppendWithinRootAsync(
            context, expectedIn, correctedValue, correctedTransferUnit, cancellationToken);
        await ProductionCostRestatementWriter.AppendWithinRootAsync(context, consume2Cost,
            correctedConsume2, StockLedgerPrecision.Money(correctedConsume2 / consume2.BaseQty), cancellationToken);
        await ProductionCostRestatementWriter.AppendWithinRootAsync(context, produce2Cost,
            correctedProduce2, StockLedgerPrecision.Money(correctedProduce2 / produce2.BaseQty), cancellationToken);
        target.TotalCost = correctedTargetValue;
        target.AverageUnitCost = target.BaseQty > 0m
            ? StockLedgerPrecision.Money(correctedTargetValue / target.BaseQty) : 0m;
        targetValuation.TrackedValue = correctedTargetValue;
        if (finished is null)
        {
            stage2.TotalCost = correctedProduce2;
            stage2.AverageUnitCost = StockLedgerPrecision.Money(correctedProduce2 / produce2.BaseQty);
            stage2Pool.TrackedValue = correctedProduce2;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(downstreamOut, downstreamIn, correctedTargetValue);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument, message,
            Retryable: true));
}
