using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Maps an active Standard revaluation fact to its original business key.
/// The replay simulator recomputes its delta from the replayed quantity.
/// </summary>
public static class StockReplayStandardRevaluationEventFactory
{
    public static StockReplayStandardEvent Create(
        StockValuationFact fact,
        StockPosting owner,
        EffectiveStandardCost revision)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(revision);
        if (fact.Id <= 0 || fact.Id >= long.MaxValue - int.MaxValue
            || owner.Id <= 0 || owner.SealedAtUtc is null
            || owner.PostingSequence <= 0
            || owner.SourceDocumentType != "STANDARD_COST_REVALUATION"
            || fact.StockPostingId != owner.Id
            || fact.CompanyCode != owner.CompanyCode
            || fact.BranchCode != owner.BranchCode
            || fact.LedgerEpochId != owner.LedgerEpochId
            || fact.EffectiveAt != owner.EffectiveAt
            || fact.BusinessDate != owner.BusinessDate
            || fact.PeriodKey != owner.PeriodKey
            || fact.ItemCode != revision.ItemCode
            || revision.Id <= 0
            || revision.EffectiveFrom.Date > fact.EffectiveAt.Date
            || revision.EffectiveTo is DateTime end
                && end.Date <= fact.EffectiveAt.Date
            || fact.InventoryHistoryId is not null
            || fact.BaseQty != 0m
            || fact.CostMethod != StockCostMethods.Standard
            || fact.ValuationSource != StockValuationSources.StandardRevaluation
            || fact.ValuationStatus != StockValuationStatuses.Valued
            || fact.OriginalValuationFactId is not null
            || fact.ReversesValuationFactId is not null
            || fact.Direction is not (-1 or 1)
            || fact.MovementCode != (fact.Direction > 0
                ? "STANDARD_REVALUE_IN" : "STANDARD_REVALUE_OUT")
            || fact.CostAmount < 0m
            || StockLedgerPrecision.Money(fact.CostAmount) != fact.CostAmount)
            throw Invalid($"Value fact {fact.Id} has no original Standard revaluation owner.");
        return new StockReplayStandardEvent(long.MaxValue - fact.Id,
            new StockReplayEventKey(owner.EffectiveAt,
                StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"V{fact.Id:D20}", 0),
            StockReplayStandardEventKinds.Revaluation,
            0m, fact.SignedValue, 0m,
            revision.TotalStandardCost, revision.Id);
    }

    public static StockReplayStandardEvent CreateCorrected(
        StockValuationFact active,
        StockValuationFact original,
        StockPosting originalOwner,
        StockPosting correctionOwner,
        EffectiveStandardCost revision)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(correctionOwner);
        var originalEvent = Create(original, originalOwner, revision);
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
            || active.CostMethod != StockCostMethods.Standard
            || active.ValuationSource != StockValuationSources.StandardRevaluation
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
            || active.BaseCurrency != original.BaseCurrency
            || active.MovementCode is not (
                "STANDARD_REVALUE_IN" or "STANDARD_REVALUE_OUT")
            || active.Direction is not (-1 or 1)
            || active.Direction != (active.MovementCode == "STANDARD_REVALUE_IN"
                ? 1 : -1)
            || active.CostAmount < 0m
            || StockLedgerPrecision.Money(active.CostAmount) != active.CostAmount)
            throw Invalid($"Value fact {active.Id} has no Standard revaluation lineage.");
        return originalEvent with { OldSignedValue = active.SignedValue };
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
