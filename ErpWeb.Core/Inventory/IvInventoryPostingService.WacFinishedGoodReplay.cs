using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using System.Globalization;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record WacFinishedGoodTailPlan(
        StockReplayWacEvent? PriorDeltaEvent,
        IReadOnlyList<StockReplayWacEvent> LaterEvents,
        StockReplayWacResult Projection);

    private sealed record WacFinishedGoodFifoTailPlan(
        StockReplayFifoActiveLayerSnapshot Snapshot,
        StockReplayFifoTailProjection Projection,
        long LayerId);

    private sealed record WacFinishedGoodCostPlan(
        string CostMethod, int BatchId, long MovementId, int HistoryId, long ReceiptFactId,
        string ItemCode, int PileId, decimal Quantity, decimal CurrentValue,
        decimal ProjectedValue, decimal ProjectedIssueValue,
        decimal ProjectedProduceValue, ProductionEffectiveCost CurrentCost,
        string EvidenceHash,
        decimal CurrentItemQuantity, decimal CurrentItemValue,
        decimal FinalItemQuantity, decimal FinalItemValue,
        WacFinishedGoodTailPlan? WacTail = null,
        WacFinishedGoodFifoTailPlan? FifoTail = null)
    {
        public bool Equals(WacFinishedGoodCostPlan? other) =>
            other is not null
            && CostMethod == other.CostMethod && BatchId == other.BatchId
            && MovementId == other.MovementId && HistoryId == other.HistoryId
            && ReceiptFactId == other.ReceiptFactId && ItemCode == other.ItemCode
            && PileId == other.PileId && Quantity == other.Quantity
            && CurrentValue == other.CurrentValue && ProjectedValue == other.ProjectedValue
            && ProjectedIssueValue == other.ProjectedIssueValue
            && ProjectedProduceValue == other.ProjectedProduceValue
            && CurrentCost == other.CurrentCost && EvidenceHash == other.EvidenceHash
            && CurrentItemQuantity == other.CurrentItemQuantity
            && CurrentItemValue == other.CurrentItemValue
            && FinalItemQuantity == other.FinalItemQuantity
            && FinalItemValue == other.FinalItemValue;

        public override int GetHashCode() =>
            HashCode.Combine(CostMethod, BatchId, MovementId, HistoryId, ReceiptFactId,
                ProjectedValue, CurrentCost, EvidenceHash);
    }

    private static Task<WacFinishedGoodCostPlan> PlanWacFinishedGoodCostAsync(
        AppDbContext db, string company, string branch, ProductionLeafReplayPlan plan,
        StockReplayWacStep issueStep, long? removingReceiptOwnerId,
        int? insertingReceiptBatchId, CancellationToken ct)
    {
        if (issueStep.SignedQuantity != -plan.Quantity || issueStep.SignedValue > 0m)
            throw ProductionReplayError("The finished-good issue projection is incomplete.");
        return PlanWacFinishedGoodCostAsync(db, company, branch, plan, -issueStep.SignedQuantity,
            -issueStep.SignedValue, StockCostMethods.MovingAverage, removingReceiptOwnerId,
            insertingReceiptBatchId, ct);
    }

    // Only one isolated output and its terminal FG receipt are admitted. This
    // pre-lock projection is rebuilt under the branch lock before any write.
    private static async Task<WacFinishedGoodCostPlan> PlanWacFinishedGoodCostAsync(
        AppDbContext db, string company, string branch, ProductionLeafReplayPlan plan,
        decimal projectedIssueQuantity, decimal projectedIssueValue, string costMethod,
        long? removingReceiptOwnerId, int? insertingReceiptBatchId, CancellationToken ct)
    {
        var output = plan.Output!;
        var fg = output.ActiveFinishedGood!;
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.Uid == plan.MovementId || m.Uid == output.ConsumeMovementId
                || m.Uid == output.ProduceMovementId || m.Uid == fg.MovementId)
            .ToDictionaryAsync(m => m.Uid, ct);
        if (movements.Count != 4 || projectedIssueQuantity != plan.Quantity
            || projectedIssueValue < 0m
            || costMethod is not (StockCostMethods.MovingAverage or StockCostMethods.Fifo))
            throw ProductionReplayError("The finished-good issue projection is incomplete.");
        var issue = movements[plan.MovementId];
        var consume = movements[output.ConsumeMovementId!.Value];
        var produce = movements[output.ProduceMovementId!.Value];
        var finished = movements[fg.MovementId];
        var costs = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch,
            [consume.Uid, produce.Uid], ct);
        var desiredIssue = projectedIssueValue;
        var desiredConsume = consume.BaseQty == issue.BaseQty ? desiredIssue
            : StockLedgerPrecision.Money(desiredIssue * consume.BaseQty / issue.BaseQty);
        var desiredProduce = StockLedgerPrecision.Money(costs[produce.Uid].TotalCost
            + desiredConsume - costs[consume.Uid].TotalCost);
        var fact = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .SingleAsync(f => f.BatchId == fg.BatchId && f.ProductionMovementId == fg.MovementId
                && f.ReversesFactId == null, ct);
        var desiredFg = FinishedGoodReceiptMath.AllocateValue(produce.BaseQty, desiredProduce,
            [(fact.SourceId, fact.BaseQty)])[fact.SourceId];
        var prior = fg.EffectiveCost.RestatementFactId is null ? null
            : await ProductionFinishedGoodCorrectedRollback.TryPrepareAsync(
                db, company, branch, fg.BatchId, fg.EffectiveAt, ct,
                allowDownstreamActivity: true);
        var produceRestatements = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => f.ProductionBalLotMovementId == produce.Uid).ToArrayAsync(ct);
        var cleared = prior is null && fg.EffectiveCost.RestatementFactId is null
            ? await ProductionFinishedGoodCorrectionHistory.ReadAsync(db, company, branch,
                fg.MovementId, null, null, ct) : null;
        var matchesClearedOutput = cleared is { Count: > 0 }
            && await ProductionFinishedGoodCorrectionHistory.MatchesOutputAsync(db, fg.MovementId, produce.Uid, ct)
            && (costs[produce.Uid].RestatementFactId is null && costs[produce.Uid].TotalCost == produce.TotalCost
                || removingReceiptOwnerId is long ownerBeingRemoved
                    && produceRestatements.Any(r => r.Id == costs[produce.Uid].RestatementFactId
                        && r.ReplayStockPostingId == ownerBeingRemoved)
                    && desiredProduce == produce.TotalCost);
        var removesOriginalSourceCorrection = removingReceiptOwnerId is long removingId
            && produceRestatements.Length == 1
            && produceRestatements[0].Id == costs[produce.Uid].RestatementFactId
            && produceRestatements[0].ReplayStockPostingId == removingId
            && desiredProduce == produce.TotalCost;
        var restoresSourceAtFinishedGoodPosting = false;
        if (prior is not null && insertingReceiptBatchId is int insertingBatchId
            && removingReceiptOwnerId is null && desiredFg == fact.TotalValue)
        {
            var correctionRoot = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
                p.Id == prior.CorrectionRootId, ct);
            var originalSource = correctionRoot?.ReversesPostingId is long sourceId
                ? produceRestatements.SingleOrDefault(r => r.ReplayStockPostingId == sourceId
                    && r.ReversesRestatementFactId == null) : null;
            restoresSourceAtFinishedGoodPosting = correctionRoot is not null
                && correctionRoot.PostingRole == "REVERSAL"
                && correctionRoot.SourceDocumentId == insertingBatchId.ToString(CultureInfo.InvariantCulture)
                && originalSource is not null && desiredProduce == originalSource.CorrectedEffectiveTotalCost
                && await ProductionFinishedGoodCorrectionHistory.MatchesOutputAsync(db, fg.MovementId, produce.Uid, ct);
        }
        if (desiredProduce < 0m || desiredFg < 0m || desiredFg > desiredProduce
            || string.Equals(issue.ItemCode, finished.ItemCode, StringComparison.OrdinalIgnoreCase)
            || prior is null && (desiredFg == fg.Value
                || produceRestatements.Length != 0 && !removesOriginalSourceCorrection && !matchesClearedOutput)
            || prior is not null && (desiredFg != fact.TotalValue
                || desiredProduce != produce.TotalCost && !restoresSourceAtFinishedGoodPosting))
            throw ProductionReplayError("This finished-good value change needs a wider replay sequence.");

        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == fact.InventoryHistoryId, ct);
        var receipt = await db.StockValuationFacts.AsNoTracking().SingleAsync(v =>
            v.InventoryHistoryId == history.Id && v.StockPostingId == fact.StockPostingId
            && v.ReversesValuationFactId == null && v.OriginalValuationFactId == null, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == fact.StockPostingId, ct);
        var stateSequence = prior is null ? cleared is { Count: > 0 } ? cleared.LastSequence : owner.PostingSequence
            : await db.StockValuationFacts.AsNoTracking().Where(v => v.Id == prior.DeltaFactId)
                .Select(v => v.StockPosting!.PostingSequence).SingleAsync(ct);
        var state = await db.StockCostStates.AsNoTracking().SingleOrDefaultAsync(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.ItemCode == finished.ItemCode, ct);
        var piles = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == finished.ItemCode).ToArrayAsync(ct);
        if (state is null || state.CostMethod != costMethod
            || piles.Length != 1 || piles[0].Id != fact.DestinationBalanceId
            || piles[0].StdQty != state.OnHandBaseQty
            || state.OnHandBaseQty < 0m || state.InventoryValue < 0m)
            throw ProductionReplayError("Finished-good stock has incompatible pile or cost state for replay.");

        var currentItemQty = state.OnHandBaseQty;
        var currentItemValue = state.InventoryValue;
        var finalItemQty = state.OnHandBaseQty;
        var finalItemValue = desiredFg;
        WacFinishedGoodTailPlan? wacTailPlan = null;
        WacFinishedGoodFifoTailPlan? fifoTailPlan = null;
        if (costMethod == StockCostMethods.MovingAverage)
        {
            var fgKey0 = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{history.Id:D10}", 0);
            var fgKey1 = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{history.Id:D10}", 1);
            var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch,
                finished.ItemCode, fgKey0, ct);
            var priorDelta = prior is null ? null
                : tail.LaterEvents.SingleOrDefault(e => e.EventId == long.MaxValue - receipt.Id
                    && e.Key == fgKey1 && e.OldSignedValue == fg.Value - fact.TotalValue);
            var laterFgEvents = tail.LaterEvents.Where(e => e.Key.CompareTo(fgKey1) > 0).ToArray();
            if (laterFgEvents.Length == 0)
            {
                if (state.OnHandBaseQty != fact.BaseQty || state.InventoryValue != fg.Value
                    || state.AverageUnitCost != StockLedgerPrecision.Money(fg.Value / fact.BaseQty)
                    || state.CurrentUnitCost != state.AverageUnitCost
                    || state.LastValuationFactId != (prior?.DeltaFactId ?? (cleared is { Count: > 0 } ? cleared.LastFactId : receipt.Id))
                    || state.LastPostingSequence != stateSequence
                    || tail.CurrentQuantity != state.OnHandBaseQty || tail.CurrentValue != state.InventoryValue
                    || tail.LaterEvents.Count != (prior is null ? 0 : 1)
                    || prior is not null && priorDelta is null)
                    throw ProductionReplayError("Finished-good stock has a later WAC event requiring full replay.");
            }
            else
            {
                if (tail.CurrentQuantity != state.OnHandBaseQty || tail.CurrentValue != state.InventoryValue
                    || tail.LaterEvents.Count != laterFgEvents.Length + (priorDelta is null ? 0 : 1)
                    || prior is not null && priorDelta is null
                    || laterFgEvents.Any(e => e.EventId <= 0 || e.EventId > int.MaxValue
                        || e.Key.EffectiveAt.Year != fg.EffectiveAt.Year
                        || e.Key.EffectiveAt.Month != fg.EffectiveAt.Month)
                    || await HasLaterStandardCountAsync(db, company, branch, finished.ItemCode,
                        owner.EffectiveAt, ct)
                    || await HasUnresolvedReversedSaleValueAsync(db, company, branch,
                        [finished.ItemCode], owner.EffectiveAt, ct)
                    || await HasReversedSalesAfterAsync(db, company, branch,
                        [finished.ItemCode], owner.EffectiveAt, ct))
                    throw ProductionReplayError("Finished-good stock has a later WAC event requiring wider replay.");
                await AssertSingleActivePolicyForFinishedGoodAsync(db, company, branch,
                    owner.EffectiveAt.Date, laterFgEvents.Max(e => e.Key.EffectiveAt).Date,
                    StockCostMethods.MovingAverage, ct);
                var fgHistoryIds = laterFgEvents.Select(e => checked((int)e.EventId)).ToArray();
                var fgHistories = await db.IvTrxHistories.AsNoTracking()
                    .Where(h => fgHistoryIds.Contains(h.Id))
                    .Select(h => new { h.Id, h.TrxType })
                    .ToArrayAsync(ct);
                if (fgHistories.Length != fgHistoryIds.Length
                    || fgHistories.Any(h => h.TrxType is not (IvTrxTypes.MiscellaneousIssue
                        or IvTrxTypes.Scrap or IvTrxTypes.SalesOut
                        or IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive))
                    || await CheckSimpleWacTailAsync(db, laterFgEvents, fg.EffectiveAt, ct,
                        allowBoundedSales: true, allowBoundedProduction: false) is not null)
                    throw ProductionReplayError("Finished-good stock has an unsupported later Inventory or Sales movement.");

                StockReplayWacResult projectedWac;
                if (priorDelta is null)
                {
                    var fgDeltaEvent = new StockReplayWacEvent(long.MaxValue - receipt.Id, fgKey1,
                        0m, 0m, 0m, FixedSignedValue: desiredFg - fact.TotalValue);
                    projectedWac = StockReplayWacSimulator.SimulateInsertion(tail.CurrentQuantity,
                        tail.CurrentValue, fgDeltaEvent, laterFgEvents);
                }
                else
                {
                    var removed = StockReplayWacSimulator.SimulateRemoval(tail.CurrentQuantity,
                        tail.CurrentValue, priorDelta, laterFgEvents);
                    var stepBefore = new StockReplayWacStep(priorDelta.EventId,
                        removed.HistoricalQuantityBeforeRemovedEvent,
                        removed.HistoricalValueBeforeRemovedEvent, 0m, 0m,
                        removed.HistoricalQuantityBeforeRemovedEvent,
                        removed.HistoricalValueBeforeRemovedEvent);
                    projectedWac = new StockReplayWacResult(
                        removed.HistoricalQuantityBeforeRemovedEvent,
                        removed.HistoricalValueBeforeRemovedEvent,
                        stepBefore, removed.LaterSteps,
                        removed.FinalQuantity, removed.FinalValue);
                }
                wacTailPlan = new WacFinishedGoodTailPlan(priorDelta, laterFgEvents, projectedWac);
                finalItemQty = projectedWac.FinalQuantity;
                finalItemValue = projectedWac.FinalValue;
            }
        }
        else
        {
            var fifo = await StockReplayFifoActiveLayerReader.ReadAsync(db, company, branch,
                finished.ItemCode, ct);
            var fgLayer = fifo.Layers.SingleOrDefault(l => l.OriginFactId == receipt.Id);
            if (fifo.CurrentQuantity != state.OnHandBaseQty || fifo.CurrentValue != state.InventoryValue
                || fgLayer is null || fgLayer.ReceiptKey is not { } fgLayerKey
                || fgLayer.OriginalQuantity != fact.BaseQty || fgLayer.OriginalValue != fact.TotalValue
                || fgLayer.AccumulatedAdjustment != StockLedgerPrecision.Money(fg.Value - fact.TotalValue)
                || fgLayer.CurrentUnitCost != StockLedgerPrecision.Money(fg.Value / fact.BaseQty))
                throw ProductionReplayError("Finished-good FIFO layer does not reconcile before replay.");
            if (fifo.ActiveConsumptions.Count == 0 && fifo.Layers.Count == 1)
            {
                if (state.OnHandBaseQty != fact.BaseQty || fgLayer.RemainingQuantity != fact.BaseQty
                    || fgLayer.RemainingValue != fg.Value || fgLayer.Status != StockFifoLayerStatuses.Open)
                    throw ProductionReplayError("Finished-good FIFO layer does not reconcile before replay.");
            }
            else
            {
                if (await HasLaterStandardCountAsync(db, company, branch, finished.ItemCode,
                        owner.EffectiveAt, ct)
                    || await HasUnresolvedReversedSaleValueAsync(db, company, branch,
                        [finished.ItemCode], owner.EffectiveAt, ct)
                    || await HasReversedSalesAfterAsync(db, company, branch,
                        [finished.ItemCode], owner.EffectiveAt, ct)
                    || fifo.Layers.Any(l => l.ReceiptKey is { } rk && rk.CompareTo(fgLayerKey) > 0
                        && (l.SalesReturn is not null || l.ProductionReturn is not null)))
                    throw ProductionReplayError("Finished-good stock has a later FIFO event requiring wider replay.");
                var maxFifoDate = fifo.Layers.Where(l => l.ReceiptKey is not null)
                    .Select(l => l.ReceiptKey!.Value.EffectiveAt.Date)
                    .Concat(fifo.ActiveConsumptions.Select(c => c.IssueKey.EffectiveAt.Date))
                    .Append(owner.EffectiveAt.Date).Max();
                await AssertSingleActivePolicyForFinishedGoodAsync(db, company, branch,
                    owner.EffectiveAt.Date, maxFifoDate, StockCostMethods.Fifo, ct);
                var fgProjection = StockReplayFifoTailProjector.RevalueLayer(fifo, fgLayer.LayerId, desiredFg);
                var fgCorrectionIds = (await StockReplayWacReceiptRestatementWriter
                    .ReadVerifiedFifoAdjustmentsAsync(db, receipt, ct)).Select(f => f.Id).ToArray();
                if (fgProjection.Issues.Any(i => !i.DepletesPool)
                    || await CheckSimpleFifoInventoryTailAsync(db, finished.ItemCode, fifo, fgProjection,
                        fgLayerKey, ct, allowBoundedSales: true, allowEmpty: true,
                        allowBoundedProduction: false,
                        correctionValueFactIds: fgCorrectionIds) is not null)
                    throw ProductionReplayError("Finished-good FIFO stock has an unsupported later Inventory or Sales movement.");
                fifoTailPlan = new WacFinishedGoodFifoTailPlan(fifo, fgProjection, fgLayer.LayerId);
                finalItemQty = fgProjection.FinalQuantity;
                finalItemValue = fgProjection.FinalValue;
            }
        }

        var hash = StockPostingFingerprint.Create(new
        {
            costMethod, fg.BatchId, fg.MovementId, desiredIssue, removingReceiptOwnerId, insertingReceiptBatchId
        }, new
        {
            fg.EffectiveCost, CurrentProduce = costs[produce.Uid], CurrentConsume = costs[consume.Uid],
            desiredProduce, desiredFg, ReceiptId = receipt.Id, PriorDeltaId = prior?.DeltaFactId,
            StateId = state.Id, state.OnHandBaseQty, state.InventoryValue, state.LastValuationFactId,
            state.LastPostingSequence, StateVersion = Convert.ToHexString(state.RowVersion ?? []),
            PileId = piles[0].Id, piles[0].StdQty,
            CurrentItemQuantity = currentItemQty, CurrentItemValue = currentItemValue,
            FinalItemQuantity = finalItemQty, FinalItemValue = finalItemValue,
            WacTail = wacTailPlan,
            FifoTail = fifoTailPlan is null ? null : new { fifoTailPlan.Snapshot.SnapshotHash, fifoTailPlan.Projection }
        }).SourceSnapshotHash;
        return new(costMethod, fg.BatchId, fg.MovementId, history.Id, receipt.Id, finished.ItemCode,
            piles[0].Id, fact.BaseQty, fg.Value, desiredFg, desiredIssue, desiredProduce,
            fg.EffectiveCost, hash, currentItemQty, currentItemValue, finalItemQty, finalItemValue,
            wacTailPlan, fifoTailPlan);
    }

    private static async Task AssertSingleActivePolicyForFinishedGoodAsync(
        AppDbContext db, string company, string branch,
        DateTime fromDate, DateTime toDate, string costMethod, CancellationToken ct)
    {
        var policies = await db.StockCostPolicyRevisions.AsNoTracking()
            .Where(p => p.CompanyCode == company && p.BranchCode == branch
                && p.Status == StockCostPolicyStatuses.Active
                && p.EffectiveFrom <= fromDate && (p.EffectiveTo == null || p.EffectiveTo > fromDate))
            .Select(p => new { p.Id, p.CostMethod, p.EffectiveTo })
            .ToArrayAsync(ct);
        if (policies.Length != 1 || policies[0].CostMethod != costMethod
            || policies[0].EffectiveTo is DateTime end && end.Date <= toDate
            || await db.StockCostPolicyRevisions.AsNoTracking().AnyAsync(p =>
                p.CompanyCode == company && p.BranchCode == branch
                && p.Status == StockCostPolicyStatuses.Active && p.Id != policies[0].Id
                && p.EffectiveFrom <= toDate && (p.EffectiveTo == null || p.EffectiveTo > fromDate), ct))
            throw ProductionReplayError("Finished-good stock spans multiple cost policies.");
    }

    private static async Task<IReadOnlyList<int>> ApplyWacFinishedGoodCostAsync(StockPostingContext context,
        ProductionLeafReplayPlan plan, WacFinishedGoodCostPlan fg, CancellationToken ct)
    {
        var db = context.Db;
        var output = plan.Output!;
        var issue = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [plan.MovementId], ct))[plan.MovementId];
        if (issue.BaseQty != plan.Quantity || issue.TotalCost != fg.ProjectedIssueValue)
            throw ProductionReplayError("The finished-good source changed after replay planning.");
        await ProductionOutputIssueCostCorrectionWriter.AppendWithinRootAsync(context,
            output.IssueMovementId!.Value, output.ConsumeMovementId!.Value,
            output.ProduceMovementId!.Value, false, plan.EffectiveCost, ct,
            fg.MovementId, fg.CurrentValue);
        var delta = await ProductionFinishedGoodWacReceiptCorrection.AppendWithinRootAsync(context,
            fg.HistoryId, fg.ReceiptFactId, fg.ProjectedValue, 1000001, 1000002, ct);
        if (delta is null || fg.ProjectedValue == fg.CurrentValue)
            throw ProductionReplayError("The finished-good Inventory correction did not match its plan.");
        var current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, [fg.MovementId], ct))[fg.MovementId];
        if (current != fg.CurrentCost)
            throw ProductionReplayError("The finished-good Production cost changed after planning.");
        await ProductionCostRestatementWriter.AppendWithinRootAsync(context, current,
            fg.ProjectedValue, StockLedgerPrecision.Money(fg.ProjectedValue / fg.Quantity), ct);
        var movement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(m => m.Uid == fg.MovementId, ct);
        var lot = await db.ProductionBalLots.SingleAsync(l => l.Uid == movement.ProductionBalLotId, ct);
        var pool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == lot.Uid, ct);
        var remaining = StockLedgerPrecision.Money(fg.ProjectedProduceValue - fg.ProjectedValue);
        if (remaining < 0m || lot.BaseQty == 0m && remaining != 0m
            || pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost)
            throw ProductionReplayError("The finished-good source pool no longer matches the replay plan.");
        lot.TotalCost = remaining;
        lot.AverageUnitCost = lot.BaseQty == 0m ? 0m : StockLedgerPrecision.Money(remaining / lot.BaseQty);
        pool.TrackedValue = remaining;
        await db.SaveChangesAsync(ct);
        IReadOnlyList<int> repricedHistoryIds = [];
        if (fg.CostMethod == StockCostMethods.MovingAverage)
        {
            var permit = new StockReplayWacWritePermit(context, fg.ItemCode, fg.EvidenceHash,
                fg.CurrentItemQuantity, fg.CurrentItemValue,
                fg.FinalItemQuantity, fg.FinalItemValue,
                fg.PileId, fg.FinalItemQuantity);
            if (fg.WacTail is { } wacTail)
            {
                var tailResult = await StockReplayWacTailRepricingWriter.AppendValueDeltaTailAsync(
                    context, permit, wacTail.LaterEvents, wacTail.Projection.LaterSteps,
                    1000010, ct);
                repricedHistoryIds = tailResult.RepricedHistoryIds;
            }
            await StockReplayWacCostStateWriter.ApplyAsync(context, permit, ct);
        }
        else if (fg.FifoTail is { } fifoTail)
        {
            var permit = new StockReplayFifoWritePermit(context, fg.ItemCode, fifoTail.Snapshot,
                fifoTail.Projection, fg.EvidenceHash, fg.PileId, fifoTail.Projection.FinalQuantity);
            var ids = new List<int>();
            var issueIndex = 0;
            foreach (var fgIssue in fifoTail.Projection.Issues.Where(i => !i.IsCandidate))
            {
                var reversalLine = checked(1000010 + issueIndex * 2);
                var replacementLine = checked(reversalLine + 1);
                issueIndex++;
                var facts = await StockReplayFifoIssueRepricingWriter.AppendRevaluedLayerTailAsync(
                    context, fg.ItemCode, fifoTail.Snapshot, fifoTail.Projection, fg.EvidenceHash,
                    permit, fgIssue.Key, reversalLine, replacementLine, ct, allowSales: true);
                if (facts.Count > 0 && facts[0].InventoryHistoryId is int id)
                    ids.Add(id);
            }
            await StockReplayFifoStateWriter.ApplyFinishedGoodRevaluationAsync(context, fg.ItemCode,
                fifoTail.Snapshot, fifoTail.Projection, fg.EvidenceHash, fg.PileId,
                fifoTail.Projection.FinalQuantity, permit, fg.ReceiptFactId, ct);
            repricedHistoryIds = ids;
        }
        else
        {
            await ApplyFifoFinishedGoodCostStateAsync(context, fg, ct);
        }
        var finalCost = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, [fg.MovementId, output.ProduceMovementId.Value], ct));
        if (finalCost[fg.MovementId].TotalCost != fg.ProjectedValue
            || finalCost[output.ProduceMovementId.Value].TotalCost != fg.ProjectedProduceValue
            || pool.TrackedValue != remaining)
            throw ProductionReplayError("Finished-good and Production values failed replay reconciliation.");
        return repricedHistoryIds;
    }

    private static async Task ApplyFifoFinishedGoodCostStateAsync(
        StockPostingContext context, WacFinishedGoodCostPlan fg, CancellationToken ct)
    {
        var db = context.Db;
        await db.SaveChangesAsync(ct);
        var layer = await db.StockFifoLayers.SingleOrDefaultAsync(l =>
            l.CompanyCode == context.CompanyCode && l.BranchCode == context.BranchCode
            && l.ItemCode == fg.ItemCode && l.OriginValuationFactId == fg.ReceiptFactId, ct);
        var state = await db.StockCostStates.SingleOrDefaultAsync(s =>
            s.CompanyCode == context.CompanyCode && s.BranchCode == context.BranchCode
            && s.ItemCode == fg.ItemCode, ct);
        var piles = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == context.CompanyCode
            && b.BranchCode == context.BranchCode && b.ICode == fg.ItemCode)
            .Select(b => new { b.Id, b.StdQty }).ToArrayAsync(ct);
        if (layer is null || state is null || state.CostMethod != StockCostMethods.Fifo
            || state.OnHandBaseQty != fg.Quantity || state.InventoryValue != fg.CurrentValue
            || piles.Length != 1 || piles[0].Id != fg.PileId || piles[0].StdQty != fg.Quantity
            || layer.OriginalQty != fg.Quantity || layer.RemainingQty != fg.Quantity
            || layer.RemainingValue != fg.CurrentValue
            || layer.Status != StockFifoLayerStatuses.Open
            || await db.StockFifoLayerConsumptions.AsNoTracking().AnyAsync(c =>
                c.FifoLayerId == layer.Id
                && !db.StockFifoLayerConsumptions.Any(r => r.ReversesConsumptionId == c.Id), ct))
            throw ProductionReplayError("Finished-good FIFO state changed after replay planning.");
        var rootFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
                f.StockPostingId == context.Posting.Id && f.ItemCode == fg.ItemCode)
            .ToArrayAsync(ct);
        if (rootFacts.Length == 0 || rootFacts.Any(f => f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode || f.LedgerEpochId != context.Epoch.Id
                || f.CostMethod != StockCostMethods.Fifo || f.BaseQty != 0m || f.CostAmount < 0m)
            || StockLedgerPrecision.Money(rootFacts.Sum(f => f.SignedValue))
                != StockLedgerPrecision.Money(fg.ProjectedValue - fg.CurrentValue))
            throw ProductionReplayError("Finished-good FIFO valuation facts do not reconcile.");
        var projectedUnit = StockLedgerPrecision.Money(fg.ProjectedValue / fg.Quantity);
        layer.AccumulatedAdjustment = StockLedgerPrecision.Money(fg.ProjectedValue - layer.OriginalValue);
        layer.RemainingValue = fg.ProjectedValue;
        layer.CurrentUnitCost = projectedUnit;
        state.InventoryValue = fg.ProjectedValue;
        state.AverageUnitCost = projectedUnit;
        state.CurrentUnitCost = projectedUnit;
        state.LastValuationFactId = rootFacts.OrderByDescending(f => f.PostingLineNo)
            .ThenByDescending(f => f.SplitOrdinal).First().Id;
        state.LastPostingSequence = context.Posting.PostingSequence;
        await db.SaveChangesAsync(ct);
    }
}
