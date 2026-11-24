using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Replaces one WAC Production Stock Return receipt valuation generation after
/// its originating Material Issue to Production has been repriced in the same root.
/// </summary>
public static class StockReplayWacProductionReturnRepricingWriter
{
    public static async Task<bool> AppendAsync(
        StockPostingContext context, StockReplayWacEvent movement,
        StockReplayWacStep step, IReadOnlyList<StockValuationFact> issueReplacements,
        int reversalLine, int replacementLine,
        CancellationToken cancellationToken = default)
    {
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || movement.EventId is <= 0 or > int.MaxValue
            || movement.ReturnOfProductionIssueEventId is not long issueHistoryId
            || step.EventId != movement.EventId
            || step.SignedQuantity != movement.OldSignedQuantity
            || step.SignedQuantity <= 0m || step.SignedValue < 0m
            || issueReplacements.Count > 1)
            throw Invalid("WAC Production return repricing has invalid event or root evidence.");
        if (issueReplacements.Count == 0)
        {
            if (step.SignedValue != movement.OldSignedValue)
                throw Invalid("A WAC Production return changed without a repriced Production issue.");
            return false;
        }

        var db = context.Db;
        var returnHistoryId = checked((int)movement.EventId);
        var issueHistory = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == issueHistoryId
                && h.CompanyCode == context.CompanyCode && h.BranchCode == context.BranchCode,
                cancellationToken);
        var returned = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == returnHistoryId
                && h.CompanyCode == context.CompanyCode && h.BranchCode == context.BranchCode,
                cancellationToken);
        if (issueHistory is null || issueHistory.TrxType != IvTrxTypes.IssueToProduction
            || returned is null || returned.TrxType != IvTrxTypes.ProductionStockReturn
            || returned.ICode != issueHistory.ICode)
            throw Invalid("The WAC Production return histories are inconsistent.");

        var current = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, [returned],
            cancellationToken);
        if (current[returned.Id].Count != 1)
            throw Invalid("The WAC Production return needs one active receipt valuation fact.");
        var oldReturn = current[returned.Id][0];
        var newIssue = issueReplacements[0];
        if (oldReturn.Direction != 1
            || oldReturn.MovementCode != "PRODUCTION_STOCK_RETURN_IN"
            || oldReturn.ValuationSource != StockValuationSources.ProductionActual
            || oldReturn.BaseCostAmount != oldReturn.CostAmount
            || oldReturn.BaseQty != step.SignedQuantity
            || oldReturn.CostAmount != movement.OldSignedValue
            || newIssue.StockPostingId != context.Posting.Id
            || newIssue.InventoryHistoryId != issueHistory.Id
            || newIssue.Direction != -1 || newIssue.BaseQty <= 0m
            || oldReturn.BaseQty > newIssue.BaseQty
            || step.SignedValue != StockLedgerPrecision.Money(
                newIssue.CostAmount * oldReturn.BaseQty / newIssue.BaseQty))
            throw Invalid("The WAC Production return valuation changed after planning.");

        await StockReplayValuationReversalWriter.AppendAsync(context,
            returnHistoryId, [new StockReplayValuationReversalLine(
                oldReturn.Id, reversalLine, 0)], cancellationToken);
        var now = DateTime.UtcNow;
        db.StockValuationFacts.Add(new StockValuationFact
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
            ExchangeRate = oldReturn.ExchangeRate,
            BaseCostAmount = step.SignedValue,
            ValuationSource = StockValuationSources.ProductionActual,
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
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
