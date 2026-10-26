using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoMrReceiptValuation(
    int HistoryId, long FactId, long LayerId);

/// <summary>
/// Appends one original approved MR receipt fact and its new FIFO layer under
/// the unsealed replay root. The caller must replay descendants and apply the
/// projected final layer/state positions before sealing.
/// </summary>
public static class StockReplayFifoMrReceiptValuationWriter
{
    public static async Task<StockReplayFifoMrReceiptValuation> AppendAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, int historyId,
        int postingLineNo, CancellationToken cancellationToken = default,
        bool goodsReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        if (postingLineNo <= 0)
            throw Invalid("FIFO MR valuation needs a positive root line.");
        var bound = await StockReplayFifoMrReceiptCandidateBinder.BindAsync(
            context, plan, permit, historyId, cancellationToken,
            goodsReceipt);
        if (bound.RootPostingId != context.Posting.Id
            || bound.PreparedPlanHash != plan.PlanHash
            || bound.FinancialProjection.CandidateReceipt is not { } receipt
            || receipt.OriginalQuantity != plan.PhysicalPlan.Candidate.SignedDelta
            || receipt.OriginalValue != plan.ReceiptValue
            || receipt.LayerId != long.MaxValue)
            throw Invalid("The bound MR has no exact projected receipt layer.");
        var db = context.Db;
        if (await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.CompanyCode == context.CompanyCode
                && f.BranchCode == context.BranchCode
                && (f.InventoryHistoryId == historyId
                    || f.StockPostingId == context.Posting.Id
                    && f.PostingLineNo == postingLineNo),
                cancellationToken)
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && (e.Entity.InventoryHistoryId == historyId
                    || e.Entity.PostingLineNo == postingLineNo)))
            throw Invalid("The FIFO MR history or root valuation line is already used.");
        var history = await db.IvTrxHistories.SingleAsync(
            h => h.Id == historyId, cancellationToken);
        var receiptUnit = goodsReceipt
            ? history.BaseUnitPrices : history.UnitPrice;
        if (history.ValuationStatus is not null
            || receiptUnit is not decimal unitCost
            || StockLedgerPrecision.Money(
                unitCost * receipt.OriginalQuantity) != receipt.OriginalValue
            || history.ToStdQty != receipt.OriginalQuantity)
            throw Invalid("The bound MR history is no longer unvalued.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw Invalid("The FIFO MR company has no base currency.");
        var sourceLine = history.TrxLineNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var fact = new StockValuationFact
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLineNo,
            SplitOrdinal = 0,
            SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{sourceLine}:{postingLineNo}:0",
            SourceDocumentType = context.Posting.SourceDocumentType,
            SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo,
            SourceDocumentLine = sourceLine,
            EffectiveAt = bound.Key.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = plan.ItemCode,
            WarehouseCode = BlankToNull(history.ToWarehouse),
            LocationCode = BlankToNull(history.ToLocation),
            LotId = history.ToLotId,
            LotNo = history.ToLotNo,
            ItemStatus = BlankToNull(history.IStatus),
            BaseUom = plan.ReceiptBaseUom!,
            MovementCode = "RECEIPT_IN",
            Direction = 1,
            BaseQty = receipt.OriginalQuantity,
            CostMethod = StockCostMethods.Fifo,
            UnitCost = unitCost,
            CostAmount = receipt.OriginalValue,
            BaseCurrency = baseCurrency,
            BaseCostAmount = receipt.OriginalValue,
            ValuationSource = StockValuationSources.Fifo,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            InventoryHistoryId = historyId,
            ToBalLocId = history.ToBalLocId,
            CreatedAtUtc = now,
            CreatedBy = context.UserId
        };
        db.StockValuationFacts.Add(fact);
        var layer = new StockFifoLayer
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            ItemCode = plan.ItemCode,
            BaseUom = plan.ReceiptBaseUom!,
            OriginValuationFact = fact,
            OriginStockPosting = context.Posting,
            ReceiptEffectiveAt = bound.Key.EffectiveAt,
            OriginalQty = receipt.OriginalQuantity,
            RemainingQty = receipt.OriginalQuantity,
            OriginalValue = receipt.OriginalValue,
            AccumulatedAdjustment = 0m,
            RemainingValue = receipt.OriginalValue,
            CurrentUnitCost = unitCost,
            SourceDocumentType = fact.SourceDocumentType,
            SourceDocumentNo = fact.SourceDocumentNo,
            SourceDocumentLine = fact.SourceDocumentLine,
            WarehouseCode = fact.WarehouseCode,
            Status = StockFifoLayerStatuses.Open
        };
        db.StockFifoLayers.Add(layer);
        history.ValuationStatus = StockValuationStatuses.Valued;
        await db.SaveChangesAsync(cancellationToken);
        return new(historyId, fact.Id, layer.Id);
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
