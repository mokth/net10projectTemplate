using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoBoundedCountSheetRemovalResult(
    IReadOnlyList<int> ReversalHistoryIds,
    IReadOnlyList<long> RemovalGenerationIds,
    IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Reverses bounded FIFO count adjustments in one shared batch.
/// Proven later receipts remain intact; supported changed issues are corrected.
/// </summary>
public static class StockReplayFifoBoundedCountSheetRemovalWriter
{
    public static async Task<StockReplayFifoBoundedCountSheetRemovalResult>
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
        var repriced = plan.Items.Any(i => i.Projection?.LaterIssues.Any(
            StockReplayFifoCountSheetRemovalPlanner.NeedsIssueRepricing)
            == true);
        var mixed = plan.Sheet.Lines.Any(l =>
            l.Physical.RemovedActiveEvent.SignedDelta < 0m);
        IReadOnlyList<StockReplayFifoCountSheetRemovalItem> items;
        if (plan.Items.Count(i => i.Projection is not null) > 1)
            items = repriced
                ? StockReplayFifoCountSheetRemovalPlanner
                    .RequireRepricedMultiItemAdjustments(plan)
                : mixed
                ? StockReplayFifoCountSheetRemovalPlanner
                    .RequireIndependentMultiItemSignedAdjustments(plan)
                : StockReplayFifoCountSheetRemovalPlanner
                    .RequireIndependentMultiItemPositiveAdjustments(plan);
        else
        {
            var item = mixed
                ? plan.Sheet.Lines.Any(l =>
                    l.Physical.RemovedActiveEvent.SignedDelta > 0m)
                    ? repriced
                        ? StockReplayFifoCountSheetRemovalPlanner
                            .RequireRepricedMixedAdjustments(plan)
                        : StockReplayFifoCountSheetRemovalPlanner
                            .RequireIndependentMixedAdjustments(plan)
                    : StockReplayFifoCountSheetRemovalPlanner
                        .RequireIndependentNegativeAdjustments(plan)
                : repriced
                    ? StockReplayFifoCountSheetRemovalPlanner
                        .RequireRepricedPositiveAdjustments(plan)
                    : StockReplayFifoCountSheetRemovalPlanner
                        .RequireIndependentPositiveAdjustments(plan);
            items = [item];
        }
        var itemByLine = plan.Items.SelectMany(i => i.CountLineIds.Select(id =>
                (Id: id, Item: i)))
            .ToDictionary(pair => pair.Id, pair => pair.Item);
        var sheet = plan.Sheet;
        if (context.Posting.PostingRole != "REVERSAL"
            || context.Posting.ReversesPostingId != sheet.AnchorPostingId)
            throw Invalid("FIFO count rollback needs the original anchor reversal root.");
        context.CostMethod = StockCostMethods.Fifo;
        var db = context.Db;
        var lines = sheet.Lines.OrderBy(l =>
            l.Physical.RemovedActiveEvent.Key).ToArray();
        var lineIds = lines.Select(l => l.CountLineId).ToArray();
        var removed = items.SelectMany(i => i.Projection!.RemovedCounts)
            .ToDictionary(c => c.Key);
        var active = await StockCountGenerationResolver.ResolveActiveAsync(db,
            sheet.CompanyCode, sheet.BranchCode, lineIds,
            cancellationToken);
        if (active.Count != lineIds.Length || lines.Any(l =>
                !active.TryGetValue(l.CountLineId, out var g)
                || g.IsRemoval || g.PreviousGenerationId is not null
                || g.Id != -l.Physical.RemovedActiveEvent.EventId
                || g.AnchorPostingId != sheet.AnchorPostingId
                || g.WritePostingId != sheet.AnchorPostingId
                || g.SignedAdjustmentQty
                    != l.Physical.RemovedActiveEvent.SignedDelta
                || (g.AdjustmentHistoryId is not null)
                    != (g.SignedAdjustmentQty != 0m)
                || (g.AdjustmentBatchNo is not null)
                    != (g.SignedAdjustmentQty != 0m)))
            throw Invalid("FIFO sheet has no unchanged original generations.");
        var historyIds = active.Values.Where(g =>
                g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).ToArray();
        var originalRows = await db.IvTrxHistories.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id))
            .OrderBy(h => h.TrxLineNo).ToArrayAsync(cancellationToken);
        if (originalRows.Length != historyIds.Length
            || originalRows.Select(h => h.Id).Distinct().Count()
                != historyIds.Length)
            throw Invalid("FIFO sheet adjustment histories are incomplete.");
        var originals = originalRows.ToDictionary(h => h.Id);
        var activeFacts = await StockReplayValuationGenerationResolver
            .ResolveAsync(db, sheet.CompanyCode, sheet.BranchCode,
                originalRows, cancellationToken);
        foreach (var line in lines)
        {
            var generation = active[line.CountLineId];
            if (generation.AdjustmentHistoryId is not int historyId)
                continue;
            var item = itemByLine[line.CountLineId];
            var count = removed[line.Physical.RemovedActiveEvent.Key];
            var positive = count.SignedAdjustmentQuantity > 0m;
            var receipt = count.Receipt;
            var history = originals[historyId];
            var facts = activeFacts[historyId];
            if (history.CompanyCode != sheet.CompanyCode
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
                        || history.ToStdQty
                            != count.SignedAdjustmentQuantity
                    : history.FromBalLocId != line.BalLocId
                        || history.ToBalLocId is not null
                        || history.FrStdQty
                            != -count.SignedAdjustmentQuantity)
                || facts.Count == 0
                || facts.Any(f =>
                    f.InventoryHistoryId != historyId
                    || f.StockPostingId != sheet.AnchorPostingId
                    || f.LedgerEpochId != context.Epoch.Id
                    || f.ItemCode != item.ItemCode
                    || f.CostMethod != StockCostMethods.Fifo
                    || f.SourceDocumentType != "IV_STOCK_COUNT"
                    || f.Direction != (positive ? 1 : -1)
                    || f.MovementCode != (positive
                        ? "ADJUST_IN" : "ADJUST_OUT"))
                || facts.Sum(f => f.SignedQty)
                    != count.SignedAdjustmentQuantity
                || (positive
                    ? facts.Count != 1 || receipt is null
                        || facts[0].Id != receipt.OriginFactId
                        || facts[0].SignedValue != receipt.OriginalValue
                    : receipt is not null
                        || facts.Count != count.Consumptions.Count
                        || !facts.Select(f => f.Id).Order()
                            .SequenceEqual(count.Consumptions
                                .Select(c => c.IssueFactId).Order())
                        || count.Consumptions.Any(c => !facts.Any(f =>
                            f.Id == c.IssueFactId
                            && f.SplitOrdinal == c.SplitOrdinal
                            && f.BaseQty == c.Quantity
                            && f.CostAmount == c.Value))
                        || facts.Sum(f => f.CostAmount)
                            != count.Consumptions.Sum(c => c.Value)))
                throw Invalid($"FIFO count line {line.CountLineId} lacks exact valuation authority.");
        }
        var positiveLayerIds = removed.Values.Where(c => c.Receipt is not null)
            .Select(c => c.Receipt!.LayerId).ToHashSet();
        var negativeConsumptions = removed.Values
            .SelectMany(c => c.Consumptions).ToArray();
        var layerIds = repriced
            ? items.SelectMany(i => i.Snapshot.Layers)
                .Select(l => l.LayerId).ToArray()
            : positiveLayerIds.Concat(
                    negativeConsumptions.Select(c => c.LayerId))
                .Distinct().ToArray();
        var layers = await db.StockFifoLayers.Where(l =>
                layerIds.Contains(l.Id)
                && l.CompanyCode == sheet.CompanyCode
                && l.BranchCode == sheet.BranchCode)
            .ToDictionaryAsync(l => l.Id, cancellationToken);
        var snapshotLayers = items.SelectMany(i => i.Snapshot.Layers
                .Select(l => (Layer: l, i.ItemCode)))
            .ToDictionary(pair => pair.Layer.LayerId);
        if (layers.Count != layerIds.Length
            || layers.Any(pair => !snapshotLayers.TryGetValue(pair.Key,
                    out var snapshot)
                || pair.Value.ItemCode != snapshot.ItemCode
                || pair.Value.OriginValuationFactId
                    != snapshot.Layer.OriginFactId
                || pair.Value.OriginalQty != snapshot.Layer.OriginalQuantity
                || pair.Value.RemainingQty != snapshot.Layer.RemainingQuantity
                || pair.Value.OriginalValue != snapshot.Layer.OriginalValue
                || pair.Value.RemainingValue != snapshot.Layer.RemainingValue
                || pair.Value.Status != snapshot.Layer.Status
                || !Convert.ToHexString(pair.Value.RowVersion)
                    .Equals(snapshot.Layer.RowVersionHex,
                        StringComparison.Ordinal))
            || !repriced && await db.StockFifoLayerConsumptions.AsNoTracking()
                .AnyAsync(c => positiveLayerIds.Contains(c.FifoLayerId),
                    cancellationToken)
            || positiveLayerIds.Any(id =>
                layers[id].OriginStockPostingId != sheet.AnchorPostingId
                || layers[id].AccumulatedAdjustment != 0m
                || !repriced
                    && layers[id].Status != StockFifoLayerStatuses.Open))
            throw Stale("FIFO count layers changed before removal.");
        var consumptionIds = negativeConsumptions.Select(c =>
            c.ConsumptionId).ToArray();
        var consumptionRows = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => consumptionIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        if (consumptionRows.Count != consumptionIds.Length
            || negativeConsumptions.Any(c =>
                !consumptionRows.TryGetValue(c.ConsumptionId, out var row)
                || row.ReversesConsumptionId is not null
                || row.IssueValuationFactId != c.IssueFactId
                || row.FifoLayerId != c.LayerId
                || row.ConsumedQty != c.Quantity
                || row.ConsumedValue != c.Value
                || row.SplitOrdinal != c.SplitOrdinal))
            throw Stale("FIFO count issue allocations changed before removal.");
        var header = await db.IvStockCountHdrs.SingleAsync(h =>
            h.Id == sheet.CountHeaderId
            && h.CompanyCode == sheet.CompanyCode
            && h.BranchCode == sheet.BranchCode, cancellationToken);
        if (header.Status != IvStockCountStatuses.Posted
            || !header.RowVersion.SequenceEqual(sheet.HeaderRowVersion)
            || !await StockCountFirstBatchPointer.MatchesAsync(db,
                sheet.CompanyCode, sheet.BranchCode, lineIds,
                header.PostedBatchNo, cancellationToken))
            throw Stale("FIFO count header changed before reversal.");
        var originalBatchNos = active.Values.Where(g =>
                g.AdjustmentBatchNo.HasValue)
            .Select(g => g.AdjustmentBatchNo!.Value).Distinct().ToArray();
        if (originalBatchNos.Length != 1)
            throw Invalid("FIFO count adjustments do not share one original batch.");
        var originalBatchNo = originalBatchNos[0];
        var originalBatch = await db.IvTrxBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.CompanyCode == sheet.CompanyCode
                && b.BranchCode == sheet.BranchCode
                && b.BatchNo == originalBatchNo, cancellationToken);
        var details = originalBatch is null ? []
            : await db.IvTrxBatchDetails.AsNoTracking()
                .Where(d => d.BatchId == originalBatch.Id)
                .ToArrayAsync(cancellationToken);
        var batchHistoryIds = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == sheet.CompanyCode
                && h.BranchCode == sheet.BranchCode
                && h.BatchNo == originalBatchNo)
            .Select(h => h.Id).ToArrayAsync(cancellationToken);
        if (originalBatch is null
            || originalBatch.TrxType != IvTrxTypes.StockAdjustment
            || originalBatch.BatchStatus != IvBatchStatuses.Posted
            || originalBatch.TrxDtTime != header.CountDate.Date
            || originalBatch.RefNo != header.CountNo
            || originalBatch.PostedCount != 1
            || details.Length != originalRows.Length
            || !batchHistoryIds.Order().SequenceEqual(historyIds.Order())
            || details.Select(d => d.TrxLineNo).Distinct().Count()
                != details.Length
            || originalRows.Any(h => !details.Any(d => Matches(d, h))))
            throw Invalid("FIFO count original ADJ batch is incomplete.");
        var pileIds = lines.Select(l => l.BalLocId).ToArray();
        var piles = await db.IvBalLocs.Where(p =>
                p.CompanyCode == sheet.CompanyCode
                && p.BranchCode == sheet.BranchCode
                && pileIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var itemCodes = items.Select(i => i.ItemCode).ToArray();
        var states = await db.StockCostStates.Where(s =>
                s.CompanyCode == sheet.CompanyCode
                && s.BranchCode == sheet.BranchCode
                && itemCodes.Contains(s.ItemCode)
                && s.CostMethod == StockCostMethods.Fifo)
            .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
        if (piles.Count != lines.Length
            || lines.Any(l => !piles.TryGetValue(l.BalLocId, out var pile)
                || pile.ICode != itemByLine[l.CountLineId].ItemCode
                || pile.StdQty != l.Physical.CurrentQuantity
                || !pile.RowVersion.SequenceEqual(
                    l.Physical.BalanceRowVersion))
            || states.Count != items.Count
            || items.Any(i => !states.TryGetValue(i.ItemCode,
                    out var state)
                || state.OnHandBaseQty != i.Snapshot.CurrentQuantity
                || state.InventoryValue != i.Snapshot.CurrentValue))
            throw Stale("FIFO count piles or cost state changed before reversal.");
        db.ChangeTracker.DetectChanges();
        if (Dirty<IvBalLoc>(p => pileIds.Contains(p.Id))
            || Dirty<StockCostState>(s => itemCodes.Contains(s.ItemCode))
            || Dirty<StockFifoLayer>(l => layerIds.Contains(l.Id))
            || Dirty<StockFifoLayerConsumption>(_ => true)
            || Dirty<IvStockCountHdr>(h => h.Id == header.Id)
            || Dirty<IvStockCountLine>(l => lineIds.Contains(l.Id))
            || Dirty<IvTrxBatch>(b => b.Id == originalBatch.Id)
            || Dirty<IvTrxBatchDetail>(d => d.BatchId == originalBatch.Id)
            || Dirty<IvTrxHistory>(h => historyIds.Contains(h.Id))
            || Dirty<StockValuationFact>(f => activeFacts.Values
                .SelectMany(facts => facts).Any(source => source.Id == f.Id)))
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
        var reversals = new IvInventoryHistoryWriter().AppendCountReversal(
            context, originalRows, context.Posting.DocumentRevision,
            header.CountDate, batchNo, 1).ToArray();
        if (reversals.Length != originalRows.Length)
            throw Invalid("FIFO count reversal omitted a history line.");
        foreach (var reversal in reversals)
        {
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
                FromLotId = reversal.FromLotId,
                ToLotId = reversal.ToLotId,
                FrWarehouse = reversal.FrWarehouse,
                ToWarehouse = reversal.ToWarehouse,
                FrLocation = reversal.FrLocation,
                ToLocation = reversal.ToLocation,
                FrLotNo = reversal.FrLotNo,
                ToLotNo = reversal.ToLotNo,
                FrStdQty = reversal.FrStdQty,
                ToStdQty = reversal.ToStdQty,
                FrStdUom = reversal.FrStdUom,
                ToStdUom = reversal.ToStdUom,
                FrPurQty = reversal.FrPurQty,
                ToPurQty = reversal.ToPurQty,
                FrPurUom = reversal.FrPurUom,
                ToPurUom = reversal.ToPurUom,
                IStatus = reversal.IStatus,
                StockCountCostBasisId = reversal.StockCountCostBasisId,
                CostEvidenceType = reversal.CostEvidenceType,
                CostOverrideReason = reversal.CostOverrideReason,
                CostApprovedBy = reversal.CostApprovedBy,
                CostApprovedAtUtc = reversal.CostApprovedAtUtc,
                UnitPrice = reversal.UnitPrice
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        var reversalByOriginal = reversals.ToDictionary(r =>
            r.ReversesHistoryId!.Value);
        var reversedFacts = new List<StockValuationFact>();
        foreach (var original in originalRows)
        {
            var reversal = reversalByOriginal[original.Id];
            var facts = activeFacts[original.Id];
            var inverse = await StockReplayValuationReversalWriter.AppendAsync(
                context, original.Id,
                facts.Select(f => new StockReplayValuationReversalLine(
                    f.Id, reversal.PostingLineNo!.Value,
                    f.SplitOrdinal, reversal.Id)).ToArray(),
                cancellationToken);
            if (inverse.Count != facts.Count)
                throw Invalid("FIFO count reversal omitted a valuation fact.");
            reversedFacts.AddRange(inverse);
            reversal.ValuationStatus = StockValuationStatuses.Reversed;
        }
        var correctedFacts = new List<StockValuationFact>();
        var requiredHistoryIds = new List<int>();
        var correctionLine = checked(reversals.Max(r =>
            r.PostingLineNo!.Value) + 1);
        foreach (var item in items)
        {
            foreach (var issue in item.Projection!.LaterIssues
                .Where(StockReplayFifoCountSheetRemovalPlanner
                    .NeedsIssueRepricing).OrderBy(i => i.Key))
            {
                var replacements = await StockReplayFifoIssueRepricingWriter
                    .AppendCountRemovalAsync(context, plan, item, issue.Key,
                        correctionLine, checked(correctionLine + 1),
                        cancellationToken);
                if (replacements.Count == 0)
                    throw Invalid("FIFO count issue correction produced no replacement.");
                correctedFacts.AddRange(replacements);
                requiredHistoryIds.Add(StockReplayFifoMiRootWriter
                    .HistoryId(issue.Key));
                correctionLine = checked(correctionLine + 2);
            }
        }
        var reversalByFact = reversedFacts.ToDictionary(f =>
            f.ReversesValuationFactId!.Value);
        foreach (var consumed in negativeConsumptions)
        {
            var original = consumptionRows[consumed.ConsumptionId];
            db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
            {
                CompanyCode = sheet.CompanyCode,
                BranchCode = sheet.BranchCode,
                IssueValuationFact = reversalByFact[consumed.IssueFactId],
                FifoLayerId = original.FifoLayerId,
                ConsumedQty = original.ConsumedQty,
                ConsumedValue = original.ConsumedValue,
                SplitOrdinal = original.SplitOrdinal,
                ReversesConsumptionId = original.Id,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            });
        }
        var finalLayers = items.SelectMany(i => i.Projection!.FinalLayers)
            .ToDictionary(l => l.LayerId);
        foreach (var (layerId, layer) in layers)
        {
            if (positiveLayerIds.Contains(layerId))
            {
                layer.RemainingQty = 0m;
                layer.RemainingValue = 0m;
                layer.CurrentUnitCost = 0m;
                layer.Status = StockFifoLayerStatuses.Closed;
            }
            else
            {
                if (!finalLayers.TryGetValue(layerId, out var final))
                    throw Invalid("FIFO count reversal has incomplete final layers.");
                layer.RemainingQty = final.RemainingQuantity;
                layer.RemainingValue = final.RemainingValue;
                layer.CurrentUnitCost = final.CurrentUnitCost;
                layer.Status = final.Status;
            }
        }
        var generations = new List<StockCountReplayGeneration>(lines.Length);
        foreach (var line in lines)
        {
            var pile = piles[line.BalLocId];
            pile.StdQty = line.Physical.Projection.FinalQuantity;
            pile.ModifiedDate = now;
            var timeline = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
                db, sheet.CompanyCode, sheet.BranchCode, pile.Id,
                new StockReplayEventKey(new DateTime(1753, 1, 1),
                    0, 0, "", 0), cancellationToken);
            var latest = timeline.Where(e => e.EventId
                    != line.Physical.RemovedActiveEvent.EventId)
                .Select(e => (DateTime?)e.Key.EffectiveAt).Max();
            pile.TransDate = latest ?? (pile.StdQty > 0m
                ? pile.TransDate : null);
            var prior = active[line.CountLineId];
            var reversal = prior.AdjustmentHistoryId is int historyId
                ? reversalByOriginal[historyId] : null;
            generations.Add(new StockCountReplayGeneration
            {
                CompanyCode = sheet.CompanyCode,
                BranchCode = sheet.BranchCode,
                StockCountLineId = line.CountLineId,
                AnchorPostingId = prior.AnchorPostingId,
                AnchorPostingSequence = prior.AnchorPostingSequence,
                WritePostingId = context.Posting.Id,
                PreviousGenerationId = prior.Id,
                EffectiveAt = prior.EffectiveAt,
                SignedAdjustmentQty = 0m,
                ReversalBatchNo = reversal?.BatchNo,
                ReversalHistoryId = reversal?.Id,
                IsRemoval = true,
                CreatedAtUtc = DateTime.UtcNow
            });
        }
        db.StockCountReplayGenerations.AddRange(generations);
        header.Status = IvStockCountStatuses.RolledBack;
        header.RolledBackBy = user;
        header.RolledBackOn = now;
        header.RollbackReason = reason.Trim();
        header.ModifiedBy = user;
        header.ModifiedDate = now;
        foreach (var item in items)
        {
            var state = states[item.ItemCode];
            state.OnHandBaseQty = item.Projection!.FinalQuantity;
            state.InventoryValue = item.Projection.FinalValue;
            state.AverageUnitCost = state.OnHandBaseQty == 0m ? 0m
                : StockLedgerPrecision.Money(
                    state.InventoryValue / state.OnHandBaseQty);
            state.CurrentUnitCost = state.AverageUnitCost;
            state.LastPostingSequence = context.Posting.PostingSequence;
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var item in items)
        {
            states[item.ItemCode].LastValuationFactId = reversedFacts
                .Concat(correctedFacts)
                .Where(f => f.ItemCode == item.ItemCode)
                .OrderBy(f => f.PostingLineNo)
                .ThenBy(f => f.SplitOrdinal).Last().Id;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(reversals.Select(r => r.Id).ToArray(),
            generations.Select(g => g.Id).ToArray(), requiredHistoryIds);

        bool Dirty<T>(Func<T, bool> matches) where T : class =>
            db.ChangeTracker.Entries<T>().Any(e =>
                matches(e.Entity) && e.State != EntityState.Unchanged);
    }

    private static bool Matches(IvTrxBatchDetail detail,
        IvTrxHistory history) =>
        detail.CompanyCode == history.CompanyCode
        && detail.BranchCode == history.BranchCode
        && detail.BatchNo == history.BatchNo
        && detail.DocumentRevision == history.DocumentRevision
        && detail.TrxLineNo == history.TrxLineNo
        && detail.TrxType == history.TrxType
        && detail.ICode == history.ICode
        && detail.FromBalLocId == history.FromBalLocId
        && detail.ToBalLocId == history.ToBalLocId
        && detail.FromLotId == history.FromLotId
        && detail.ToLotId == history.ToLotId
        && detail.FrWarehouse == history.FrWarehouse
        && detail.ToWarehouse == history.ToWarehouse
        && detail.FrLocation == history.FrLocation
        && detail.ToLocation == history.ToLocation
        && detail.FrLotNo == history.FrLotNo
        && detail.ToLotNo == history.ToLotNo
        && detail.FrStdQty == history.FrStdQty
        && detail.ToStdQty == history.ToStdQty
        && detail.FrStdUom == history.FrStdUom
        && detail.ToStdUom == history.ToStdUom
        && detail.FrPurQty == history.FrPurQty
        && detail.ToPurQty == history.ToPurQty
        && detail.FrPurUom == history.FrPurUom
        && detail.ToPurUom == history.ToPurUom
        && detail.IStatus == history.IStatus
        && detail.StockCountCostBasisId == history.StockCountCostBasisId
        && detail.CostEvidenceType == history.CostEvidenceType
        && detail.CostOverrideReason == history.CostOverrideReason
        && detail.CostApprovedBy == history.CostApprovedBy
        && detail.CostApprovedAtUtc == history.CostApprovedAtUtc
        && detail.UnitPrice == history.UnitPrice;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch,
            message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
