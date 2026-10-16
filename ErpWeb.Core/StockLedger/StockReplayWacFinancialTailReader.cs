using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacFinancialTail(
    decimal CurrentQuantity,
    decimal CurrentValue,
    IReadOnlyList<StockReplayWacEvent> LaterEvents,
    IReadOnlyDictionary<int, IReadOnlyList<StockReplayPhysicalEvent>> PhysicalByPile,
    IReadOnlyDictionary<int, decimal> CurrentQuantityByPile);

/// <summary>
/// Reads an item's active WAC tail by joining canonical pile events to complete
/// valuation generations and original value-only facts. Count events remain
/// unbound until the caller supplies each pile's new physical projection.
/// This read-only result does not authorize a financial replay write.
/// </summary>
public static class StockReplayWacFinancialTailReader
{
    public static async Task<StockReplayWacFinancialTail> ReadLaterAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        StockReplayEventKey after,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(companyCode)
            || string.IsNullOrWhiteSpace(branchCode)
            || string.IsNullOrWhiteSpace(itemCode))
            throw new ArgumentException("A tenant and item are required.");

        var states = await db.StockCostStates.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode && s.ItemCode == itemCode)
            .ToArrayAsync(cancellationToken);
        if (states.Length != 1
            || states[0].CostMethod != StockCostMethods.MovingAverage)
            throw Corrupt($"Item {itemCode} has no unique active WAC state.");
        var state = states[0];
        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.ICode == itemCode)
            .Select(b => new { b.Id, b.StdQty })
            .ToArrayAsync(cancellationToken);
        if (balances.Any(b => b.Id <= 0 || b.StdQty < 0m)
            || StockLedgerPrecision.Quantity(balances.Sum(b => b.StdQty))
                != state.OnHandBaseQty
            || state.OnHandBaseQty < 0m || state.InventoryValue < 0m
            || state.OnHandBaseQty == 0m && state.InventoryValue != 0m)
            throw Corrupt($"Item {itemCode} WAC state differs from its physical piles.");

        var byPile = new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>();
        foreach (var balance in balances.OrderBy(b => b.Id))
            byPile.Add(balance.Id,
                await StockReplayPhysicalTimelineReader.ReadLaterAsync(db,
                    companyCode, branchCode, balance.Id, after, cancellationToken));

        var physicalGroups = byPile.SelectMany(p => p.Value.Select(e =>
                new { PileId = p.Key, Event = e }))
            .GroupBy(x => x.Event.EventId).ToArray();
        var ordinaryIds = physicalGroups.Where(g => g.Key > 0)
            .Select(g => checked((int)g.Key)).ToArray();
        var countIds = physicalGroups.Where(g => g.Key < 0)
            .Select(g => -g.Key).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ordinaryIds.Contains(h.Id))
            .ToArrayAsync(cancellationToken);
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => countIds.Contains(g.Id))
            .ToArrayAsync(cancellationToken);
        if (histories.Length != ordinaryIds.Length
            || generations.Length != countIds.Length)
            throw Corrupt($"Item {itemCode} has an unresolved physical WAC event.");
        var historyById = histories.ToDictionary(h => h.Id);
        var generationById = generations.ToDictionary(g => g.Id);

        var countLineIds = generations.Select(g => g.StockCountLineId).ToArray();
        var countLines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => countLineIds.Contains(l.Id))
            .Select(l => new { l.Id, l.BalLocId, l.ICode,
                l.Header.CompanyCode, l.Header.BranchCode })
            .ToArrayAsync(cancellationToken);
        var countLineById = countLines.ToDictionary(l => l.Id);
        if (countLines.Length != countLineIds.Length)
            throw Corrupt($"Item {itemCode} has an unresolved count line.");
        var basisRows = await db.StockCountCostBases.AsNoTracking()
            .Where(b => countLineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        if (basisRows.GroupBy(b => b.StockCountLineId).Any(g => g.Count() != 1))
            throw Corrupt($"Item {itemCode} has duplicate count cost bases.");
        var basisByLine = basisRows.ToDictionary(b => b.StockCountLineId);
        var countHistoryIds = generations.Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).ToArray();
        var countHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => countHistoryIds.Contains(h.Id))
            .ToArrayAsync(cancellationToken);
        if (countHistories.Length != countHistoryIds.Length
            || countHistories.Any(h => h.ValuationStatus
                != StockValuationStatuses.Valued))
            throw Corrupt($"Item {itemCode} has a count without active adjustment history.");
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, companyCode, branchCode,
            histories.Concat(countHistories).ToArray(), cancellationToken);
        var correctedCountHistoryIds = generations
            .Where(g => g.AdjustmentHistoryId is int historyId
                && activeFacts[historyId].Count == 1
                && activeFacts[historyId][0].StockPostingId != g.WritePostingId)
            .Select(g => g.AdjustmentHistoryId!.Value).Distinct().ToArray();
        var originalCountFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.InventoryHistoryId.HasValue
                && correctedCountHistoryIds.Contains(f.InventoryHistoryId.Value)
                && f.OriginalValuationFactId == null
                && f.ReversesValuationFactId == null)
            .ToArrayAsync(cancellationToken);
        var activeFactRows = activeFacts.Values.SelectMany(f => f).ToArray();
        var factPostingIds = activeFactRows.Select(f => f.StockPostingId)
            .Distinct().ToArray();
        var factPostings = await db.StockPostings.AsNoTracking()
            .Where(p => factPostingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (activeFactRows.Any(f => !factPostings.TryGetValue(f.StockPostingId,
                    out var owner)
                || owner.CompanyCode != companyCode || owner.BranchCode != branchCode
                || owner.SealedAtUtc is null || owner.LedgerEpochId != f.LedgerEpochId))
            throw Corrupt($"Item {itemCode} has an unsealed or foreign active valuation fact.");

        var events = new List<StockReplayWacEvent>(physicalGroups.Length);
        foreach (var group in physicalGroups)
        {
            var legs = group.ToArray();
            var key = legs[0].Event.Key;
            if (legs.Any(l => l.Event.Key != key)
                || legs.Select(l => l.PileId).Distinct().Count() != legs.Length)
                throw Corrupt($"Physical event {group.Key} has inconsistent pile keys.");
            if (group.Key > 0)
            {
                var history = historyById[checked((int)group.Key)];
                if (history.CompanyCode != companyCode || history.BranchCode != branchCode
                    || history.ICode != itemCode
                    || !PhysicalLegsMatch(history, legs.Select(l =>
                        (l.PileId, l.Event.SignedDelta)).ToArray()))
                    throw Corrupt($"History {history.Id} does not match the item pile events.");
                var originalId = ParseHistoryIdentity(key.LineIdentity);
                events.Add(StockReplayWacHistoryEventFactory.Create(
                    history, originalId, key, activeFacts[history.Id]));
            }
            else
            {
                if (legs.Length != 1
                    || !generationById.TryGetValue(-group.Key, out var generation)
                    || !countLineById.TryGetValue(generation.StockCountLineId, out var line)
                    || line.CompanyCode != companyCode || line.BranchCode != branchCode
                    || line.ICode != itemCode || line.BalLocId != legs[0].PileId)
                    throw Corrupt($"Count event {group.Key} is not on the counted item pile.");
                var facts = generation.AdjustmentHistoryId is int countHistoryId
                    ? activeFacts[countHistoryId] : [];
                StockValuationFact? originalCountFact = null;
                if (facts.Count == 1
                    && facts[0].StockPostingId != generation.WritePostingId)
                {
                    var matches = originalCountFacts.Where(f =>
                        f.InventoryHistoryId == generation.AdjustmentHistoryId
                        && f.StockPostingId == generation.WritePostingId).ToArray();
                    if (matches.Length != 1)
                        throw Corrupt($"Count generation {generation.Id} has no unique original valuation fact.");
                    originalCountFact = matches[0];
                }
                events.Add(StockReplayWacCountEventFactory.Create(
                    legs[0].Event, generation, facts,
                    basisByLine.GetValueOrDefault(line.Id), originalCountFact));
            }
        }

        var valueRows = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.CompanyCode == companyCode && f.BranchCode == branchCode
                && f.ItemCode == itemCode && f.InventoryHistoryId == null
                && f.EffectiveAt >= after.EffectiveAt.Date
                && f.StockPosting!.SealedAtUtc != null)
            .ToArrayAsync(cancellationToken);
        var valueIds = valueRows.Where(f => f.ReversesValuationFactId is null)
            .Select(f => f.Id).ToArray();
        var reversalRows = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.ReversesValuationFactId.HasValue
                && valueIds.Contains(f.ReversesValuationFactId.Value))
            .ToArrayAsync(cancellationToken);
        var allReversals = valueRows.Where(f => f.ReversesValuationFactId is not null)
            .Concat(reversalRows).GroupBy(f => f.Id).Select(g => g.First()).ToArray();
        var baseById = valueRows.Where(f => f.ReversesValuationFactId is null)
            .ToDictionary(f => f.Id);
        if (allReversals.Any(r => r.ReversesValuationFactId is not long priorId
                || !baseById.TryGetValue(priorId, out var prior)
                || r.CompanyCode != prior.CompanyCode
                || r.BranchCode != prior.BranchCode || r.ItemCode != prior.ItemCode
                || r.Direction != -prior.Direction || r.BaseQty != prior.BaseQty
                || r.CostAmount != prior.CostAmount
                || r.ValuationStatus != StockValuationStatuses.Reversed)
            || allReversals.GroupBy(r => r.ReversesValuationFactId)
                .Any(g => g.Count() != 1))
            throw Corrupt($"Item {itemCode} has invalid value-only reversal lineage.");
        var reversedIds = allReversals.Select(r => r.ReversesValuationFactId!.Value)
            .ToHashSet();
        var activeValues = valueRows.Where(f =>
            f.ReversesValuationFactId is null && !reversedIds.Contains(f.Id)).ToArray();
        var postingIds = activeValues.Select(f => f.StockPostingId).Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var originalValueIds = activeValues
            .Where(f => f.OriginalValuationFactId.HasValue)
            .Select(f => f.OriginalValuationFactId!.Value).Distinct().ToArray();
        var originalValueFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => originalValueIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        var originalPostingIds = originalValueFacts.Values
            .Select(f => f.StockPostingId).Distinct().ToArray();
        var originalPostings = await db.StockPostings.AsNoTracking()
            .Where(p => originalPostingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var receiptOrigins = originalValueFacts.Values
            .Where(f => f.InventoryHistoryId.HasValue).ToArray();
        var receiptOriginIds = receiptOrigins.Select(f => f.Id).ToArray();
        var receiptHistoryIds = receiptOrigins
            .Select(f => f.InventoryHistoryId!.Value).Distinct().ToArray();
        var receiptHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => receiptHistoryIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        var reversedReceipts = (await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.ReversesValuationFactId.HasValue
                && receiptOriginIds.Contains(f.ReversesValuationFactId.Value))
            .Select(f => f.ReversesValuationFactId!.Value)
            .ToArrayAsync(cancellationToken)).ToHashSet();
        var reversedReceiptHistories = (await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.ReversesHistoryId.HasValue
                && receiptHistoryIds.Contains(h.ReversesHistoryId.Value))
            .Select(h => h.ReversesHistoryId!.Value)
            .ToArrayAsync(cancellationToken)).ToHashSet();
        foreach (var fact in activeValues)
        {
            if (!postings.TryGetValue(fact.StockPostingId, out var owner))
                throw Corrupt($"Value fact {fact.Id} has no sealed posting.");
            StockReplayWacEvent valueEvent;
            if (fact.OriginalValuationFactId is long originalId)
            {
                if (!originalValueFacts.TryGetValue(originalId, out var original)
                    || !originalPostings.TryGetValue(original.StockPostingId,
                        out var originalOwner))
                    throw Corrupt($"Value fact {fact.Id} has no original WAC owner.");
                if (original.InventoryHistoryId is int receiptHistoryId)
                {
                    if (reversedReceipts.Contains(originalId)
                        || reversedReceiptHistories.Contains(receiptHistoryId)
                        || !receiptHistories.TryGetValue(receiptHistoryId,
                            out var receiptHistory))
                        throw Corrupt($"Value fact {fact.Id} has no active receipt origin.");
                    valueEvent = StockReplayWacValueEventFactory.CreateReceiptRestatement(
                        fact, original, receiptHistory, originalOwner, owner);
                }
                else
                {
                    if (!reversedIds.Contains(originalId))
                        throw Corrupt($"Value fact {fact.Id} has no reversed original adjustment.");
                    valueEvent = StockReplayWacValueEventFactory.CreateCorrected(
                        fact, original, originalOwner, owner);
                }
            }
            else
                valueEvent = StockReplayWacValueEventFactory.Create(fact, owner);
            if (valueEvent.Key.CompareTo(after) > 0)
                events.Add(valueEvent);
        }

        var ordered = events.OrderBy(e => e.Key).ThenBy(e => e.EventId).ToArray();
        if (ordered.Select(e => e.EventId).Distinct().Count() != ordered.Length
            || ordered.Zip(ordered.Skip(1), (a, b) => a.Key == b.Key).Any(equal => equal))
            throw Corrupt($"Item {itemCode} has duplicate WAC event identity or key.");
        return new(state.OnHandBaseQty, state.InventoryValue, ordered, byPile,
            balances.ToDictionary(b => b.Id, b => b.StdQty));
    }

    private static bool PhysicalLegsMatch(
        IvTrxHistory history, IReadOnlyCollection<(int PileId, decimal Delta)> legs)
    {
        var expected = new Dictionary<int, decimal>();
        if (history.FromBalLocId is int from && history.FrStdQty is > 0m)
            expected[from] = -history.FrStdQty.Value;
        if (history.ToBalLocId is int to && history.ToStdQty is > 0m)
            expected[to] = expected.GetValueOrDefault(to) + history.ToStdQty.Value;
        return expected.Count == legs.Count
            && legs.All(l => expected.TryGetValue(l.PileId, out var delta)
                && delta == l.Delta);
    }

    private static int ParseHistoryIdentity(string lineIdentity)
    {
        if (lineIdentity.Length != 11 || lineIdentity[0] != 'H'
            || !int.TryParse(lineIdentity.AsSpan(1), out var id) || id <= 0
            || lineIdentity != $"H{id:D10}")
            throw Corrupt("An ordinary WAC event has no original history identity.");
        return id;
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
