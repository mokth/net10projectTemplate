using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Replaces one Sales-return cost generation after its exact sale is repriced.
/// The receipt root owns the transaction, validated document and state update.
/// </summary>
public static class StockReplayWacSalesReturnRepricingWriter
{
    public static async Task<bool> AppendAsync(
        StockPostingContext context, StockReplayWacEvent movement,
        StockReplayWacStep step, IReadOnlyList<StockValuationFact> saleReplacements,
        int reversalLine, int replacementLine,
        CancellationToken cancellationToken = default)
    {
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || movement.EventId is <= 0 or > int.MaxValue
            || movement.ReturnOfSaleEventId is not long saleHistoryId
            || movement.SalesReturnAllocationId is not long allocationId
            || step.EventId != movement.EventId
            || step.SignedQuantity != movement.OldSignedQuantity
            || step.SignedQuantity <= 0m || step.SignedValue < 0m
            || saleReplacements.Count > 1)
            throw Invalid("WAC Sales return repricing has invalid event or root evidence.");
        if (saleReplacements.Count == 0)
        {
            if (step.SignedValue != movement.OldSignedValue)
                throw Invalid("A WAC Sales return changed without a repriced sale.");
            return false;
        }

        var db = context.Db;
        var returnHistoryId = checked((int)movement.EventId);
        var saleHistory = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == saleHistoryId
                && h.CompanyCode == context.CompanyCode && h.BranchCode == context.BranchCode,
                cancellationToken);
        var returned = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == returnHistoryId
                && h.CompanyCode == context.CompanyCode && h.BranchCode == context.BranchCode,
                cancellationToken);
        if (saleHistory is null || saleHistory.TrxType != IvTrxTypes.SalesOut
            || returned is null || returned.TrxType != IvTrxTypes.CustomerReturn
            || returned.ICode != saleHistory.ICode)
            throw Invalid("The WAC Sales return histories are inconsistent.");
        var current = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, [saleHistory, returned],
            cancellationToken);
        if (current[saleHistory.Id].Count != 1 || current[returned.Id].Count != 1)
            throw Invalid("The WAC Sales return needs one sale and return fact.");
        var oldSale = current[saleHistory.Id][0];
        var oldReturn = current[returned.Id][0];
        var newSale = saleReplacements[0];
        var allocation = await db.SalesReturnCostAllocations.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == allocationId
                && a.CompanyCode == context.CompanyCode && a.BranchCode == context.BranchCode,
                cancellationToken);
        if (allocation is null || allocation.ReversesAllocationId is not null
            || allocation.OriginalValuationFactId != oldSale.Id
            || allocation.ReturnValuationFactId != oldReturn.Id
            || allocation.StockPostingId != oldReturn.StockPostingId
            || allocation.ReturnedBaseQty != oldReturn.BaseQty
            || allocation.ReturnedCostAmount != oldReturn.CostAmount
            || await db.SalesReturnCostAllocations.AsNoTracking().AnyAsync(a =>
                a.ReversesAllocationId == allocation.Id, cancellationToken)
            || oldReturn.MovementCode != "SALE_RETURN_IN"
            || oldReturn.ValuationSource != StockValuationSources.OriginalSaleReturn
            || !HasSupportedCostEvidence(oldReturn)
            || oldReturn.BaseCostAmount != oldReturn.CostAmount
            || oldReturn.BaseQty != step.SignedQuantity
            || oldReturn.CostAmount != movement.OldSignedValue
            || newSale.StockPostingId != context.Posting.Id
            || newSale.InventoryHistoryId != saleHistory.Id
            || newSale.Direction != -1 || newSale.BaseQty != oldSale.BaseQty
            || (newSale.OriginalValuationFactId ?? newSale.Id)
                != (oldSale.OriginalValuationFactId ?? oldSale.Id)
            || step.SignedValue != StockLedgerPrecision.Money(
                newSale.CostAmount * oldReturn.BaseQty / newSale.BaseQty))
            throw Invalid("The WAC Sales return allocation changed after planning.");

        var reversals = await StockReplayValuationReversalWriter.AppendAsync(context,
            returnHistoryId, [new StockReplayValuationReversalLine(
                oldReturn.Id, reversalLine, 0)], cancellationToken);
        var reversal = reversals.Single();
        var now = DateTime.UtcNow;
        var replacement = new StockValuationFact
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id, StockPostingId = context.Posting.Id,
            PostingLineNo = replacementLine, SplitOrdinal = 0,
            SourceLineId = oldReturn.SourceLineId,
            SourceDocumentType = oldReturn.SourceDocumentType,
            SourceDocumentId = oldReturn.SourceDocumentId,
            SourceDocumentNo = oldReturn.SourceDocumentNo,
            SourceDocumentLine = oldReturn.SourceDocumentLine,
            EffectiveAt = oldReturn.EffectiveAt, BusinessDate = oldReturn.BusinessDate,
            PeriodKey = oldReturn.PeriodKey, ItemCode = oldReturn.ItemCode,
            WarehouseCode = oldReturn.WarehouseCode, LocationCode = oldReturn.LocationCode,
            LotId = oldReturn.LotId, LotNo = oldReturn.LotNo, ItemStatus = oldReturn.ItemStatus,
            BaseUom = oldReturn.BaseUom, MovementCode = oldReturn.MovementCode,
            Direction = 1, BaseQty = oldReturn.BaseQty,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = StockLedgerPrecision.Money(step.SignedValue / oldReturn.BaseQty),
            CostAmount = step.SignedValue, BaseCurrency = oldReturn.BaseCurrency,
            TransactionCurrency = oldReturn.TransactionCurrency,
            TransactionCostAmount = oldReturn.TransactionCostAmount,
            ExchangeRate = oldReturn.TransactionCostAmount is > 0m
                ? Rate(step.SignedValue / oldReturn.TransactionCostAmount.Value) : null,
            BaseCostAmount = step.SignedValue,
            ValuationSource = StockValuationSources.OriginalSaleReturn,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = checked(oldReturn.ValuationVersion + 1),
            InventoryHistoryId = oldReturn.InventoryHistoryId,
            FromBalLocId = oldReturn.FromBalLocId, ToBalLocId = oldReturn.ToBalLocId,
            ProductionMovementId = oldReturn.ProductionMovementId,
            WorkOrderId = oldReturn.WorkOrderId,
            WorkOrderOperationId = oldReturn.WorkOrderOperationId,
            ProductionPostingLinkId = oldReturn.ProductionPostingLinkId,
            OriginalValuationFactId = oldReturn.OriginalValuationFactId ?? oldReturn.Id,
            CreatedAtUtc = now, CreatedBy = context.UserId
        };
        db.StockValuationFacts.Add(replacement);
        db.SalesReturnCostAllocations.Add(new SalesReturnCostAllocation
        {
            CompanyCode = allocation.CompanyCode, BranchCode = allocation.BranchCode,
            ReturnDocumentType = allocation.ReturnDocumentType,
            ReturnDocumentNo = allocation.ReturnDocumentNo,
            ReturnDocumentLine = allocation.ReturnDocumentLine,
            ReturnCostingRevision = allocation.ReturnCostingRevision,
            OriginalValuationFactId = allocation.OriginalValuationFactId,
            OriginalOwnerType = allocation.OriginalOwnerType,
            OriginalOwnerDocumentNo = allocation.OriginalOwnerDocumentNo,
            OriginalOwnerDocumentLine = allocation.OriginalOwnerDocumentLine,
            ReturnedBaseQty = allocation.ReturnedBaseQty,
            ReturnedCostAmount = allocation.ReturnedCostAmount,
            StockPostingId = context.Posting.Id, ReturnValuationFact = reversal,
            ReversesAllocationId = allocation.Id,
            CreatedAtUtc = now, CreatedBy = context.UserId
        });
        db.SalesReturnCostAllocations.Add(new SalesReturnCostAllocation
        {
            CompanyCode = allocation.CompanyCode, BranchCode = allocation.BranchCode,
            ReturnDocumentType = allocation.ReturnDocumentType,
            ReturnDocumentNo = allocation.ReturnDocumentNo,
            ReturnDocumentLine = allocation.ReturnDocumentLine,
            ReturnCostingRevision = allocation.ReturnCostingRevision,
            OriginalValuationFact = newSale,
            OriginalOwnerType = allocation.OriginalOwnerType,
            OriginalOwnerDocumentNo = allocation.OriginalOwnerDocumentNo,
            OriginalOwnerDocumentLine = allocation.OriginalOwnerDocumentLine,
            ReturnedBaseQty = allocation.ReturnedBaseQty,
            ReturnedCostAmount = step.SignedValue,
            StockPostingId = context.Posting.Id, ReturnValuationFact = replacement,
            CreatedAtUtc = now, CreatedBy = context.UserId
        });
        return true;
    }

    // TransactionCostAmount records the return document's price evidence. The
    // rate relates that evidence to each immutable generation's actual value.
    internal static bool HasSupportedCostEvidence(StockValuationFact fact) =>
        fact.TransactionCurrency is null
        && fact.TransactionCostAmount is not < 0m
        && (fact.TransactionCostAmount is > 0m
            ? fact.ExchangeRate == Rate(fact.CostAmount / fact.TransactionCostAmount.Value)
            : fact.ExchangeRate is null);

    private static decimal Rate(decimal value) =>
        decimal.Round(value, 8, MidpointRounding.AwayFromZero);

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
