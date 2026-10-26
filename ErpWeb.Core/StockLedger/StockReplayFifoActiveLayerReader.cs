using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoLayerPosition(
    long LayerId, long OriginFactId, long OriginPostingId,
    long OriginPostingSequence,
    DateTime ReceiptEffectiveAt, decimal OriginalQuantity,
    decimal OriginalValue, decimal AccumulatedAdjustment,
    decimal RemainingQuantity, decimal RemainingValue,
    decimal CurrentUnitCost, string BaseUom, string Status,
    string RowVersionHex,
    StockReplayEventKey? ReceiptKey = null);

public sealed record StockReplayFifoConsumptionPosition(
    long ConsumptionId, long IssueFactId, long LayerId,
    decimal Quantity, decimal Value, int SplitOrdinal,
    bool DepletesPool, StockReplayEventKey IssueKey);

public sealed record StockReplayFifoActiveLayerSnapshot(
    string CompanyCode, string BranchCode, string ItemCode,
    decimal CurrentQuantity, decimal CurrentValue,
    IReadOnlyList<StockReplayFifoLayerPosition> Layers,
    IReadOnlyList<StockReplayFifoConsumptionPosition> ActiveConsumptions,
    string SnapshotHash);

/// <summary>
/// Reads an exact current FIFO layer base for historical simulation. Exact MR/GR
/// receipt reversals and direct initial-count removals are supported; other
/// receipt reversals and layer value adjustments fail closed.
/// </summary>
public static class StockReplayFifoActiveLayerReader
{
    internal static bool SameSnapshot(
        StockReplayFifoActiveLayerSnapshot expected,
        StockReplayFifoActiveLayerSnapshot current) =>
        expected.CompanyCode == current.CompanyCode
        && expected.BranchCode == current.BranchCode
        && expected.ItemCode == current.ItemCode
        && expected.CurrentQuantity == current.CurrentQuantity
        && expected.CurrentValue == current.CurrentValue
        && expected.SnapshotHash == current.SnapshotHash
        && expected.Layers.SequenceEqual(current.Layers)
        && expected.ActiveConsumptions.SequenceEqual(current.ActiveConsumptions);

