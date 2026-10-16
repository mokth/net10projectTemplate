using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
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

    /// <summary>
    /// A corrected zero-quantity adjustment stays at its original business
    /// owner's key. The replay root that wrote it supplies audit ownership,
    /// not a new chronological position.
    /// </summary>
    public static StockReplayWacEvent CreateCorrected(
        StockValuationFact active,
        StockValuationFact original,
        StockPosting originalOwner,
        StockPosting correctionOwner)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(originalOwner);
        ArgumentNullException.ThrowIfNull(correctionOwner);
        var originalEvent = Create(original, originalOwner);
        if (active.Id <= 0 || active.StockPostingId != correctionOwner.Id
            || correctionOwner.Id == originalOwner.Id
            || correctionOwner.SealedAtUtc is null
            || correctionOwner.CompanyCode != originalOwner.CompanyCode
            || correctionOwner.BranchCode != originalOwner.BranchCode
            || correctionOwner.LedgerEpochId != originalOwner.LedgerEpochId
            || active.CompanyCode != original.CompanyCode
            || active.BranchCode != original.BranchCode
            || active.LedgerEpochId != original.LedgerEpochId
            || active.OriginalValuationFactId != original.Id
            || active.ReversesValuationFactId is not null
            || active.InventoryHistoryId is not null || active.BaseQty != 0m
            || active.CostMethod != StockCostMethods.MovingAverage
            || active.ValuationStatus != StockValuationStatuses.Valued
            || active.ItemCode != original.ItemCode
            || active.EffectiveAt != original.EffectiveAt
            || active.BusinessDate != original.BusinessDate
            || active.PeriodKey != original.PeriodKey
            || active.SourceDocumentType != original.SourceDocumentType
            || active.SourceDocumentId != original.SourceDocumentId
            || active.SourceDocumentNo != original.SourceDocumentNo
            || active.SourceDocumentLine != original.SourceDocumentLine
            || active.SourceLineId != original.SourceLineId
            || active.WarehouseCode != original.WarehouseCode
            || active.LocationCode != original.LocationCode
            || active.LotId != original.LotId
            || active.LotNo != original.LotNo
            || active.ItemStatus != original.ItemStatus
            || active.BaseUom != original.BaseUom
            || active.MovementCode != original.MovementCode
            || active.BaseCurrency != original.BaseCurrency
            || active.Direction is not (-1 or 1)
            || active.CostAmount < 0m
            || StockLedgerPrecision.Money(active.CostAmount) != active.CostAmount)
            throw Corrupt($"Value fact {active.Id} has no valid original WAC adjustment lineage.");
        var signedValue = active.SignedValue;
        return originalEvent with
        {
            OldSignedValue = signedValue,
            FixedSignedValue = signedValue
        };
    }

    /// <summary>
    /// A cost-only correction of one unchanged physical receipt is ordered
    /// immediately after that receipt, under its business owner's sequence.
    /// </summary>
    public static StockReplayWacEvent CreateReceiptRestatement(
        StockValuationFact active,
        StockValuationFact receipt,
        IvTrxHistory receiptHistory,
        StockPosting receiptOwner,
        StockPosting correctionOwner)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(receiptHistory);
        ArgumentNullException.ThrowIfNull(receiptOwner);
        ArgumentNullException.ThrowIfNull(correctionOwner);
        if (receipt.Id <= 0 || receipt.Id >= long.MaxValue - int.MaxValue
            || receipt.InventoryHistoryId != receiptHistory.Id
            || receiptHistory.Id <= 0 || receiptHistory.LedgerVersion != 2
            || receiptHistory.EntryRole != "ORIGINAL"
            || receiptHistory.BatchStatus != "POSTED"
            || receiptHistory.ValuationStatus != StockValuationStatuses.Valued
            || receiptHistory.FrStdQty is > 0m
            || receiptHistory.ToStdQty is not > 0m
            || receiptHistory.StockPostingId != receiptOwner.Id
            || receipt.StockPostingId != receiptOwner.Id
            || receiptOwner.SealedAtUtc is null
            || receiptOwner.PostingSequence <= 0
            || receipt.EffectiveAt != receiptOwner.EffectiveAt
            || receipt.BusinessDate != receiptOwner.BusinessDate
            || receipt.PeriodKey != receiptOwner.PeriodKey
            || receipt.EffectiveAt != receiptHistory.TrxDtTime
            || receipt.Direction != 1 || receipt.BaseQty <= 0m
            || receipt.CostMethod != StockCostMethods.MovingAverage
            || receipt.ValuationStatus != StockValuationStatuses.Valued
            || receipt.ReversesValuationFactId is not null
            || receipt.CompanyCode != receiptHistory.CompanyCode
            || receipt.BranchCode != receiptHistory.BranchCode
            || receipt.ItemCode != receiptHistory.ICode
            || active.Id <= 0 || active.StockPostingId != correctionOwner.Id
            || correctionOwner.SealedAtUtc is null
            || correctionOwner.CompanyCode != receipt.CompanyCode
            || correctionOwner.BranchCode != receipt.BranchCode
            || correctionOwner.LedgerEpochId != receipt.LedgerEpochId
            || active.CompanyCode != receipt.CompanyCode
            || active.BranchCode != receipt.BranchCode
            || active.LedgerEpochId != receipt.LedgerEpochId
            || active.OriginalValuationFactId != receipt.Id
            || active.ReversesValuationFactId is not null
            || active.InventoryHistoryId is not null || active.BaseQty != 0m
            || active.CostMethod != StockCostMethods.MovingAverage
            || active.ValuationStatus != StockValuationStatuses.Valued
            || active.ItemCode != receipt.ItemCode
            || active.EffectiveAt != receipt.EffectiveAt
            || active.BusinessDate != receipt.BusinessDate
            || active.PeriodKey != receipt.PeriodKey
            || active.SourceLineId != receipt.SourceLineId
            || active.SourceDocumentType != receipt.SourceDocumentType
            || active.SourceDocumentId != receipt.SourceDocumentId
            || active.SourceDocumentNo != receipt.SourceDocumentNo
            || active.SourceDocumentLine != receipt.SourceDocumentLine
            || active.BaseCurrency != receipt.BaseCurrency
            || active.Direction is not (-1 or 1)
            || active.CostAmount < 0m
            || StockLedgerPrecision.Money(active.CostAmount) != active.CostAmount)
            throw Corrupt($"Value fact {active.Id} has no unchanged WAC receipt authority.");
        var signedValue = active.SignedValue;
        return new StockReplayWacEvent(long.MaxValue - receipt.Id,
            new StockReplayEventKey(receipt.EffectiveAt,
                StockReplayEventKey.OrdinaryClass,
                receiptOwner.PostingSequence,
                $"H{receiptHistory.Id:D10}", 1),
            0m, signedValue, 0m, FixedSignedValue: signedValue);
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
