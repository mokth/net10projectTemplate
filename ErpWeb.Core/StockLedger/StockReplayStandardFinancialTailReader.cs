using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayStandardFinancialTail(
    decimal CurrentQuantity,
    decimal CurrentValue,
    IReadOnlyList<StockReplayStandardEvent> LaterEvents,
    IReadOnlyDictionary<int, IReadOnlyList<StockReplayPhysicalEvent>> PhysicalByPile,
    IReadOnlyDictionary<int, decimal> CurrentQuantityByPile);

/// <summary>
/// Read-only Standard tail. Physical count resets use their canonical keys;
/// ordinary histories require a complete active Standard valuation generation.
/// Unsupported value-only effects fail closed before a replay writer can run.
/// </summary>
public static class StockReplayStandardFinancialTailReader
{
    public static async Task<StockReplayStandardFinancialTail> ReadLaterAsync(
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
                && s.BranchCode == branchCode && s.ItemCode == itemCode
                && s.CostMethod == StockCostMethods.Standard)
            .ToArrayAsync(cancellationToken);
        if (states.Length != 1)
            throw Invalid($"Item {itemCode} has no unique Standard cost state.");
        var state = states[0];
        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.ICode == itemCode)
            .Select(b => new { b.Id, b.StdQty })
            .ToArrayAsync(cancellationToken);
        if (balances.Any(b => b.Id <= 0 || b.StdQty < 0m
                || IvQty.Round(b.StdQty) != b.StdQty)
            || StockLedgerPrecision.Quantity(balances.Sum(b => b.StdQty))
                != state.OnHandBaseQty
            || state.OnHandBaseQty < 0m || state.InventoryValue < 0m
            || state.OnHandBaseQty == 0m && state.InventoryValue != 0m)
            throw Invalid($"Item {itemCode} Standard state differs from its piles.");

        var byPile = new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>();
        foreach (var balance in balances.OrderBy(b => b.Id))
            byPile.Add(balance.Id,
                await StockReplayPhysicalTimelineReader.ReadLaterAsync(db,
                    companyCode, branchCode, balance.Id, after,
                    cancellationToken));
        var groups = byPile.SelectMany(p => p.Value.Select(e =>
                (PileId: p.Key, Event: e)))
            .GroupBy(x => x.Event.EventId).ToArray();
        var ordinaryIds = groups.Where(g => g.Key > 0)
            .Select(g => checked((int)g.Key)).ToArray();
        var countIds = groups.Where(g => g.Key < 0)
            .Select(g => -g.Key).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ordinaryIds.Contains(h.Id))
            .ToArrayAsync(cancellationToken);
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => countIds.Contains(g.Id))
            .ToArrayAsync(cancellationToken);
        if (histories.Length != ordinaryIds.Length
            || generations.Length != countIds.Length)
            throw Invalid($"Item {itemCode} has an unresolved physical event.");
        var historyById = histories.ToDictionary(h => h.Id);
        var generationById = generations.ToDictionary(g => g.Id);
        var countLineIds = generations.Select(g => g.StockCountLineId).ToArray();
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => countLineIds.Contains(l.Id))
            .Select(l => new { l.Id, l.BalLocId, l.ICode,
                l.Header.CompanyCode, l.Header.BranchCode })
            .ToArrayAsync(cancellationToken);
        if (lines.Length != countLineIds.Length)
            throw Invalid($"Item {itemCode} has an unresolved count line.");
        var lineById = lines.ToDictionary(l => l.Id);
        var bases = await db.StockCountCostBases.AsNoTracking()
            .Where(b => countLineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        if (bases.GroupBy(b => b.StockCountLineId).Any(g => g.Count() != 1))
            throw Invalid($"Item {itemCode} has duplicate count bases.");
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);
        var countHistoryIds = generations.Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).ToArray();
        var countHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => countHistoryIds.Contains(h.Id))
            .ToArrayAsync(cancellationToken);
        if (countHistories.Length != countHistoryIds.Length
            || countHistories.Any(h => h.ValuationStatus
                != StockValuationStatuses.Valued))
            throw Invalid($"Item {itemCode} has an unvalued count history.");
        var activeFacts = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, companyCode, branchCode,
            histories.Concat(countHistories).ToArray(), cancellationToken);
        var factRows = activeFacts.Values.SelectMany(x => x).ToArray();
        var postingIds = factRows.Select(f => f.StockPostingId).Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (factRows.Any(f => !postings.TryGetValue(f.StockPostingId,
                out var owner) || owner.SealedAtUtc is null
                || owner.CompanyCode != companyCode
                || owner.BranchCode != branchCode
                || owner.LedgerEpochId != f.LedgerEpochId))
            throw Invalid($"Item {itemCode} has an unsealed active fact.");
        var correctedCountHistoryIds = generations
            .Where(g => g.AdjustmentHistoryId is int id
                && activeFacts[id].Count == 1
                && activeFacts[id][0].StockPostingId != g.WritePostingId)
            .Select(g => g.AdjustmentHistoryId!.Value).ToArray();
        var originalCountFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.InventoryHistoryId.HasValue
                && correctedCountHistoryIds.Contains(f.InventoryHistoryId.Value)
                && f.OriginalValuationFactId == null
                && f.ReversesValuationFactId == null)
            .ToArrayAsync(cancellationToken);

        var resolver = new ItemStandardCostResolver();
        var revisions = new Dictionary<DateTime, EffectiveStandardCost>();
        async Task<EffectiveStandardCost> RevisionAt(DateTime at)
        {
            var day = at.Date;
            if (!revisions.TryGetValue(day, out var revision))
            {
                revision = await resolver.ResolveAsync(db, companyCode,
                    branchCode, itemCode, day, cancellationToken);
                revisions.Add(day, revision);
            }
            return revision;
        }

        var events = new List<StockReplayStandardEvent>();
        foreach (var group in groups)
        {
            var legs = group.ToArray();
            var key = legs[0].Event.Key;
            if (legs.Any(l => l.Event.Key != key)
                || legs.Select(l => l.PileId).Distinct().Count()
                    != legs.Length)
                throw Invalid($"Physical event {group.Key} has inconsistent piles.");
            var revision = await RevisionAt(key.EffectiveAt);
            if (group.Key > 0)
            {
                var history = historyById[checked((int)group.Key)];
                if (history.CompanyCode != companyCode
                    || history.BranchCode != branchCode
                    || history.ICode != itemCode
                    || !PhysicalLegsMatch(history, legs.Select(l =>
                        (l.PileId, l.Event.SignedDelta)).ToArray()))
                    throw Invalid($"History {history.Id} differs from its physical legs.");
                events.Add(StockReplayStandardHistoryEventFactory.Create(
                    history, ParseHistoryIdentity(key.LineIdentity), key,
                    activeFacts[history.Id], revision));
            }
            else
            {
                var generation = generationById[-group.Key];
                if (legs.Length != 1
                    || !lineById.TryGetValue(generation.StockCountLineId,
                        out var line)
                    || line.CompanyCode != companyCode
                    || line.BranchCode != branchCode
                    || line.ICode != itemCode
                    || line.BalLocId != legs[0].PileId)
                    throw Invalid($"Count {generation.Id} differs from its pile.");
                var facts = generation.AdjustmentHistoryId is int id
                    ? activeFacts[id] : [];
                StockValuationFact? original = null;
                if (facts.Count == 1
                    && facts[0].StockPostingId != generation.WritePostingId)
                {
                    var matches = originalCountFacts.Where(f =>
                        f.InventoryHistoryId == generation.AdjustmentHistoryId
                        && f.StockPostingId == generation.WritePostingId)
                        .ToArray();
                    if (matches.Length != 1)
                        throw Invalid($"Count {generation.Id} has no original fact.");
                    original = matches[0];
                }
                events.Add(StockReplayStandardCountEventFactory.CreateActive(
                    legs[0].Event, generation, itemCode, revision,
                    facts, basisByLine.GetValueOrDefault(line.Id), original));
            }
        }

        var valueRows = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.CompanyCode == companyCode
                && f.BranchCode == branchCode && f.ItemCode == itemCode
                && f.InventoryHistoryId == null
                && f.EffectiveAt >= after.EffectiveAt.Date
                && f.StockPosting!.SealedAtUtc != null)
            .ToArrayAsync(cancellationToken);
        var valuesById = valueRows.ToDictionary(f => f.Id);
        var reversals = valueRows.Where(f => f.ReversesValuationFactId.HasValue)
            .ToArray();
        if (reversals.Any(r => r.ReversesValuationFactId is not long id
                || !valuesById.TryGetValue(id, out var prior)
                || prior.ReversesValuationFactId is not null
                || r.CompanyCode != prior.CompanyCode
                || r.BranchCode != prior.BranchCode
                || r.ItemCode != prior.ItemCode
                || r.CostMethod != prior.CostMethod
                || r.EffectiveAt != prior.EffectiveAt
                || r.Direction != -prior.Direction
                || r.BaseQty != prior.BaseQty
                || r.CostAmount != prior.CostAmount
                || r.ValuationStatus != StockValuationStatuses.Reversed)
            || reversals.GroupBy(r => r.ReversesValuationFactId)
                .Any(g => g.Count() != 1))
            throw Invalid($"Item {itemCode} has invalid value-only reversals.");
        var reversedIds = reversals.Select(r =>
            r.ReversesValuationFactId!.Value).ToHashSet();
        var activeValues = valueRows.Where(f =>
            f.ReversesValuationFactId is null
            && !reversedIds.Contains(f.Id)).ToArray();
        var valuePostingIds = activeValues.Select(f => f.StockPostingId)
            .Concat(activeValues.Where(f => f.OriginalValuationFactId.HasValue)
                .Select(f => valuesById.GetValueOrDefault(
                    f.OriginalValuationFactId!.Value)?.StockPostingId ?? 0))
            .Distinct().ToArray();
        var valuePostings = await db.StockPostings.AsNoTracking()
            .Where(p => valuePostingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        foreach (var fact in activeValues)
        {
            if (!valuePostings.TryGetValue(fact.StockPostingId,
                    out var owner) || owner.SealedAtUtc is null)
                throw Invalid($"Value fact {fact.Id} has no sealed owner.");
            var revision = await RevisionAt(fact.EffectiveAt);
            StockReplayStandardEvent valueEvent;
            if (fact.OriginalValuationFactId is long originalId)
            {
                if (!reversedIds.Contains(originalId)
                    || !valuesById.TryGetValue(originalId, out var original)
                    || !valuePostings.TryGetValue(original.StockPostingId,
                        out var originalOwner))
                    throw Invalid($"Value fact {fact.Id} has no reversed original.");
                valueEvent = StockReplayStandardRevaluationEventFactory
                    .CreateCorrected(fact, original, originalOwner,
                        owner, revision);
            }
            else
                valueEvent = StockReplayStandardRevaluationEventFactory
                    .Create(fact, owner, revision);
            if (valueEvent.Key.CompareTo(after) > 0)
                events.Add(valueEvent);
        }
        var ordered = events.OrderBy(e => e.Key).ThenBy(e => e.EventId)
            .ToArray();
        if (ordered.Select(e => e.EventId).Distinct().Count()
                != ordered.Length
            || ordered.Zip(ordered.Skip(1), (a, b) =>
                a.Key == b.Key).Any(equal => equal))
            throw Invalid($"Item {itemCode} has duplicate Standard event keys.");
        return new(state.OnHandBaseQty, state.InventoryValue,
            ordered, byPile, balances.ToDictionary(b => b.Id, b => b.StdQty));
    }

    private static bool PhysicalLegsMatch(
        ErpWeb.Model.Entities.Inventory.IvTrxHistory history,
        IReadOnlyCollection<(int PileId, decimal Delta)> legs)
    {
        var expected = new Dictionary<int, decimal>();
        if (history.FromBalLocId is int from && history.FrStdQty is > 0m)
            expected[from] = -history.FrStdQty.Value;
        if (history.ToBalLocId is int to && history.ToStdQty is > 0m)
            expected[to] = expected.GetValueOrDefault(to)
                + history.ToStdQty.Value;
        return expected.Count == legs.Count
            && legs.All(l => expected.TryGetValue(l.PileId,
                out var delta) && delta == l.Delta);
    }

    private static int ParseHistoryIdentity(string identity)
    {
        if (identity.Length != 11 || identity[0] != 'H'
            || !int.TryParse(identity.AsSpan(1), out var id)
            || id <= 0 || identity != $"H{id:D10}")
            throw Invalid("A Standard movement has no original history identity.");
        return id;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