    public static async Task<StockReplayFifoActiveLayerSnapshot> ReadAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(companyCode)
            || string.IsNullOrWhiteSpace(branchCode)
            || string.IsNullOrWhiteSpace(itemCode))
            throw new ArgumentException("A FIFO tenant and item are required.");
        var states = await db.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == itemCode
                && s.CostMethod == StockCostMethods.Fifo)
            .ToArrayAsync(cancellationToken);
        if (states.Length != 1)
            throw Invalid($"Item {itemCode} has no unique FIFO cost state.");
        var state = states[0];
        var physical = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.ICode == itemCode)
            .Select(b => b.StdQty).ToArrayAsync(cancellationToken);
        if (physical.Any(q => q < 0m
                || StockLedgerPrecision.Quantity(q) != q)
            || StockLedgerPrecision.Quantity(physical.Sum())
                != state.OnHandBaseQty
            || state.OnHandBaseQty < 0m || state.InventoryValue < 0m
            || state.OnHandBaseQty == 0m && state.InventoryValue != 0m)
            throw Invalid($"Item {itemCode} FIFO state differs from its piles.");

        var layers = await db.StockFifoLayers.AsNoTracking()
            .Where(l => l.CompanyCode == companyCode
                && l.BranchCode == branchCode && l.ItemCode == itemCode)
            .OrderBy(l => l.ReceiptEffectiveAt)
            .ThenBy(l => l.OriginValuationFactId)
            .ThenBy(l => l.Id)
            .ToArrayAsync(cancellationToken);
        var layerIds = layers.Select(l => l.Id).ToArray();
        if (layerIds.Distinct().Count() != layerIds.Length
            || layers.Select(l => l.OriginValuationFactId).Distinct().Count()
                != layers.Length)
            throw Invalid($"Item {itemCode} has duplicate FIFO layer origins.");
        var rows = await db.StockFifoLayerConsumptions.AsNoTracking()
            .Where(c => layerIds.Contains(c.FifoLayerId))
            .ToArrayAsync(cancellationToken);
        var byConsumptionId = rows.ToDictionary(c => c.Id);
        var reversedIds = new HashSet<long>();
        foreach (var row in rows.Where(c => c.ReversesConsumptionId.HasValue))
        {
            var targetId = row.ReversesConsumptionId!.Value;
            if (!byConsumptionId.TryGetValue(targetId, out var target)
                || target.ReversesConsumptionId.HasValue
                || !reversedIds.Add(targetId)
                || row.CompanyCode != companyCode
                || row.BranchCode != branchCode
                || target.CompanyCode != companyCode
                || target.BranchCode != branchCode
                || row.FifoLayerId != target.FifoLayerId
                || row.ConsumedQty != target.ConsumedQty
                || row.ConsumedValue != target.ConsumedValue
                || row.SplitOrdinal != target.SplitOrdinal)
                throw Invalid($"FIFO consumption {row.Id} has an invalid reversal.");
        }
        var active = rows.Where(c => !c.ReversesConsumptionId.HasValue
                && !reversedIds.Contains(c.Id))
            .OrderBy(c => c.FifoLayerId).ThenBy(c => c.Id).ToArray();
        var factIds = layers.Select(l => l.OriginValuationFactId)
            .Concat(rows.Select(c => c.IssueValuationFactId))
            .Distinct().ToArray();
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => factIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        var factReversals = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.ReversesValuationFactId.HasValue
                && factIds.Contains(f.ReversesValuationFactId.Value))
            .ToArrayAsync(cancellationToken);
        var countLineage = await ReadCountLineageAsync(db, companyCode,
            branchCode, facts.Values, factReversals,
            cancellationToken);
        var countKeys = countLineage.ActiveKeys;
        if (facts.Values.Any(f => f.SourceDocumentType == "IV_STOCK_COUNT"
                && f.InventoryHistoryId is int historyId
                && !countKeys.ContainsKey(historyId)
                && !countLineage.RemovedHistoryIds.Contains(historyId)))
            throw Invalid($"Item {itemCode} has a FIFO count fact without active canonical lineage.");
        var issueHistoryIds = active.Select(c =>
                facts.TryGetValue(c.IssueValuationFactId, out var fact)
                    ? fact.InventoryHistoryId : null)
            .Distinct().ToArray();
        if (issueHistoryIds.Any(id => id is null))
            throw Invalid($"Item {itemCode} has an active FIFO issue without Inventory history.");
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => issueHistoryIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        if (histories.Count != issueHistoryIds.Length
            || histories.Values.Any(h => h.CompanyCode != companyCode
                || h.BranchCode != branchCode || h.ICode != itemCode
                || h.LedgerVersion != 2 || h.EntryRole != "ORIGINAL"
                || h.BatchStatus != "POSTED"
                || h.ValuationStatus != StockValuationStatuses.Valued
                || h.StockPostingId is not > 0))
            throw Invalid($"Item {itemCode} has an invalid FIFO issue owner.");
        var postingIds = layers.Select(l => l.OriginStockPostingId)
            .Concat(facts.Values.Select(f => f.StockPostingId))
            .Concat(histories.Values.Select(h => h.StockPostingId!.Value))
            .Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (facts.Count != factIds.Length || postings.Count != postingIds.Length
            || postings.Values.Any(p => p.SealedAtUtc is null
                || p.CompanyCode != companyCode || p.BranchCode != branchCode)
            || facts.Values.Select(f => f.LedgerEpochId).Distinct().Count() > 1
            || facts.Values.Any(f => !postings.TryGetValue(f.StockPostingId,
                    out var owner) || owner.LedgerEpochId != f.LedgerEpochId))
            throw Invalid($"Item {itemCode} has an unsealed FIFO layer owner.");
        if (histories.Values.Any(h => !postings.TryGetValue(
                    h.StockPostingId!.Value, out var owner)
                || owner.LedgerEpochId != h.LedgerEpochId
                || !countKeys.ContainsKey(h.Id)
                    && owner.EffectiveAt != h.TrxDtTime))
            throw Invalid($"Item {itemCode} has an unsealed FIFO issue owner.");
        var reversedFactIds = factReversals.Select(f =>
            f.ReversesValuationFactId!.Value).ToHashSet();
        if (factReversals.GroupBy(f => f.ReversesValuationFactId)
                .Any(g => g.Count() != 1)
            || factReversals.Any(f => f.CompanyCode != companyCode
                || f.BranchCode != branchCode || f.ItemCode != itemCode
                || f.CostMethod != StockCostMethods.Fifo
                || !facts.TryGetValue(f.ReversesValuationFactId!.Value,
                    out var prior) || f.LedgerEpochId != prior.LedgerEpochId
                || f.Direction != -prior.Direction
                || f.BaseQty != prior.BaseQty
                || f.CostAmount != prior.CostAmount
                || f.ValuationStatus != StockValuationStatuses.Reversed))
            throw Invalid($"Item {itemCode} has duplicate FIFO fact reversals.");
        var reversalOwnerIds = factReversals.Select(f => f.StockPostingId)
            .Distinct().ToArray();
        var reversalOwners = await db.StockPostings.AsNoTracking()
            .Where(p => reversalOwnerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (reversalOwners.Count != reversalOwnerIds.Length
            || factReversals.Any(f =>
                !reversalOwners.TryGetValue(f.StockPostingId, out var owner)
                || owner.SealedAtUtc is null
                || owner.CompanyCode != companyCode
                || owner.BranchCode != branchCode
                || owner.LedgerEpochId != f.LedgerEpochId))
            throw Invalid($"Item {itemCode} has an unsealed FIFO fact reversal.");
        var reversedLayerIds = layers
            .Where(l => reversedFactIds.Contains(l.OriginValuationFactId))
            .Select(l => l.Id).ToHashSet();
        if (reversedLayerIds.Count > 0)
        {
            var reversedLayers = layers.Where(l =>
                reversedLayerIds.Contains(l.Id)).ToArray();
            var reversedOriginIds = reversedLayers.Select(l =>
                l.OriginValuationFactId).ToHashSet();
            var receiptReversals = factReversals.Where(f =>
                f.ReversesValuationFactId is long id
                && reversedOriginIds.Contains(id))
                .ToDictionary(f => f.ReversesValuationFactId!.Value);
            var receiptHistoryIds = reversedLayers.SelectMany(l =>
                new int?[]
                {
                    facts[l.OriginValuationFactId].InventoryHistoryId,
                    receiptReversals[l.OriginValuationFactId].InventoryHistoryId
                }).Where(id => id.HasValue).Select(id => id!.Value)
                .Distinct().ToArray();
            var receiptHistories = await db.IvTrxHistories.AsNoTracking()
                .Where(h => receiptHistoryIds.Contains(h.Id))
                .ToDictionaryAsync(h => h.Id, cancellationToken);
            if (receiptHistoryIds.Length != reversedLayers.Length * 2
                || receiptHistories.Count != receiptHistoryIds.Length
                || reversedLayers.Any(l =>
                {
                    var origin = facts[l.OriginValuationFactId];
                    var reversal = receiptReversals[l.OriginValuationFactId];
                    var removedGeneration = origin.InventoryHistoryId
                        is int countId
                        && countLineage.RemovedByOriginalHistory.TryGetValue(
                            countId, out var lineage)
                            ? lineage : null;
                    var removedCount = removedGeneration is not null;
                    return origin.InventoryHistoryId is not int originalId
                        || reversal.InventoryHistoryId is not int reversalId
                        || !receiptHistories.TryGetValue(originalId,
                            out var originalHistory)
                        || !receiptHistories.TryGetValue(reversalId,
                            out var reversalHistory)
                        || !reversalOwners.TryGetValue(
                            reversal.StockPostingId, out var reversalOwner)
                        || originalHistory.EntryRole != "ORIGINAL"
                        || originalHistory.TrxType is not (
                            IvTrxTypes.MiscellaneousReceipt
                            or IvTrxTypes.GoodsReceive)
                            && (!removedCount
                                || originalHistory.TrxType
                                    != IvTrxTypes.StockAdjustment)
                        || removedCount
                            && (reversalHistory.TrxType
                                != IvTrxTypes.StockAdjustment
                                || removedGeneration.ReversalHistoryId
                                    != reversalId
                                || removedGeneration.WritePostingId
                                    != reversalOwner.Id)
                        || originalHistory.StockPostingId != l.OriginStockPostingId
                        || reversalHistory.EntryRole != "REVERSAL"
                        || reversalHistory.ReversesHistoryId != originalId
                        || reversalHistory.StockPostingId != reversalOwner.Id
                        || reversalHistory.ValuationStatus
                            != StockValuationStatuses.Reversed
                        || reversalOwner.PostingRole != "REVERSAL"
                        || reversalOwner.ReversesPostingId
                            != l.OriginStockPostingId
                        || reversal.MovementCode != origin.MovementCode
                        || reversal.ValuationSource
                            != StockValuationSources.OriginalReversal;
                }))
                throw Invalid($"Item {itemCode} has an invalid FIFO receipt reversal lineage.");
        }
        if (active.Any(c => reversedLayerIds.Contains(c.FifoLayerId)))
            throw Invalid($"Item {itemCode} still consumes a reversed FIFO receipt.");

        var activeIssueFacts = active.Select(c => facts[c.IssueValuationFactId])
            .DistinctBy(f => f.Id).ToArray();
        var activeGenerations = await StockReplayValuationGenerationResolver
            .ResolveAsync(db, companyCode, branchCode,
                histories.Values.ToArray(), cancellationToken);
        foreach (var history in histories.Values)
        {
            var fromConsumptions = activeIssueFacts
                .Where(f => f.InventoryHistoryId == history.Id)
                .Select(f => f.Id).Order().ToArray();
            var fromGeneration = activeGenerations[history.Id]
                .Where(f => f.Direction < 0)
                .Select(f => f.Id).Order().ToArray();
            if (!fromConsumptions.SequenceEqual(fromGeneration)
                || activeGenerations[history.Id]
                    .Select(f => (f.StockPostingId, f.ValuationVersion))
                    .Distinct().Count() != 1)
                throw Invalid($"FIFO history {history.Id} has an incomplete active issue generation.");
        }
        var corrected = activeIssueFacts
            .Where(f => f.OriginalValuationFactId.HasValue).ToArray();
        if (activeIssueFacts.Any(f => f.OriginalValuationFactId is null
                && f.StockPostingId != histories[f.InventoryHistoryId!.Value]
                    .StockPostingId))
            throw Invalid($"Item {itemCode} has an unlinked FIFO issue generation.");
        var originalIds = corrected.Select(f =>
            f.OriginalValuationFactId!.Value).Distinct().ToArray();
        var originals = await db.StockValuationFacts.AsNoTracking()
            .Where(f => originalIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        if (originals.Count != originalIds.Length
            || corrected.Any(f => !originals.TryGetValue(
                    f.OriginalValuationFactId!.Value, out var original)
                || original.OriginalValuationFactId is not null
                || original.ReversesValuationFactId is not null
                || original.StockPostingId != histories[
                    f.InventoryHistoryId!.Value].StockPostingId
                || original.CompanyCode != f.CompanyCode
                || original.BranchCode != f.BranchCode
                || original.LedgerEpochId != f.LedgerEpochId
                || original.InventoryHistoryId != f.InventoryHistoryId
                || original.ItemCode != f.ItemCode
                || original.CostMethod != f.CostMethod
                || original.BaseUom != f.BaseUom
                || original.MovementCode != f.MovementCode
                || original.Direction != f.Direction
                || original.ValuationStatus != StockValuationStatuses.Valued
                || original.ValuationSource != StockValuationSources.Fifo
                || original.EffectiveAt != f.EffectiveAt
                || original.SourceDocumentType != f.SourceDocumentType
                || original.SourceDocumentId != f.SourceDocumentId
                || original.SourceDocumentLine != f.SourceDocumentLine
                || f.ValuationVersion <= original.ValuationVersion))
            throw Invalid($"Item {itemCode} has an invalid FIFO issue correction lineage.");

        var activeTransferFacts = active.Select(c => facts[c.IssueValuationFactId])
            .Where(f => f.MovementCode == "TRANSFER_OUT")
            .DistinctBy(f => f.Id).ToArray();
        if (activeTransferFacts.Any(f => f.InventoryHistoryId is null))
            throw Invalid($"Item {itemCode} has a transfer without history evidence.");
        var transferHistoryIds = activeTransferFacts
            .Select(f => f.InventoryHistoryId!.Value).Distinct().ToArray();
        foreach (var transferGroup in activeTransferFacts.GroupBy(f => f.InventoryHistoryId))
        {
            var companion = activeGenerations[transferGroup.Key!.Value];
            var inbound = companion.Where(f => f.MovementCode == "TRANSFER_IN")
                .ToArray();
            if (inbound.Length != 1 || companion.Count != transferGroup.Count() + 1
                || companion.Any(f => f.CompanyCode != companyCode
                    || f.BranchCode != branchCode || f.ItemCode != itemCode
                    || f.CostMethod != StockCostMethods.Fifo
                    || f.StockPostingId != inbound[0].StockPostingId
                    || f.LedgerEpochId != inbound[0].LedgerEpochId
                    || f.ValuationStatus != StockValuationStatuses.Valued)
                || companion.Count(f => f.MovementCode == "TRANSFER_OUT")
                    != transferGroup.Count()
                || inbound[0].Direction != 1
                || inbound[0].BaseQty != transferGroup.Sum(f => f.BaseQty)
                || inbound[0].CostAmount != transferGroup.Sum(f => f.CostAmount))
                throw Invalid($"FIFO transfer history {transferGroup.Key} has no matching receipt.");
        }

        var activeByLayer = active
            .Where(c => facts[c.IssueValuationFactId].MovementCode != "TRANSFER_OUT")
            .GroupBy(c => c.FifoLayerId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var layer in layers)
        {
            if (!facts.TryGetValue(layer.OriginValuationFactId,
                    out var origin)
                || origin.StockPostingId != layer.OriginStockPostingId
                || origin.CompanyCode != companyCode
                || origin.BranchCode != branchCode
                || origin.ItemCode != itemCode
                || origin.CostMethod != StockCostMethods.Fifo
                || origin.Direction != 1 || origin.BaseQty != layer.OriginalQty
                || origin.CostAmount != layer.OriginalValue
                || origin.EffectiveAt != layer.ReceiptEffectiveAt
                || origin.BaseUom != layer.BaseUom
                || origin.ValuationStatus != StockValuationStatuses.Valued
                || layer.OriginalQty <= 0m || layer.OriginalValue < 0m
                || layer.AccumulatedAdjustment != 0m)
                throw Invalid($"FIFO layer {layer.Id} has an invalid origin.");
            if (reversedLayerIds.Contains(layer.Id))
            {
                if (layer.Status != StockFifoLayerStatuses.Closed
                    || layer.RemainingQty != 0m
                    || layer.RemainingValue != 0m
                    || layer.CurrentUnitCost != 0m)
                    throw Invalid($"Reversed FIFO layer {layer.Id} still has active stock.");
                continue;
            }
            if (layer.RemainingQty < 0m || layer.RemainingValue < 0m
                || layer.Status != (layer.RemainingQty == 0m
                    ? StockFifoLayerStatuses.Closed
                    : StockFifoLayerStatuses.Open)
                || activeByLayer.GetValueOrDefault(layer.Id) is { } allocations
                    && (StockLedgerPrecision.Quantity(layer.OriginalQty
                            - allocations.Sum(c => c.ConsumedQty))
                        != layer.RemainingQty
                        || StockLedgerPrecision.Money(layer.OriginalValue
                            - allocations.Sum(c => c.ConsumedValue))
                            != layer.RemainingValue)
                || !activeByLayer.ContainsKey(layer.Id)
                    && (layer.OriginalQty != layer.RemainingQty
                        || layer.OriginalValue != layer.RemainingValue)
                || layer.RemainingQty == 0m
                    && layer.CurrentUnitCost != 0m
                || layer.RemainingQty > 0m
                    && layer.CurrentUnitCost != StockLedgerPrecision.Money(
                        layer.RemainingValue / layer.RemainingQty)
                    && (activeByLayer.ContainsKey(layer.Id)
                        || layer.CurrentUnitCost != origin.UnitCost))
                throw Invalid($"FIFO layer {layer.Id} does not reconcile to its origin and active issues.");
        }
        foreach (var row in rows)
        {
            if (!facts.TryGetValue(row.IssueValuationFactId, out var fact)
                || row.CompanyCode != companyCode
                || row.BranchCode != branchCode
                || fact.CompanyCode != companyCode
                || fact.BranchCode != branchCode
                || fact.ItemCode != itemCode
                || fact.CostMethod != StockCostMethods.Fifo
                || row.ConsumedQty <= 0m || row.ConsumedValue < 0m
                || row.SplitOrdinal != fact.SplitOrdinal)
                throw Invalid($"FIFO consumption {row.Id} has no matching fact.");
            if (row.ReversesConsumptionId is long targetId)
            {
                var target = byConsumptionId[targetId];
                if (fact.ReversesValuationFactId
                        != target.IssueValuationFactId
                    || fact.Direction != 1
                    || fact.ValuationStatus != StockValuationStatuses.Reversed)
                    throw Invalid($"FIFO consumption {row.Id} has no exact issue reversal.");
            }
            else if (!reversedIds.Contains(row.Id)
                && (fact.Direction != -1
                    || fact.ValuationStatus != StockValuationStatuses.Valued
                    || fact.InventoryHistoryId is null
                    || !histories.TryGetValue(fact.InventoryHistoryId.Value,
                        out var issueHistory)
                    || !postings.TryGetValue(
                        issueHistory.StockPostingId!.Value,
                        out var issueOwner)
                    || fact.EffectiveAt != issueOwner.EffectiveAt
                    || !postings.TryGetValue(
                        layers.Single(l => l.Id == row.FifoLayerId)
                            .OriginStockPostingId, out var receiptOwner)
                    || fact.EffectiveAt < receiptOwner.EffectiveAt
                    || fact.EffectiveAt == receiptOwner.EffectiveAt
                        && issueOwner.PostingSequence
                            < receiptOwner.PostingSequence
                    || reversedFactIds.Contains(fact.Id)))
                throw Invalid($"FIFO consumption {row.Id} has no active issue fact.");
        }
        foreach (var group in active.GroupBy(c => c.IssueValuationFactId))
        {
            var fact = facts[group.Key];
            if (StockLedgerPrecision.Quantity(group.Sum(c => c.ConsumedQty))
                    != fact.BaseQty
                || StockLedgerPrecision.Money(group.Sum(c => c.ConsumedValue))
                    != fact.CostAmount)
                throw Invalid($"FIFO issue fact {fact.Id} has incomplete active consumption.");
        }
        var quantity = StockLedgerPrecision.Quantity(
            layers.Sum(l => l.RemainingQty));
        var value = StockLedgerPrecision.Money(
            layers.Sum(l => l.RemainingValue));
        if (quantity != state.OnHandBaseQty || value != state.InventoryValue)
            throw Invalid($"Item {itemCode} FIFO layers differ from its cost state.");
        var positions = layers.Where(l => !reversedLayerIds.Contains(l.Id))
            .Select(l =>
        {
            var originFact = facts[l.OriginValuationFactId];
            var key = originFact.InventoryHistoryId is int historyId
                && countKeys.TryGetValue(historyId, out var countKey)
                    ? countKey
                    : new StockReplayEventKey(l.ReceiptEffectiveAt,
                        StockReplayEventKey.OrdinaryClass,
                        postings[l.OriginStockPostingId].PostingSequence,
                        OriginLine(originFact), 0);
            if (key.EffectiveAt != l.ReceiptEffectiveAt)
                throw Invalid($"FIFO layer {l.Id} differs from its canonical receipt key.");
            return new StockReplayFifoLayerPosition(
                l.Id, l.OriginValuationFactId, l.OriginStockPostingId,
                key.OriginalPostingSequence,
                l.ReceiptEffectiveAt, l.OriginalQty, l.OriginalValue,
                l.AccumulatedAdjustment, l.RemainingQty, l.RemainingValue,
                l.CurrentUnitCost, l.BaseUom, l.Status,
                Convert.ToHexString(l.RowVersion), key);
        }).ToArray();
        var consumptions = active.Select(c =>
            new StockReplayFifoConsumptionPosition(c.Id,
                c.IssueValuationFactId, c.FifoLayerId,
                c.ConsumedQty, c.ConsumedValue, c.SplitOrdinal,
                facts[c.IssueValuationFactId].MovementCode != "TRANSFER_OUT",
                countKeys.TryGetValue(facts[c.IssueValuationFactId]
                        .InventoryHistoryId!.Value, out var countKey)
                    ? countKey
                    : new StockReplayEventKey(
                        facts[c.IssueValuationFactId].EffectiveAt,
                        StockReplayEventKey.OrdinaryClass,
                        postings[histories[facts[c.IssueValuationFactId]
                            .InventoryHistoryId!.Value].StockPostingId!.Value]
                            .PostingSequence,
                        $"H{facts[c.IssueValuationFactId].InventoryHistoryId!.Value:D10}",
                        0)))
            .ToArray();
        var hash = StockPostingFingerprint.Create(new
        {
            companyCode, branchCode, itemCode,
            state.OnHandBaseQty, state.InventoryValue,
            StateRowVersion = Convert.ToHexString(state.RowVersion),
            Layers = positions, ActiveConsumptions = consumptions
        }, new { }).RequestFingerprint;
        return new(companyCode, branchCode, itemCode,
            quantity, value, positions, consumptions, hash);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private sealed record CountLineage(
        IReadOnlyDictionary<int, StockReplayEventKey> ActiveKeys,
        IReadOnlyDictionary<int, StockCountReplayGeneration> RemovedByOriginalHistory,
        IReadOnlySet<int> RemovedHistoryIds);

    private static async Task<CountLineage> ReadCountLineageAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyCollection<StockValuationFact> facts,
        IReadOnlyCollection<StockValuationFact> factReversals,
        CancellationToken cancellationToken)
    {
        var originalFacts = facts.Where(f =>
                f.SourceDocumentType == "IV_STOCK_COUNT"
                && f.InventoryHistoryId.HasValue
                && f.ReversesValuationFactId is null
                && f.OriginalValuationFactId is null).ToArray();
        var originalHistoryIds = originalFacts.Select(f =>
            f.InventoryHistoryId!.Value).Distinct().ToArray();
        if (originalHistoryIds.Length == 0)
            return new(new Dictionary<int, StockReplayEventKey>(),
                new Dictionary<int, StockCountReplayGeneration>(),
                new HashSet<int>());
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.CompanyCode == companyCode
                && g.BranchCode == branchCode
                && g.AdjustmentHistoryId.HasValue
                && originalHistoryIds.Contains(g.AdjustmentHistoryId.Value))
            .ToArrayAsync(cancellationToken);
        if (generations.Length != originalHistoryIds.Length
            || generations.Select(g => g.AdjustmentHistoryId)
                .Distinct().Count() != generations.Length)
            throw Invalid("FIFO count facts have ambiguous generations.");
        var lineIds = generations.Select(g => g.StockCountLineId)
            .Distinct().ToArray();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(db,
            companyCode, branchCode, lineIds, cancellationToken);
        var lineage = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.CompanyCode == companyCode
                && g.BranchCode == branchCode
                && lineIds.Contains(g.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        var byGeneration = lineage.ToDictionary(g => g.Id);
        var children = lineage.Where(g => g.PreviousGenerationId.HasValue)
            .ToDictionary(g => g.PreviousGenerationId!.Value);
        var allOriginalIds = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.SourceDocumentType == "IV_STOCK_COUNT"
                && f.InventoryHistoryId.HasValue
                && originalHistoryIds.Contains(f.InventoryHistoryId.Value)
                && f.ReversesValuationFactId == null
                && f.OriginalValuationFactId == null)
            .Select(f => f.Id).ToArrayAsync(cancellationToken);
        if (!allOriginalIds.Order().SequenceEqual(
                originalFacts.Select(f => f.Id).Order()))
            throw Invalid("FIFO count facts have incomplete layer coverage.");
        var reversalByOriginal = factReversals
            .Where(f => f.ReversesValuationFactId.HasValue)
            .GroupBy(f => f.ReversesValuationFactId!.Value)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var keys = new Dictionary<int, StockReplayEventKey>();
        var removed = new Dictionary<int, StockCountReplayGeneration>();
        var removedHistoryIds = new HashSet<int>();
        foreach (var generation in generations)
        {
            var historyId = generation.AdjustmentHistoryId!.Value;
            if (!active.TryGetValue(generation.StockCountLineId,
                    out var leaf)
                || generation.PreviousGenerationId is long parentId
                    && (!byGeneration.TryGetValue(parentId, out var parent)
                        || !parent.IsRemoval
                        || parent.StockCountLineId != generation.StockCountLineId)
                || generation.WritePostingId != generation.AnchorPostingId
                || generation.SignedAdjustmentQty == 0m)
                throw Invalid("FIFO count needs one unchanged initial adjustment origin.");
            if (leaf.Id == generation.Id && !leaf.IsRemoval)
            {
                keys.Add(historyId, new StockReplayEventKey(
                    generation.EffectiveAt, StockReplayEventKey.CountClass,
                    generation.AnchorPostingSequence,
                    $"C{generation.StockCountLineId:D10}", 0));
                continue;
            }
            if (!children.TryGetValue(generation.Id, out var removal)
                || !removal.IsRemoval
                || removal.StockCountLineId != generation.StockCountLineId
                || removal.AnchorPostingId != generation.AnchorPostingId
                || removal.AnchorPostingSequence
                    != generation.AnchorPostingSequence
                || removal.ReversalHistoryId is not int reversalHistoryId
                || originalFacts.Where(f => f.InventoryHistoryId == historyId)
                    .Any(f => !reversalByOriginal.TryGetValue(f.Id,
                            out var reversals)
                        || reversals.Length != 1
                        || reversals[0].InventoryHistoryId
                            != reversalHistoryId
                        || reversals[0].StockPostingId
                            != removal.WritePostingId
                        || reversals[0].SourceDocumentType
                            != "IV_STOCK_COUNT"))
                throw Invalid("FIFO count removal has incomplete fact reversals.");
            removed.Add(historyId, removal);
            removedHistoryIds.Add(historyId);
            removedHistoryIds.Add(reversalHistoryId);
        }
        return new(keys, removed, removedHistoryIds);
    }

    private static string OriginLine(StockValuationFact fact) =>
        fact.InventoryHistoryId is int historyId
            ? $"H{historyId:D10}"
            : $"V{fact.Id:D20}";
}
