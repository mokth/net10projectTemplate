using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Applies the final FIFO layer and item state after every projected issue
/// generation has been appended inside the same replay root transaction.
/// </summary>
public static class StockReplayFifoStateWriter
{
    public static async Task ApplyMiscReceiptRemovalAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        var removed = plan.Projection.CandidateReceipt;
        if (removed is null || !plan.Projection.IsRemoval
            || plan.PhysicalPlan.RemovedActiveEvent.SignedDelta <= 0m
            || removed.ReceiptKey != plan.PhysicalPlan.RemovedActiveEvent.Key
            || context.Posting.SourceDocumentType is not (
                IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive))
            throw Invalid("FIFO receipt removal state needs its original Inventory receipt layer.");
        var first = plan.PhysicalPlan.RemovedActiveEvent.Key.EffectiveAt.Date;
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, first, plan.PhysicalPlan.LaterEvents
                .Select(e => e.Key.EffectiveAt.Date).Append(first).Max(),
            cancellationToken);
        await ApplyCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, plan.PhysicalPlan.BalLocId,
            plan.PhysicalPlan.Projection.FinalQuantity, "REMOVE", permit,
            cancellationToken, null, removed);
    }

    public static async Task ApplyMiscReceiptInsertionAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit,
        StockReplayFifoMrReceiptValuation receipt,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoReceiptLayerBinding.Validate(plan, receipt.LayerId);
        if (context.Posting.SourceDocumentType is not (
                IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || permit.Operation != "INSERT")
            throw Invalid("FIFO receipt state needs an Inventory receipt insertion root.");
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, plan, cancellationToken);
        await ApplyCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, plan.PhysicalPlan.BalLocId,
            plan.PhysicalPlan.Projection.FinalQuantity, "INSERT", permit,
            cancellationToken, receipt);
    }

    public static async Task ApplyIssueInsertionAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.Candidate.SignedDelta >= 0m)
            throw Invalid("FIFO issue state write needs a negative candidate.");
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, plan, cancellationToken);
        await ApplyCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, plan.PhysicalPlan.BalLocId,
            plan.PhysicalPlan.Projection.FinalQuantity, "INSERT", permit,
            cancellationToken);
    }

    public static async Task ApplyIssueRemovalAsync(
        StockPostingContext context, StockReplayFifoRemovalPlan plan,
        StockReplayFifoWritePermit permit,
        CancellationToken cancellationToken = default)
    {
        StockReplayFifoRemovalPlanner.ValidatePreparedPlan(plan);
        if (plan.PhysicalPlan.RemovedActiveEvent.SignedDelta >= 0m)
            throw Invalid("FIFO issue removal needs a negative removed event.");
        var first = plan.PhysicalPlan.RemovedActiveEvent.Key.EffectiveAt.Date;
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, first, plan.PhysicalPlan.LaterEvents
                .Select(e => e.Key.EffectiveAt.Date).Append(first).Max(),
            cancellationToken);
        await ApplyCoreAsync(context, plan.ItemCode, plan.Snapshot,
            plan.Projection, plan.PlanHash, plan.PhysicalPlan.BalLocId,
            plan.PhysicalPlan.Projection.FinalQuantity, "REMOVE", permit,
            cancellationToken);
    }

    private static async Task ApplyCoreAsync(
        StockPostingContext context, string itemCode,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection, string planHash,
        int balLocId, decimal finalPileQuantity, string operation,
        StockReplayFifoWritePermit permit, CancellationToken cancellationToken,
        StockReplayFifoMrReceiptValuation? receipt = null,
        StockReplayFifoLayerPosition? removedReceipt = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        if (permit.Operation != operation
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != itemCode
            || permit.PlanHash != planHash
            || permit.SnapshotHash != snapshot.SnapshotHash
            || permit.CurrentQuantity != snapshot.CurrentQuantity
            || permit.CurrentValue != snapshot.CurrentValue
            || permit.FinalQuantity != projection.FinalQuantity
            || permit.FinalValue != projection.FinalValue
            || permit.ChangedPileId != balLocId
            || permit.ChangedPileFinalQuantity != finalPileQuantity)
            throw Invalid("FIFO state write needs its prepared issue root permit.");
        var db = context.Db;
        await db.SaveChangesAsync(cancellationToken);
        var state = await db.StockCostStates.SingleOrDefaultAsync(s =>
            s.CompanyCode == context.CompanyCode
            && s.BranchCode == context.BranchCode
            && s.ItemCode == itemCode, cancellationToken);
        if (state is null || state.CostMethod != StockCostMethods.Fifo
            || state.OnHandBaseQty != permit.CurrentQuantity
            || state.InventoryValue != permit.CurrentValue)
            throw Invalid("FIFO item state changed after root preparation.");
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.ICode == itemCode)
            .Select(b => new { b.Id, b.StdQty }).ToArrayAsync(cancellationToken);
        if (piles.Any(p => p.StdQty < 0m
                || IvQty.Round(p.StdQty) != p.StdQty)
            || !piles.Any(p => p.Id == permit.ChangedPileId
                && p.StdQty == permit.ChangedPileFinalQuantity)
            || StockLedgerPrecision.Quantity(piles.Sum(p => p.StdQty))
                != permit.FinalQuantity)
            throw Invalid("FIFO physical piles differ from the replay projection.");
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == context.Posting.Id
                && f.ItemCode == itemCode)
            .ToArrayAsync(cancellationToken);
        var snapshotLayers = snapshot.Layers.ToDictionary(l => l.LayerId);
        var expectedAdjustments = projection.FinalLayers.Where(l =>
                snapshotLayers.TryGetValue(l.LayerId, out var before)
                && l.AccumulatedAdjustment != before.AccumulatedAdjustment)
            .Select(l => new
            {
                Before = snapshotLayers[l.LayerId], Final = l,
                SignedValue = StockLedgerPrecision.Money(
                    l.AccumulatedAdjustment - snapshotLayers[l.LayerId].AccumulatedAdjustment)
            }).ToArray();
        var adjustmentOriginIds = expectedAdjustments.Select(x => x.Before.OriginFactId).ToArray();
        var adjustmentOrigins = await db.StockValuationFacts.AsNoTracking()
            .Where(f => adjustmentOriginIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        var valueFacts = facts.Where(f => f.BaseQty == 0m).ToArray();
        if (expectedAdjustments.Any(x => x.Before.SalesReturn is null
                || !adjustmentOrigins.TryGetValue(x.Before.OriginFactId, out var origin)
                || origin.BaseQty != x.Before.OriginalQuantity
                || origin.CostAmount != x.Before.OriginalValue
                || x.Before.SalesReturn.ReturnedValue != StockLedgerPrecision.Money(
                    origin.CostAmount + x.Before.AccumulatedAdjustment)
                || valueFacts.Count(f => StockReplayFifoSalesReturnValueAdjustment.Matches(
                    f, origin, x.SignedValue)) != 1)
            || valueFacts.Length != expectedAdjustments.Length)
            throw Invalid("FIFO return value facts do not match the prepared layer adjustments.");
        if (facts.Length == 0 || facts.Any(f =>
                f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode
                || f.LedgerEpochId != context.Epoch.Id
                || f.CostMethod != StockCostMethods.Fifo
                || f.Direction is not (-1 or 1)
                || f.ValuationStatus is not (
                    StockValuationStatuses.Valued or StockValuationStatuses.Reversed)
                || f.BaseQty < 0m || f.CostAmount < 0m
                || IvQty.Round(f.BaseQty) != f.BaseQty
                || StockLedgerPrecision.Money(f.CostAmount) != f.CostAmount)
            || StockLedgerPrecision.Quantity(facts.Sum(f => f.SignedQty))
                != StockLedgerPrecision.Quantity(
                    permit.FinalQuantity - permit.CurrentQuantity)
            || StockLedgerPrecision.Money(facts.Sum(f => f.SignedValue))
                != StockLedgerPrecision.Money(
                    permit.FinalValue - permit.CurrentValue))
            throw Invalid("FIFO root facts do not explain the projected item delta.");

        var layers = await db.StockFifoLayers
            .Where(l => l.CompanyCode == context.CompanyCode
                && l.BranchCode == context.BranchCode
                && l.ItemCode == itemCode)
            .ToArrayAsync(cancellationToken);
        var beforeById = snapshot.Layers.ToDictionary(l => l.LayerId);
        var receiptId = receipt?.LayerId;
        var finalById = projection.FinalLayers.ToDictionary(
            l => StockReplayFifoReceiptLayerBinding.Map(l.LayerId, receiptId));
        var archived = layers.Where(l => !beforeById.ContainsKey(l.Id)
                && l.Id != receiptId).ToArray();
        var archivedFactIds = archived.Select(l => l.OriginValuationFactId)
            .ToArray();
        var archivedReversals = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.ReversesValuationFactId.HasValue
                && archivedFactIds.Contains(f.ReversesValuationFactId.Value))
            .Select(f => new { f.ReversesValuationFactId, f.Direction,
                f.MovementCode, f.ValuationStatus, f.ValuationSource,
                f.CompanyCode, f.BranchCode, f.CostMethod, f.BaseQty, f.CostAmount,
                OriginalMovement = f.ReversesValuationFact!.MovementCode,
                OriginalType = f.ReversesValuationFact.SourceDocumentType,
                OriginalPostingId = f.ReversesValuationFact.StockPostingId,
                OriginalHistoryId = f.ReversesValuationFact.InventoryHistoryId,
                ReversedHistoryId = f.InventoryHistory!.ReversesHistoryId,
                PostingType = f.StockPosting!.SourceDocumentType,
                f.StockPosting.PostingRole, f.StockPosting.ReversesPostingId,
                SealedAtUtc = f.StockPosting.SealedAtUtc })
            .ToArrayAsync(cancellationToken);
        if (archived.Any(l => l.Status != StockFifoLayerStatuses.Closed
                || l.RemainingQty != 0m || l.RemainingValue != 0m
                || l.CurrentUnitCost != 0m
                || archivedReversals.Count(f =>
                    f.ReversesValuationFactId == l.OriginValuationFactId
                    && f.Direction == -1
                    && f.ValuationStatus == StockValuationStatuses.Reversed
                    && f.CompanyCode == context.CompanyCode
                    && f.BranchCode == context.BranchCode
                    && f.CostMethod == StockCostMethods.Fifo
                    && (f.MovementCode == "RECEIPT_IN"
                        || f.MovementCode == "RECEIPT_IN_REVERSAL"
                            && f.ValuationSource == StockValuationSources.OriginalReversal
                            && f.OriginalMovement == "RECEIPT_IN"
                            && f.OriginalType is (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                            && f.OriginalPostingId == l.OriginStockPostingId
                            && f.OriginalHistoryId == f.ReversedHistoryId
                            && f.PostingType == f.OriginalType
                            && f.PostingRole == "REVERSAL"
                            && f.ReversesPostingId == l.OriginStockPostingId
                            && f.BaseQty == l.OriginalQty
                            && f.CostAmount == l.OriginalValue
                        || f.MovementCode == "SALE_RETURN_IN_REVERSAL"
                            && f.ValuationSource == StockValuationSources.OriginalReversal
                            && f.OriginalMovement == "SALE_RETURN_IN"
                            && f.OriginalType == IvTrxTypes.CustomerReturn
                            && f.OriginalPostingId == l.OriginStockPostingId
                            && f.OriginalHistoryId == f.ReversedHistoryId
                            && f.PostingType == IvTrxTypes.CustomerReturn
                            && f.PostingRole == "REVERSAL"
                            && f.ReversesPostingId == l.OriginStockPostingId
                            && f.BaseQty == l.OriginalQty
                            && f.CostAmount == l.OriginalValue)
                    && f.SealedAtUtc is not null) != 1)
            || archivedReversals.Length != archived.Length
            || layers.Length != beforeById.Count
                + (receipt is null ? 0 : 1) + archived.Length
            || layers.Length != finalById.Count
                + (removedReceipt is null ? 0 : 1) + archived.Length
            || projection.FinalLayers.Any(l => l.LayerId <= 0
                || l.LayerId == long.MaxValue && receipt is null)
            || StockLedgerPrecision.Quantity(
                projection.FinalLayers.Sum(l => l.RemainingQuantity))
                != permit.FinalQuantity
            || StockLedgerPrecision.Money(
                projection.FinalLayers.Sum(l => l.RemainingValue))
                != permit.FinalValue)
            throw Invalid("FIFO final layers differ from the prepared item state.");
        foreach (var layer in layers)
        {
            if (archived.Any(l => l.Id == layer.Id))
                continue;
            if (removedReceipt is not null
                && layer.Id == removedReceipt.LayerId)
            {
                if (!beforeById.TryGetValue(layer.Id, out var before)
                    || before != removedReceipt
                    || finalById.ContainsKey(layer.Id)
                    || layer.OriginValuationFactId != before.OriginFactId
                    || layer.OriginStockPostingId != before.OriginPostingId
                    || layer.OriginalQty != before.OriginalQuantity
                    || layer.OriginalValue != before.OriginalValue
                    || layer.AccumulatedAdjustment != before.AccumulatedAdjustment
                    || layer.RemainingQty != before.RemainingQuantity
                    || layer.RemainingValue != before.RemainingValue
                    || layer.CurrentUnitCost != before.CurrentUnitCost
                    || layer.Status != before.Status
                    || Convert.ToHexString(layer.RowVersion) != before.RowVersionHex
                    || facts.Count(f =>
                        f.ReversesValuationFactId == before.OriginFactId
                        && f.Direction == -1
                        && f.MovementCode == "RECEIPT_IN") != 1)
                    throw Invalid("The removed FIFO receipt layer has no exact reversal.");
                layer.RemainingQty = 0m;
                layer.RemainingValue = 0m;
                layer.CurrentUnitCost = 0m;
                layer.Status = StockFifoLayerStatuses.Closed;
                continue;
            }
            if (!finalById.TryGetValue(layer.Id, out var final))
                throw Invalid($"FIFO layer {layer.Id} has no projected final position.");
            if (receipt is not null && layer.Id == receipt.LayerId)
            {
                var candidate = projection.CandidateReceipt;
                var fact = facts.SingleOrDefault(f => f.Id == receipt.FactId);
                if (candidate is null || fact is null
                    || receipt.HistoryId != fact.InventoryHistoryId
                    || fact.MovementCode != "RECEIPT_IN"
                    || fact.Direction != 1
                    || fact.CostAmount != candidate.OriginalValue
                    || fact.BaseQty != candidate.OriginalQuantity
                    || layer.OriginValuationFactId != receipt.FactId
                    || layer.OriginStockPostingId != context.Posting.Id
                    || layer.OriginalQty != candidate.OriginalQuantity
                    || layer.OriginalValue != candidate.OriginalValue
                    || layer.RemainingQty != candidate.OriginalQuantity
                    || layer.RemainingValue != candidate.OriginalValue
                    || layer.AccumulatedAdjustment != 0m
                    || layer.CurrentUnitCost != fact.UnitCost
                    || layer.Status != StockFifoLayerStatuses.Open
                    || final.OriginalQuantity != candidate.OriginalQuantity
                    || final.OriginalValue != candidate.OriginalValue
                    || final.AccumulatedAdjustment != 0m
                    || final.RemainingQuantity < 0m
                    || final.RemainingValue < 0m)
                    throw Invalid("The saved FIFO receipt layer differs from the candidate.");
            }
            else if (!beforeById.TryGetValue(layer.Id, out var before)
                || layer.OriginValuationFactId != before.OriginFactId
                || layer.OriginStockPostingId != before.OriginPostingId
                || layer.OriginalQty != before.OriginalQuantity
                || layer.OriginalValue != before.OriginalValue
                || layer.AccumulatedAdjustment != before.AccumulatedAdjustment
                || layer.RemainingQty != before.RemainingQuantity
                || layer.RemainingValue != before.RemainingValue
                || layer.CurrentUnitCost != before.CurrentUnitCost
                || layer.Status != before.Status
                || Convert.ToHexString(layer.RowVersion) != before.RowVersionHex
                || final.OriginalQuantity != before.OriginalQuantity
                || final.OriginalValue != before.OriginalValue
                || final.AccumulatedAdjustment != before.AccumulatedAdjustment
                    && !expectedAdjustments.Any(x => x.Before.LayerId == before.LayerId
                        && x.Final.AccumulatedAdjustment == final.AccumulatedAdjustment)
                || final.RemainingQuantity < 0m
                || final.RemainingValue < 0m)
                throw Invalid($"FIFO layer {layer.Id} changed after root preparation.");
            layer.RemainingQty = final.RemainingQuantity;
            layer.RemainingValue = final.RemainingValue;
            layer.AccumulatedAdjustment = final.AccumulatedAdjustment;
            layer.CurrentUnitCost = final.CurrentUnitCost;
            layer.Status = final.Status;
        }
        state.OnHandBaseQty = permit.FinalQuantity;
        state.InventoryValue = permit.FinalValue;
        state.AverageUnitCost = permit.FinalQuantity == 0m
            ? 0m : StockLedgerPrecision.Money(
                permit.FinalValue / permit.FinalQuantity);
        state.CurrentUnitCost = state.AverageUnitCost;
        var lastFact = facts.OrderByDescending(f => f.PostingLineNo)
            .ThenByDescending(f => f.SplitOrdinal).First();
        state.LastValuationFactId = lastFact.Id;
        state.LastPostingSequence = context.Posting.PostingSequence;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
