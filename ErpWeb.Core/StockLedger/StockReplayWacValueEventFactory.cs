using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Maps one active, original zero-quantity WAC adjustment to its own event key.
/// A later cost restatement needs separate business-owner lineage and is not
/// treated as a new original adjustment by this adapter.
/// </summary>
public static class StockReplayWacValueEventFactory
{
    public static StockReplayWacEvent Create(
        StockValuationFact fact, StockPosting owner)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(owner);
        if (fact.Id <= 0 || fact.Id >= long.MaxValue - int.MaxValue
            || owner.Id <= 0 || owner.SealedAtUtc is null
            || owner.PostingSequence <= 0
            || fact.StockPostingId != owner.Id
            || fact.CompanyCode != owner.CompanyCode
            || fact.BranchCode != owner.BranchCode
            || fact.LedgerEpochId != owner.LedgerEpochId
            || fact.EffectiveAt != owner.EffectiveAt
            || fact.BusinessDate != owner.BusinessDate
            || fact.PeriodKey != owner.PeriodKey
            || string.IsNullOrWhiteSpace(fact.ItemCode)
            || fact.InventoryHistoryId is not null || fact.BaseQty != 0m
            || fact.CostMethod != StockCostMethods.MovingAverage
            || fact.ValuationStatus != StockValuationStatuses.Valued
            || fact.OriginalValuationFactId is not null
            || fact.ReversesValuationFactId is not null
            || fact.Direction is not (-1 or 1)
            || fact.CostAmount < 0m
            || StockLedgerPrecision.Money(fact.CostAmount) != fact.CostAmount)
            throw Corrupt($"Value fact {fact.Id} has no valid original WAC owner.");
        var signedValue = fact.SignedValue;
        // Positive int IDs identify histories and negative IDs identify counts.
        // Reserve the upper long range for value-only fact events.
        return new StockReplayWacEvent(long.MaxValue - fact.Id,
            new StockReplayEventKey(owner.EffectiveAt,
                StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
                $"V{fact.Id:D20}", 0),
            0m, signedValue, 0m, FixedSignedValue: signedValue);
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
