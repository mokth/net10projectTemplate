using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Resolves the complete active valuation set for each active inventory history.
/// A FIFO issue can have several splits, and a transfer has both issue and receipt
/// legs; neither can be represented by a single "latest" valuation fact.
/// </summary>
public static class StockReplayValuationGenerationResolver
{
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<StockValuationFact>>> ResolveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<IvTrxHistory> activeHistories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(activeHistories);
        if (activeHistories.Count == 0)
            return new Dictionary<int, IReadOnlyList<StockValuationFact>>();

        var ids = activeHistories.Select(x => x.Id).Distinct().ToArray();
        if (ids.Length != activeHistories.Count || ids.Any(x => x <= 0)
            || activeHistories.Any(x => x.CompanyCode != companyCode || x.BranchCode != branchCode
                || x.LedgerVersion != 2 || x.EntryRole != "ORIGINAL"))
            throw Corrupt("Active history input has duplicate or invalid V2 tenant identity.");

        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.InventoryHistoryId.HasValue && ids.Contains(x.InventoryHistoryId.Value))
            .ToArrayAsync(cancellationToken);
        var factIds = facts.Select(x => x.Id).ToArray();
        var reversals = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode
                && x.ReversesValuationFactId.HasValue
                && factIds.Contains(x.ReversesValuationFactId.Value))
            .ToArrayAsync(cancellationToken);
        return ResolveActive(activeHistories, facts.Concat(reversals).ToArray());
    }

    public static IReadOnlyDictionary<int, IReadOnlyList<StockValuationFact>> ResolveActive(
        IReadOnlyCollection<IvTrxHistory> activeHistories,
        IReadOnlyCollection<StockValuationFact> facts)
    {
        ArgumentNullException.ThrowIfNull(activeHistories);
        ArgumentNullException.ThrowIfNull(facts);
        var result = new Dictionary<int, IReadOnlyList<StockValuationFact>>();
        var byId = new Dictionary<long, StockValuationFact>();
        foreach (var fact in facts)
        {
            if (fact.Id <= 0 || !byId.TryAdd(fact.Id, fact))
                throw Corrupt($"Valuation fact {fact.Id} has duplicate or missing identity.");
        }

        var reversalChildren = new Dictionary<long, StockValuationFact>();
        foreach (var reversal in facts.Where(x => x.ReversesValuationFactId is not null))
        {
            var priorId = reversal.ReversesValuationFactId!.Value;
            if (!byId.TryGetValue(priorId, out var prior)
                || !reversalChildren.TryAdd(priorId, reversal)
                || reversal.ValuationStatus != StockValuationStatuses.Reversed
                || reversal.CompanyCode != prior.CompanyCode || reversal.BranchCode != prior.BranchCode
                || reversal.ItemCode != prior.ItemCode
                || reversal.Direction != -prior.Direction
                || reversal.BaseQty != prior.BaseQty || reversal.CostAmount != prior.CostAmount)
                throw Corrupt($"Valuation fact {reversal.Id} is not an exact unique reversal of {priorId}.");
        }

        foreach (var history in activeHistories)
        {
            if (history.Id <= 0 || result.ContainsKey(history.Id)
                || history.LedgerVersion != 2 || history.EntryRole != "ORIGINAL")
                throw Corrupt($"History {history.Id} is not one unique active V2 original.");

            var active = facts.Where(x => x.InventoryHistoryId == history.Id
                    && x.ReversesValuationFactId is null
                    && !reversalChildren.ContainsKey(x.Id))
                .OrderBy(x => x.StockPostingId).ThenBy(x => x.PostingLineNo)
                .ThenBy(x => x.SplitOrdinal).ToArray();
            if (active.Length == 0 || active.Any(x => x.ValuationStatus != StockValuationStatuses.Valued
                    || x.CompanyCode != history.CompanyCode || x.BranchCode != history.BranchCode
                    || x.ItemCode != history.ICode || x.BaseQty <= 0m
                    || x.CostAmount < 0m || x.Direction is not (-1 or 1))
                || active.Select(x => x.CostMethod).Distinct(StringComparer.Ordinal).Count() != 1)
                throw Corrupt($"History {history.Id} has no complete, single-method active valuation generation.");

            var expectedOut = StockLedgerPrecision.Quantity(history.FrStdQty ?? 0m);
            var expectedIn = StockLedgerPrecision.Quantity(history.ToStdQty ?? 0m);
            var valuedOut = StockLedgerPrecision.Quantity(active.Where(x => x.Direction < 0).Sum(x => x.BaseQty));
            var valuedIn = StockLedgerPrecision.Quantity(active.Where(x => x.Direction > 0).Sum(x => x.BaseQty));
            if (valuedOut != expectedOut || valuedIn != expectedIn)
                throw Corrupt($"History {history.Id} valuation splits do not cover both physical legs.");
            if (expectedOut > 0m && expectedIn > 0m
                && StockLedgerPrecision.Money(active.Where(x => x.Direction < 0).Sum(x => x.CostAmount))
                    != StockLedgerPrecision.Money(active.Where(x => x.Direction > 0).Sum(x => x.CostAmount)))
                throw Corrupt($"History {history.Id} transfer valuation is not value neutral.");
            result.Add(history.Id, active);
        }
        return result;
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
