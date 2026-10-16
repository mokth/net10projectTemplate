using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Converts one active ordinary Inventory history and its complete active
/// Standard valuation generation into an item-level replay movement.
/// </summary>
public static class StockReplayStandardHistoryEventFactory
{
    public static StockReplayStandardEvent Create(
        IvTrxHistory history,
        int originalHistoryId,
        StockReplayEventKey key,
        IReadOnlyCollection<StockValuationFact> activeFacts,
        EffectiveStandardCost revision)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(activeFacts);
        ArgumentNullException.ThrowIfNull(revision);
        if (history.Id <= 0 || originalHistoryId <= 0
            || history.LedgerVersion != 2 || history.EntryRole != "ORIGINAL"
            || history.BatchStatus != "POSTED"
            || history.StockPostingId is not > 0
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.LedgerEpochId is not > 0
            || history.StockCountCostBasisId is not null
            || string.IsNullOrWhiteSpace(history.ICode)
            || key.ClassPriority != StockReplayEventKey.OrdinaryClass
            || key.AttachedSuborder != 0
            || key.OriginalPostingSequence <= 0
            || key.LineIdentity != $"H{originalHistoryId:D10}"
            || key.EffectiveAt != history.TrxDtTime
            || revision.Id <= 0 || revision.ItemCode != history.ICode
            || revision.EffectiveFrom.Date > key.EffectiveAt.Date
            || revision.EffectiveTo is DateTime end
                && end.Date <= key.EffectiveAt.Date)
            throw Corrupt($"History {history.Id} has invalid Standard event ownership.");

        var fromQty = StockLedgerPrecision.Quantity(history.FrStdQty ?? 0m);
        var toQty = StockLedgerPrecision.Quantity(history.ToStdQty ?? 0m);
        var expectedLegs = (fromQty > 0m ? 1 : 0) + (toQty > 0m ? 1 : 0);
        if (expectedLegs == 0 || activeFacts.Count != expectedLegs
            || fromQty < 0m || toQty < 0m
            || IvQty.Round(fromQty) != fromQty
            || IvQty.Round(toQty) != toQty
            || (fromQty > 0m) != (history.FromBalLocId is > 0)
            || (toQty > 0m) != (history.ToBalLocId is > 0))
            throw Corrupt($"History {history.Id} has incomplete Standard physical legs.");

        if (activeFacts.Select(f => f.Id).Distinct().Count() != expectedLegs
            || activeFacts.Any(f => f.Id <= 0 || f.StockPostingId <= 0
                || f.LedgerEpochId != history.LedgerEpochId
                || f.CompanyCode != history.CompanyCode
                || f.BranchCode != history.BranchCode
                || f.InventoryHistoryId != history.Id
                || f.ItemCode != history.ICode
                || f.EffectiveAt != key.EffectiveAt
                || f.CostMethod != StockCostMethods.Standard
                || f.ValuationStatus != StockValuationStatuses.Valued
                || f.ReversesValuationFactId is not null
                || f.Direction is not (-1 or 1)
                || f.BaseQty <= 0m
                || StockLedgerPrecision.Quantity(f.BaseQty) != f.BaseQty
                || f.CostAmount < 0m
                || StockLedgerPrecision.Money(f.CostAmount)
                    != f.CostAmount
                || f.CostAmount != StockLedgerPrecision.Money(
                    f.BaseQty * revision.TotalStandardCost))
            || activeFacts.Count(f => f.Direction < 0) != (fromQty > 0m ? 1 : 0)
            || activeFacts.Count(f => f.Direction > 0) != (toQty > 0m ? 1 : 0)
            || activeFacts.Where(f => f.Direction < 0)
                .Sum(f => f.BaseQty) != fromQty
            || activeFacts.Where(f => f.Direction > 0)
                .Sum(f => f.BaseQty) != toQty)
            throw Corrupt($"History {history.Id} has no exact Standard valuation generation.");

        var signedQuantity = StockLedgerPrecision.Quantity(toQty - fromQty);
        var signedValue = StockLedgerPrecision.Money(
            activeFacts.Sum(f => f.SignedValue));
        if (signedValue != StockLedgerPrecision.Money(
                signedQuantity * revision.TotalStandardCost))
            throw Corrupt($"History {history.Id} Standard legs are not value neutral.");
        return new StockReplayStandardEvent(history.Id, key,
            StockReplayStandardEventKinds.Movement,
            signedQuantity, signedValue, signedQuantity,
            revision.TotalStandardCost, revision.Id);
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
