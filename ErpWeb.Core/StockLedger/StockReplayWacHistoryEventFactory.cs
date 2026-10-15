using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Converts one active ordinary Inventory history and its complete active WAC
/// valuation generation into an item-level replay event. A transfer has two
/// physical legs but one net-zero item-level event.
/// </summary>
public static class StockReplayWacHistoryEventFactory
{
    public static StockReplayWacEvent Create(
        IvTrxHistory history,
        int originalHistoryId,
        StockReplayEventKey key,
        IReadOnlyCollection<StockValuationFact> activeFacts)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(activeFacts);
        if (history.Id <= 0 || originalHistoryId <= 0
            || history.LedgerVersion != 2 || history.EntryRole != "ORIGINAL"
            || history.BatchStatus != "POSTED" || history.StockPostingId is not > 0
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.LedgerEpochId is not > 0
            || history.StockCountCostBasisId is not null
            || string.IsNullOrWhiteSpace(history.ICode)
            || key.ClassPriority != StockReplayEventKey.OrdinaryClass
            || key.AttachedSuborder != 0 || key.OriginalPostingSequence <= 0
            || key.LineIdentity != $"H{originalHistoryId:D10}"
            || key.EffectiveAt != history.TrxDtTime)
            throw Corrupt($"History {history.Id} has invalid WAC event ownership.");

        var fromQty = StockLedgerPrecision.Quantity(history.FrStdQty ?? 0m);
        var toQty = StockLedgerPrecision.Quantity(history.ToStdQty ?? 0m);
        if (activeFacts.Count == 0 || fromQty < 0m || toQty < 0m
            || fromQty == 0m && toQty == 0m
            || fromQty > 0m != (history.FromBalLocId is > 0)
            || toQty > 0m != (history.ToBalLocId is > 0)
            || IvQty.Round(fromQty) != fromQty || IvQty.Round(toQty) != toQty)
            throw Corrupt($"History {history.Id} has invalid physical WAC legs.");

        if (activeFacts.Any(f => f.Id <= 0 || f.StockPostingId <= 0
            || f.LedgerEpochId != history.LedgerEpochId
            || f.CompanyCode != history.CompanyCode || f.BranchCode != history.BranchCode
            || f.InventoryHistoryId != history.Id || f.ItemCode != history.ICode
            || f.EffectiveAt != key.EffectiveAt
            || f.CostMethod != StockCostMethods.MovingAverage
            || f.ValuationStatus != StockValuationStatuses.Valued
            || f.ReversesValuationFactId is not null
            || f.Direction is not (-1 or 1) || f.BaseQty <= 0m
            || f.CostAmount < 0m
            || StockLedgerPrecision.Quantity(f.BaseQty) != f.BaseQty
            || StockLedgerPrecision.Money(f.CostAmount) != f.CostAmount)
            || activeFacts.Select(f => f.Id).Distinct().Count() != activeFacts.Count)
            throw Corrupt($"History {history.Id} has invalid active WAC facts.");

        var valuedOut = StockLedgerPrecision.Quantity(
            activeFacts.Where(f => f.Direction < 0).Sum(f => f.BaseQty));
        var valuedIn = StockLedgerPrecision.Quantity(
            activeFacts.Where(f => f.Direction > 0).Sum(f => f.BaseQty));
        var outValue = StockLedgerPrecision.Money(
            activeFacts.Where(f => f.Direction < 0).Sum(f => f.CostAmount));
        var inValue = StockLedgerPrecision.Money(
            activeFacts.Where(f => f.Direction > 0).Sum(f => f.CostAmount));
        if (valuedOut != fromQty || valuedIn != toQty
            || fromQty > 0m && toQty > 0m
                && (fromQty != toQty || outValue != inValue))
            throw Corrupt($"History {history.Id} WAC generation does not reconcile to both legs.");

        var signedQty = StockLedgerPrecision.Quantity(toQty - fromQty);
        var signedValue = StockLedgerPrecision.Money(inValue - outValue);
        if (signedQty > 0m && signedValue < 0m
            || signedQty < 0m && signedValue > 0m
            || signedQty == 0m && signedValue != 0m)
            throw Corrupt($"History {history.Id} WAC direction is inconsistent.");
        return new StockReplayWacEvent(history.Id, key,
            signedQty, signedValue, signedQty,
            FixedSignedValue: signedQty >= 0m ? signedValue : null);
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
