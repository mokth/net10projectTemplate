using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoIsolatedCountRemovalResult(
    int ReversalHistoryId, IReadOnlyList<long> RemovalGenerationIds);

/// <summary>
/// Reverses one FIFO count adjustment and any unchanged zero-adjustment lines
/// while preserving earlier issues, later unchanged issues, and independent
/// later receipts. Multiple adjustments or repriced issues need the full writer.
/// </summary>
public static class StockReplayFifoIsolatedCountRemovalWriter
{
    public static async Task<StockReplayFifoIsolatedCountRemovalResult>
        AppendWithinRootAsync(StockPostingContext context,
            StockReplayFifoCountSheetRemovalPlan plan,
            IRunningNumberService numbers, string reason, DateTime now,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 250)
            throw Invalid("FIFO count rollback needs a valid reason.");
        await StockReplayFifoCountSheetRemovalPlanner.ValidateWithinRootAsync(
            context, plan, cancellationToken);
        var item = StockReplayFifoCountSheetRemovalPlanner
            .RequireIsolatedAdjustment(plan);
        if (context.Posting.PostingRole != "REVERSAL"
            || context.Posting.ReversesPostingId != plan.Sheet.AnchorPostingId)
            throw Invalid("FIFO count rollback needs the original anchor reversal root.");
        context.CostMethod = StockCostMethods.Fifo;
        var sheet = plan.Sheet;
        var line = sheet.Lines.Single(l =>
            l.Physical.RemovedActiveEvent.SignedDelta != 0m);
        var removed = item.Projection!.RemovedCounts[0];
        var positive = removed.SignedAdjustmentQuantity > 0m;
        var receipt = removed.Receipt;
        var db = context.Db;
        var lineIds = sheet.Lines.Select(l => l.CountLineId).ToArray();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(db,
            sheet.CompanyCode, sheet.BranchCode, lineIds,
            cancellationToken);
        if (active.Count != lineIds.Length
            || sheet.Lines.Where(l => l.CountLineId != line.CountLineId)
                .Any(l => !active.TryGetValue(l.CountLineId, out var zero)
                    || zero.IsRemoval || zero.PreviousGenerationId is not null
                    || zero.Id != -l.Physical.RemovedActiveEvent.EventId
                    || zero.AnchorPostingId != sheet.AnchorPostingId
                    || zero.WritePostingId != sheet.AnchorPostingId
                    || zero.SignedAdjustmentQty != 0m
                    || zero.AdjustmentHistoryId is not null
                    || zero.AdjustmentBatchNo is not null)
            || !active.TryGetValue(line.CountLineId, out var generation)
            || generation.IsRemoval || generation.PreviousGenerationId is not null
            || generation.AdjustmentHistoryId is not int historyId
            || generation.Id != -line.Physical.RemovedActiveEvent.EventId
            || generation.AnchorPostingId != sheet.AnchorPostingId
            || generation.WritePostingId != sheet.AnchorPostingId
            || generation.SignedAdjustmentQty
                != removed.SignedAdjustmentQuantity)
            throw Invalid("FIFO count has no unchanged adjustment generation.");
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null || history.CompanyCode != sheet.CompanyCode
            || history.BranchCode != sheet.BranchCode
            || history.StockPostingId != sheet.AnchorPostingId
            || history.LedgerEpochId != context.Epoch.Id
            || history.LedgerVersion != 2
            || history.TrxType != IvTrxTypes.StockAdjustment
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.EntryRole != "ORIGINAL"
            || history.ReversesHistoryId is not null
            || history.BatchNo != generation.AdjustmentBatchNo
            || history.ICode != item.ItemCode
            || (positive
                ? history.ToBalLocId != line.BalLocId
                    || history.FromBalLocId is not null
                    || history.ToStdQty != removed.SignedAdjustmentQuantity
                : history.FromBalLocId != line.BalLocId
                    || history.ToBalLocId is not null
                    || history.FrStdQty != -removed.SignedAdjustmentQuantity))
            throw Invalid("FIFO count has no exact valued ADJ history.");
        var activeFacts = await StockReplayValuationGenerationResolver
            .ResolveAsync(db, sheet.CompanyCode, sheet.BranchCode,
                [history], cancellationToken);
        var facts = activeFacts[historyId];
        if (facts.Count == 0 || facts.Any(f =>
                f.StockPostingId != sheet.AnchorPostingId
                || f.Direction != (positive ? 1 : -1)
                || f.MovementCode != (positive ? "ADJUST_IN" : "ADJUST_OUT")
                || f.CostMethod != StockCostMethods.Fifo
                || f.SourceDocumentType != "IV_STOCK_COUNT")
            || facts.Sum(f => f.SignedQty)
                != removed.SignedAdjustmentQuantity
            || (positive
                ? facts.Count != 1 || receipt is null
                    || facts[0].Id != receipt.OriginFactId
                    || facts[0].SignedValue != receipt.OriginalValue
                : facts.Count != removed.Consumptions.Count
                    || facts.Select(f => f.Id).Order().SequenceEqual(
                        removed.Consumptions.Select(c => c.IssueFactId)
                            .Order()) is false
                    || removed.Consumptions.Any(c =>
                        !facts.Any(f => f.Id == c.IssueFactId
                            && f.SplitOrdinal == c.SplitOrdinal
                            && f.BaseQty == c.Quantity
                            && f.CostAmount == c.Value))
                    || facts.Sum(f => f.CostAmount)
                        != removed.Consumptions.Sum(c => c.Value)))
            throw Invalid("FIFO count has incomplete original valuation facts.");
        var touchedLayerIds = positive
            ? new[] { receipt!.LayerId }
            : removed.Consumptions.Select(c => c.LayerId)
                .Distinct().ToArray();
        var layers = await db.StockFifoLayers.Where(l =>
                touchedLayerIds.Contains(l.Id)
                && l.CompanyCode == sheet.CompanyCode
                && l.BranchCode == sheet.BranchCode
                && l.ItemCode == item.ItemCode)
            .ToDictionaryAsync(l => l.Id, cancellationToken);
        var snapshotLayers = item.Snapshot.Layers
            .ToDictionary(l => l.LayerId);
        if (layers.Count != touchedLayerIds.Length
            || layers.Any(pair => !snapshotLayers.TryGetValue(pair.Key,
                    out var snapshot)
                || pair.Value.OriginValuationFactId != snapshot.OriginFactId
                || pair.Value.OriginalQty != snapshot.OriginalQuantity
                || pair.Value.RemainingQty != snapshot.RemainingQuantity
                || pair.Value.OriginalValue != snapshot.OriginalValue
                || pair.Value.RemainingValue != snapshot.RemainingValue
                || pair.Value.Status != snapshot.Status
                || !Convert.ToHexString(pair.Value.RowVersion)
                    .Equals(snapshot.RowVersionHex,
                        StringComparison.Ordinal)))
            throw Stale("FIFO count layers changed before removal.");
        if (positive && (layers[receipt!.LayerId].OriginStockPostingId
                != sheet.AnchorPostingId
            || receipt.OriginalQuantity != receipt.RemainingQuantity
            || receipt.OriginalValue != receipt.RemainingValue
            || layers[receipt.LayerId].AccumulatedAdjustment != 0m
            || layers[receipt.LayerId].Status != StockFifoLayerStatuses.Open
            || await db.StockFifoLayerConsumptions.AsNoTracking()
                .AnyAsync(c => c.FifoLayerId == receipt.LayerId,
                    cancellationToken)))
            throw Stale("FIFO count receipt layer changed before removal.");
        var consumptionIds = removed.Consumptions
            .Select(c => c.ConsumptionId).ToArray();
        var consumptionRows = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => consumptionIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        if (!positive && (consumptionRows.Count != consumptionIds.Length
            || removed.Consumptions.Any(c =>
                !consumptionRows.TryGetValue(c.ConsumptionId, out var row)
                || row.ReversesConsumptionId is not null
                || row.IssueValuationFactId != c.IssueFactId
                || row.FifoLayerId != c.LayerId
                || row.ConsumedQty != c.Quantity
                || row.ConsumedValue != c.Value
                || row.SplitOrdinal != c.SplitOrdinal)))
            throw Stale("FIFO count issue allocations changed before removal.");
        var header = await db.IvStockCountHdrs.SingleAsync(h =>
            h.Id == sheet.CountHeaderId
            && h.CompanyCode == sheet.CompanyCode
            && h.BranchCode == sheet.BranchCode, cancellationToken);
        if (header.Status != IvStockCountStatuses.Posted
            || !header.RowVersion.SequenceEqual(sheet.HeaderRowVersion)
            || !await StockCountFirstBatchPointer.MatchesAsync(db,
                sheet.CompanyCode, sheet.BranchCode,
                lineIds, header.PostedBatchNo,
                cancellationToken))
            throw Stale("FIFO count header changed before reversal.");
        var originalBatch = await db.IvTrxBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && b.BatchNo == history.BatchNo, cancellationToken);
        var originalDetails = originalBatch is null ? []
            : await db.IvTrxBatchDetails.AsNoTracking()
                .Where(d => d.BatchId == originalBatch.Id)
                .ToArrayAsync(cancellationToken);
        var originalHistoryIds = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == sheet.CompanyCode
                && h.BranchCode == sheet.BranchCode
                && h.BatchNo == history.BatchNo)
            .Select(h => h.Id).ToArrayAsync(cancellationToken);
        if (originalBatch is null
            || originalBatch.TrxType != IvTrxTypes.StockAdjustment
            || originalBatch.BatchStatus != IvBatchStatuses.Posted
            || originalBatch.TrxDtTime != header.CountDate.Date
            || originalBatch.RefNo != header.CountNo
            || originalBatch.PostedCount != 1
            || originalDetails.Length != 1
            || originalHistoryIds.Length != 1
            || originalHistoryIds[0] != history.Id
            || originalDetails[0].CompanyCode != sheet.CompanyCode
            || originalDetails[0].BranchCode != sheet.BranchCode
            || originalDetails[0].BatchNo != history.BatchNo
            || originalDetails[0].DocumentRevision != history.DocumentRevision
            || originalDetails[0].TrxLineNo != history.TrxLineNo
            || originalDetails[0].TrxType != history.TrxType
            || originalDetails[0].ICode != history.ICode
            || originalDetails[0].FromBalLocId != history.FromBalLocId
            || originalDetails[0].ToBalLocId != history.ToBalLocId
            || originalDetails[0].FromLotId != history.FromLotId
            || originalDetails[0].ToLotId != history.ToLotId
            || originalDetails[0].FrWarehouse != history.FrWarehouse
            || originalDetails[0].ToWarehouse != history.ToWarehouse
            || originalDetails[0].FrLocation != history.FrLocation
            || originalDetails[0].ToLocation != history.ToLocation
            || originalDetails[0].FrLotNo != history.FrLotNo
            || originalDetails[0].ToLotNo != history.ToLotNo
            || originalDetails[0].FrStdQty != history.FrStdQty
            || originalDetails[0].ToStdQty != history.ToStdQty
            || originalDetails[0].FrStdUom != history.FrStdUom
            || originalDetails[0].ToStdUom != history.ToStdUom
            || originalDetails[0].FrPurQty != history.FrPurQty
            || originalDetails[0].ToPurQty != history.ToPurQty
            || originalDetails[0].FrPurUom != history.FrPurUom
            || originalDetails[0].ToPurUom != history.ToPurUom
            || originalDetails[0].IStatus != history.IStatus
            || originalDetails[0].StockCountCostBasisId
                != history.StockCountCostBasisId
            || originalDetails[0].CostEvidenceType != history.CostEvidenceType
            || originalDetails[0].CostOverrideReason
                != history.CostOverrideReason
            || originalDetails[0].CostApprovedBy != history.CostApprovedBy
            || originalDetails[0].CostApprovedAtUtc != history.CostApprovedAtUtc
            || originalDetails[0].UnitPrice != history.UnitPrice)
            throw Invalid("FIFO count original ADJ batch is incomplete.");
        var pile = await db.IvBalLocs.SingleAsync(p => p.Id == line.BalLocId
            && p.CompanyCode == sheet.CompanyCode
            && p.BranchCode == sheet.BranchCode, cancellationToken);
        var state = await db.StockCostStates.SingleAsync(s =>
            s.CompanyCode == sheet.CompanyCode
            && s.BranchCode == sheet.BranchCode
            && s.ItemCode == item.ItemCode
            && s.CostMethod == StockCostMethods.Fifo, cancellationToken);
        if (pile.ICode != item.ItemCode
            || pile.StdQty != line.Physical.CurrentQuantity
            || !pile.RowVersion.SequenceEqual(line.Physical.BalanceRowVersion)
            || state.OnHandBaseQty != item.Snapshot.CurrentQuantity
            || state.InventoryValue != item.Snapshot.CurrentValue)
            throw Stale("FIFO count physical or cost state changed before reversal.");
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries<IvBalLoc>().Any(e =>
                sheet.Lines.Any(l => l.BalLocId == e.Entity.Id)
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<StockCostState>().Any(e =>
                e.Entity.ItemCode == item.ItemCode
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<StockFifoLayer>().Any(e =>
                touchedLayerIds.Contains(e.Entity.Id)
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<StockFifoLayerConsumption>().Any(e =>
                consumptionIds.Contains(e.Entity.Id)
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvStockCountHdr>().Any(e =>
                e.Entity.Id == header.Id && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvStockCountLine>().Any(e =>
                lineIds.Contains(e.Entity.Id)
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxBatch>().Any(e =>
                e.Entity.Id == originalBatch.Id
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxBatchDetail>().Any(e =>
                e.Entity.BatchId == originalBatch.Id
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxHistory>().Any(e =>
                e.Entity.Id == history.Id
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                facts.Any(f => f.Id == e.Entity.Id)
                && e.State != EntityState.Unchanged))
            throw Invalid("FIFO count reversal has pending source edits.");

        var batchNo = await numbers.GetNextAsync(db,
            sheet.CompanyCode, RunningNumberKeys.IvBatch,
            cancellationToken);
        if (await db.IvTrxBatches.AsNoTracking().AnyAsync(b =>
                b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && b.BatchNo == batchNo, cancellationToken))
            throw Invalid("FIFO count reversal batch number is already used.");
        var user = context.UserId.Length > 10
            ? context.UserId[..10] : context.UserId;
        var batch = new IvTrxBatch
        {
            CompanyCode = sheet.CompanyCode,
            BranchCode = sheet.BranchCode,
            BatchNo = batchNo,
            TrxDtTime = header.CountDate.Date,
            TrxType = IvTrxTypes.StockAdjustment,
            BatchStatus = IvBatchStatuses.Posted,
            RefNo = header.CountNo,
            PostedDate = now, PostedBy = user, PostedCount = 1,
            CreatedDate = now, CreatedBy = user
        };
        db.IvTrxBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        var reversal = new IvInventoryHistoryWriter().AppendCountReversal(
            context, [history], context.Posting.DocumentRevision,
            header.CountDate, batchNo, 1).Single();
        reversal.ValuationStatus = StockValuationStatuses.Unvalued;
        db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
        {
            BatchId = batch.Id,
            CompanyCode = sheet.CompanyCode,
            BranchCode = sheet.BranchCode,
            BatchNo = batchNo,
            DocumentRevision = context.Posting.DocumentRevision,
            TrxLineNo = reversal.TrxLineNo,
            TrxType = IvTrxTypes.StockAdjustment,
            ICode = reversal.ICode, IDesc = reversal.IDesc,
            FromBalLocId = reversal.FromBalLocId,
            ToBalLocId = reversal.ToBalLocId,
            FromLotId = reversal.FromLotId, ToLotId = reversal.ToLotId,
            FrWarehouse = reversal.FrWarehouse,
            ToWarehouse = reversal.ToWarehouse,
            FrLocation = reversal.FrLocation,
            ToLocation = reversal.ToLocation,
            FrLotNo = reversal.FrLotNo, ToLotNo = reversal.ToLotNo,
            FrStdQty = reversal.FrStdQty, ToStdQty = reversal.ToStdQty,
            FrStdUom = reversal.FrStdUom, ToStdUom = reversal.ToStdUom,
            FrPurQty = reversal.FrPurQty, ToPurQty = reversal.ToPurQty,
            FrPurUom = reversal.FrPurUom, ToPurUom = reversal.ToPurUom,
            IStatus = reversal.IStatus,
            StockCountCostBasisId = reversal.StockCountCostBasisId,
            CostEvidenceType = reversal.CostEvidenceType,
            CostOverrideReason = reversal.CostOverrideReason,
            CostApprovedBy = reversal.CostApprovedBy,
            CostApprovedAtUtc = reversal.CostApprovedAtUtc,
            UnitPrice = reversal.UnitPrice
        });
        await db.SaveChangesAsync(cancellationToken);
        var reversedFacts = await StockReplayValuationReversalWriter.AppendAsync(
            context, history.Id,
            facts.Select(f => new StockReplayValuationReversalLine(f.Id,
                reversal.PostingLineNo!.Value, f.SplitOrdinal,
                reversal.Id)).ToArray(), cancellationToken);
        if (reversedFacts.Count != facts.Count)
            throw Invalid("FIFO count reversal omitted a valuation split.");
        reversal.ValuationStatus = StockValuationStatuses.Reversed;
        if (positive)
        {
            var layer = layers[receipt!.LayerId];
            layer.RemainingQty = 0m;
            layer.RemainingValue = 0m;
            layer.CurrentUnitCost = 0m;
            layer.Status = StockFifoLayerStatuses.Closed;
        }
        else
        {
            var reversalByFact = reversedFacts.ToDictionary(f =>
                f.ReversesValuationFactId!.Value);
            foreach (var consumed in removed.Consumptions)
            {
                var original = consumptionRows[consumed.ConsumptionId];
                var inverse = reversalByFact[consumed.IssueFactId];
                db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
                {
                    CompanyCode = sheet.CompanyCode,
                    BranchCode = sheet.BranchCode,
                    IssueValuationFact = inverse,
                    FifoLayerId = original.FifoLayerId,
                    ConsumedQty = original.ConsumedQty,
                    ConsumedValue = original.ConsumedValue,
                    SplitOrdinal = original.SplitOrdinal,
                    ReversesConsumptionId = original.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = context.UserId
                });
            }
            var finalLayers = item.Projection!.FinalLayers
                .ToDictionary(l => l.LayerId);
            if (finalLayers.Count != item.Snapshot.Layers.Count
                || item.Snapshot.Layers.Any(l =>
                    !finalLayers.ContainsKey(l.LayerId)))
                throw Invalid("FIFO count reversal has incomplete final layers.");
            foreach (var (layerId, layer) in layers)
            {
                var final = finalLayers[layerId];
                layer.RemainingQty = final.RemainingQuantity;
                layer.RemainingValue = final.RemainingValue;
                layer.CurrentUnitCost = final.CurrentUnitCost;
                layer.Status = final.Status;
            }
        }
        pile.StdQty = line.Physical.Projection.FinalQuantity;
        pile.ModifiedDate = now;
        var priorEvents = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, sheet.CompanyCode, sheet.BranchCode, pile.Id,
            new StockReplayEventKey(new DateTime(1753, 1, 1), 0,
                0, "", 0), cancellationToken);
        var latest = priorEvents.Where(e => e.EventId
                != line.Physical.RemovedActiveEvent.EventId)
            .Select(e => (DateTime?)e.Key.EffectiveAt).Max();
        pile.TransDate = latest ?? (pile.StdQty > 0m ? pile.TransDate : null);
        var removedGeneration = new StockCountReplayGeneration
        {
            CompanyCode = sheet.CompanyCode,
            BranchCode = sheet.BranchCode,
            StockCountLineId = line.CountLineId,
            AnchorPostingId = generation.AnchorPostingId,
            AnchorPostingSequence = generation.AnchorPostingSequence,
            WritePostingId = context.Posting.Id,
            PreviousGenerationId = generation.Id,
            EffectiveAt = generation.EffectiveAt,
            SignedAdjustmentQty = 0m,
            ReversalBatchNo = batchNo,
            ReversalHistoryId = reversal.Id,
            IsRemoval = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        var zeroRemovals = sheet.Lines
            .Where(l => l.CountLineId != line.CountLineId)
            .Select(l =>
            {
                var prior = active[l.CountLineId];
                return new StockCountReplayGeneration
                {
                    CompanyCode = sheet.CompanyCode,
                    BranchCode = sheet.BranchCode,
                    StockCountLineId = l.CountLineId,
                    AnchorPostingId = prior.AnchorPostingId,
                    AnchorPostingSequence = prior.AnchorPostingSequence,
                    WritePostingId = context.Posting.Id,
                    PreviousGenerationId = prior.Id,
                    EffectiveAt = prior.EffectiveAt,
                    SignedAdjustmentQty = 0m,
                    IsRemoval = true,
                    CreatedAtUtc = DateTime.UtcNow
                };
            }).ToArray();
        db.StockCountReplayGenerations.Add(removedGeneration);
        db.StockCountReplayGenerations.AddRange(zeroRemovals);
        header.Status = IvStockCountStatuses.RolledBack;
        header.RolledBackBy = user;
        header.RolledBackOn = now;
        header.RollbackReason = reason.Trim();
        header.ModifiedBy = user;
        header.ModifiedDate = now;
        state.OnHandBaseQty = item.Projection.FinalQuantity;
        state.InventoryValue = item.Projection.FinalValue;
        state.AverageUnitCost = state.OnHandBaseQty == 0m ? 0m
            : StockLedgerPrecision.Money(
                state.InventoryValue / state.OnHandBaseQty);
        state.CurrentUnitCost = state.AverageUnitCost;
        state.LastPostingSequence = context.Posting.PostingSequence;
        await db.SaveChangesAsync(cancellationToken);
        state.LastValuationFactId = reversedFacts
            .OrderBy(f => f.PostingLineNo)
            .ThenBy(f => f.SplitOrdinal).Last().Id;
        await db.SaveChangesAsync(cancellationToken);
        return new(reversal.Id, [removedGeneration.Id,
            ..zeroRemovals.Select(g => g.Id)]);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
