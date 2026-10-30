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
    private sealed record WacFinishedGoodCostPlan(
        int BatchId, long MovementId, int HistoryId, long ReceiptFactId,
        string ItemCode, int PileId, decimal Quantity, decimal CurrentValue,
        decimal ProjectedValue, decimal ProjectedIssueValue,
        decimal ProjectedProduceValue, ProductionEffectiveCost CurrentCost,
        string EvidenceHash);

    // Only one isolated output and its terminal FG receipt are admitted. This
    // pre-lock projection is rebuilt under the branch lock before any write.
    private static async Task<WacFinishedGoodCostPlan> PlanWacFinishedGoodCostAsync(
        AppDbContext db, string company, string branch, ProductionLeafReplayPlan plan,
        StockReplayWacStep issueStep, long? removingReceiptOwnerId,
        int? insertingReceiptBatchId, CancellationToken ct)
    {
        var output = plan.Output!;
        var fg = output.ActiveFinishedGood!;
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.Uid == plan.MovementId || m.Uid == output.ConsumeMovementId
                || m.Uid == output.ProduceMovementId || m.Uid == fg.MovementId)
            .ToDictionaryAsync(m => m.Uid, ct);
        if (movements.Count != 4 || issueStep.SignedQuantity != -plan.Quantity
            || issueStep.SignedValue > 0m)
            throw ProductionReplayError("The finished-good issue projection is incomplete.");
        var issue = movements[plan.MovementId];
        var consume = movements[output.ConsumeMovementId!.Value];
        var produce = movements[output.ProduceMovementId!.Value];
        var finished = movements[fg.MovementId];
        var costs = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch,
            [consume.Uid, produce.Uid], ct);
        var desiredIssue = -issueStep.SignedValue;
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
                db, company, branch, fg.BatchId, fg.EffectiveAt, ct);
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
        if (state is null || state.CostMethod != StockCostMethods.MovingAverage
            || state.OnHandBaseQty != fact.BaseQty || state.InventoryValue != fg.Value
            || state.AverageUnitCost != StockLedgerPrecision.Money(fg.Value / fact.BaseQty)
            || state.CurrentUnitCost != state.AverageUnitCost
            || state.LastValuationFactId != (prior?.DeltaFactId ?? (cleared is { Count: > 0 } ? cleared.LastFactId : receipt.Id))
            || state.LastPostingSequence != stateSequence
            || piles.Length != 1 || piles[0].Id != fact.DestinationBalanceId || piles[0].StdQty != fact.BaseQty)
            throw ProductionReplayError("Finished-good stock has later activity requiring full replay.");
        var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            owner.PostingSequence, $"H{history.Id:D10}", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch,
            finished.ItemCode, key, ct);
        if (tail.CurrentQuantity != state.OnHandBaseQty || tail.CurrentValue != state.InventoryValue
            || tail.LaterEvents.Count != (prior is null ? 0 : 1)
            || prior is not null && (tail.LaterEvents[0].EventId != long.MaxValue - receipt.Id
                || tail.LaterEvents[0].OldSignedValue != fg.Value - fact.TotalValue))
            throw ProductionReplayError("Finished-good stock has a later WAC event requiring full replay.");

        var hash = StockPostingFingerprint.Create(new
        {
            fg.BatchId, fg.MovementId, desiredIssue, removingReceiptOwnerId, insertingReceiptBatchId
        }, new
        {
            fg.EffectiveCost, CurrentProduce = costs[produce.Uid], CurrentConsume = costs[consume.Uid],
            desiredProduce, desiredFg, ReceiptId = receipt.Id, PriorDeltaId = prior?.DeltaFactId,
            StateId = state.Id, state.OnHandBaseQty, state.InventoryValue, state.LastValuationFactId,
            state.LastPostingSequence, StateVersion = Convert.ToHexString(state.RowVersion ?? []),
            PileId = piles[0].Id, piles[0].StdQty
        }).SourceSnapshotHash;
        return new(fg.BatchId, fg.MovementId, history.Id, receipt.Id, finished.ItemCode,
            piles[0].Id, fact.BaseQty, fg.Value, desiredFg, desiredIssue, desiredProduce,
            fg.EffectiveCost, hash);
    }

    private static async Task ApplyWacFinishedGoodCostAsync(StockPostingContext context,
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
        var permit = new StockReplayWacWritePermit(context, fg.ItemCode, fg.EvidenceHash,
            fg.Quantity, fg.CurrentValue, fg.Quantity, fg.ProjectedValue, fg.PileId, fg.Quantity);
        await StockReplayWacCostStateWriter.ApplyAsync(context, permit, ct);
        var finalCost = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, [fg.MovementId, output.ProduceMovementId.Value], ct));
        if (finalCost[fg.MovementId].TotalCost != fg.ProjectedValue
            || finalCost[output.ProduceMovementId.Value].TotalCost != fg.ProjectedProduceValue
            || pool.TrackedValue != remaining)
            throw ProductionReplayError("Finished-good and Production values failed replay reconciliation.");
    }
}
