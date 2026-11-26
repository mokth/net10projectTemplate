using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Append-only value changes to an existing FIFO Production-return layer.</summary>
internal static class StockReplayFifoProductionReturnValueAdjustment
{
    internal const string Source = "FIFO_PRODUCTION_RETURN_REPLAY";
    internal const string Movement = "PRODUCTION_RETURN_VALUE";

    internal static StockValuationFact Create(StockPostingContext context,
        StockValuationFact origin, decimal signedValue, int line, int ordinal = 0)
    {
        context.EnsureUnsealed();
        if (signedValue == 0m || StockLedgerPrecision.Money(signedValue) != signedValue
            || origin.BaseQty <= 0m || origin.CostMethod != StockCostMethods.Fifo
            || origin.MovementCode != "PRODUCTION_STOCK_RETURN_IN"
            || origin.ValuationSource != StockValuationSources.ProductionActual
            || line <= 0 || ordinal < 0)
            throw Invalid("FIFO Production return adjustment has invalid value or origin.");
        var amount = Math.Abs(signedValue);
        return new StockValuationFact
        {
            CompanyCode = origin.CompanyCode, BranchCode = origin.BranchCode,
            LedgerEpochId = origin.LedgerEpochId, StockPostingId = context.Posting.Id,
            PostingLineNo = line, SplitOrdinal = ordinal,
            SourceLineId = origin.SourceLineId, SourceDocumentType = origin.SourceDocumentType,
            SourceDocumentId = origin.SourceDocumentId, SourceDocumentNo = origin.SourceDocumentNo,
            SourceDocumentLine = origin.SourceDocumentLine, EffectiveAt = origin.EffectiveAt,
            BusinessDate = origin.BusinessDate, PeriodKey = origin.PeriodKey,
            ItemCode = origin.ItemCode, WarehouseCode = origin.WarehouseCode,
            LocationCode = origin.LocationCode, LotId = origin.LotId, LotNo = origin.LotNo,
            ItemStatus = origin.ItemStatus, BaseUom = origin.BaseUom,
            MovementCode = Movement, Direction = Math.Sign(signedValue), BaseQty = 0m,
            CostMethod = StockCostMethods.Fifo,
            UnitCost = StockLedgerPrecision.Money(amount / origin.BaseQty),
            CostAmount = amount, BaseCostAmount = amount, BaseCurrency = origin.BaseCurrency,
            ValuationSource = Source, ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = checked(origin.ValuationVersion + 1),
            ToBalLocId = origin.ToBalLocId, FromBalLocId = origin.FromBalLocId,
            OriginalValuationFactId = origin.Id, CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        };
    }

    internal static bool Matches(StockValuationFact fact, StockValuationFact origin, decimal signedValue) =>
        signedValue != 0m && fact.CompanyCode == origin.CompanyCode
        && fact.BranchCode == origin.BranchCode && fact.LedgerEpochId == origin.LedgerEpochId
        && fact.OriginalValuationFactId == origin.Id && fact.ReversesValuationFactId is null
        && fact.InventoryHistoryId is null && fact.BaseQty == 0m
        && fact.PostingLineNo > 0 && fact.SplitOrdinal >= 0
        && fact.Direction == Math.Sign(signedValue) && fact.CostAmount == Math.Abs(signedValue)
        && fact.CostAmount > 0m && StockLedgerPrecision.Money(fact.CostAmount) == fact.CostAmount
        && fact.BaseCostAmount == fact.CostAmount
        && fact.UnitCost == StockLedgerPrecision.Money(fact.CostAmount / origin.BaseQty)
        && fact.CostMethod == StockCostMethods.Fifo && fact.MovementCode == Movement
        && fact.ValuationSource == Source && fact.ValuationStatus == StockValuationStatuses.Valued
        && (long)fact.ValuationVersion == (long)origin.ValuationVersion + 1
        && fact.SourceLineId == origin.SourceLineId && fact.SourceDocumentType == origin.SourceDocumentType
        && fact.SourceDocumentId == origin.SourceDocumentId && fact.SourceDocumentNo == origin.SourceDocumentNo
        && fact.SourceDocumentLine == origin.SourceDocumentLine && fact.EffectiveAt == origin.EffectiveAt
        && fact.BusinessDate == origin.BusinessDate && fact.PeriodKey == origin.PeriodKey
        && fact.ItemCode == origin.ItemCode && fact.WarehouseCode == origin.WarehouseCode
        && fact.LocationCode == origin.LocationCode && fact.LotId == origin.LotId
        && fact.LotNo == origin.LotNo && fact.ItemStatus == origin.ItemStatus
        && fact.BaseUom == origin.BaseUom && fact.BaseCurrency == origin.BaseCurrency
        && fact.FromBalLocId == origin.FromBalLocId && fact.ToBalLocId == origin.ToBalLocId
        && fact.TransactionCurrency is null && fact.TransactionCostAmount is null && fact.ExchangeRate is null;

    internal static async Task<IReadOnlyList<StockValuationFact>> ReadVerifiedAsync(
        AppDbContext db, StockValuationFact origin, CancellationToken cancellationToken)
    {
        if (origin.BaseQty <= 0m || origin.Direction != 1
            || origin.CostMethod != StockCostMethods.Fifo
            || origin.MovementCode != "PRODUCTION_STOCK_RETURN_IN"
            || origin.ValuationSource != StockValuationSources.ProductionActual)
            throw Invalid("FIFO Production return value evidence has an invalid origin.");
        var rows = await db.StockValuationFacts.AsNoTracking().Include(f => f.StockPosting)
            .Where(f => f.OriginalValuationFactId == origin.Id && f.InventoryHistoryId == null)
            .ToArrayAsync(cancellationToken);
        if (rows.GroupBy(f => f.StockPostingId).Any(g => g.Count() != 1)
            || rows.Any(f => !Matches(f, origin, f.SignedValue)
                || f.StockPosting is null || f.StockPosting.SealedAtUtc is null
                || f.StockPosting.CompanyCode != origin.CompanyCode
                || f.StockPosting.BranchCode != origin.BranchCode
                || f.StockPosting.LedgerEpochId != origin.LedgerEpochId
                || !(f.StockPosting.SourceModule == "INVENTORY"
                        && f.StockPosting.SourceDocumentType is (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                        && f.StockPosting.PostingRole is ("PRIMARY" or "REVERSAL")
                    || f.StockPosting.SourceModule == "PRODUCTION"
                        && f.StockPosting.SourceDocumentType == ProductionDocumentTypes.ProductionStockReturn
                        && f.StockPosting.PostingRole == "REVERSAL"
                        && f.StockPosting.ReversesPostingId == origin.StockPostingId)))
            throw Invalid("FIFO Production return value adjustment evidence is inconsistent.");
        return rows;
    }

    internal static async Task<bool> AppendIfRequiredAsync(
        StockPostingContext context,
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoTailProjection projection,
        StockReplayFifoProjectedIssue issue,
        IReadOnlyList<StockValuationFact> replacements,
        StockReplayFifoProductionPermit? productionPermit,
        int adjustmentLine,
        CancellationToken cancellationToken)
    {
        var historyId = StockReplayFifoMiRootWriter.HistoryId(issue.Key);
        var affected = snapshot.Layers.Where(l => l.ProductionReturn?.IssueHistoryId == historyId).ToArray();
        if (affected.Length == 0)
            return false;
        if (affected.Length != 1 || affected[0].ProductionReturn is not { } binding
            || productionPermit is null
            || productionPermit.IssueHistoryId != historyId
            || issue.PreviousConsumptions.Count == 0 || issue.Allocations.Count == 0
            || replacements.Count != issue.Allocations.Count
            || replacements.Where((fact, ordinal) => fact.StockPostingId != context.Posting.Id
                || fact.InventoryHistoryId != historyId || fact.SplitOrdinal != ordinal
                || fact.BaseQty != issue.Allocations[ordinal].Quantity
                || fact.CostAmount != issue.Allocations[ordinal].Value).Any())
            throw Invalid("FIFO Production return relinking needs its exact issue generation.");
        var before = affected[0];
        var final = projection.FinalLayers.SingleOrDefault(l => l.LayerId == before.LayerId);
        var db = context.Db;
        var origin = await db.StockValuationFacts.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == before.OriginFactId, cancellationToken);
        var newReturnValue = StockLedgerPrecision.Money(
            issue.Value * binding.ReturnedQuantity / issue.Quantity);
        if (final is null || final.OriginalValue != before.OriginalValue
            || final.OriginalQuantity != before.OriginalQuantity
            || origin is null || origin.MovementCode != "PRODUCTION_STOCK_RETURN_IN"
            || origin.ValuationSource != StockValuationSources.ProductionActual
            || origin.CostMethod != StockCostMethods.Fifo
            || origin.InventoryHistoryId != binding.ReturnHistoryId
            || StockLedgerPrecision.Money(origin.CostAmount + before.AccumulatedAdjustment) != binding.ReturnedValue
            || final.AccumulatedAdjustment != StockLedgerPrecision.Money(newReturnValue - origin.CostAmount))
            throw Invalid("FIFO Production return changed after the receipt was planned.");

        var delta = StockLedgerPrecision.Money(final.AccumulatedAdjustment - before.AccumulatedAdjustment);
        if (delta == 0m)
            return false;
        db.StockValuationFacts.Add(Create(context, origin, delta, adjustmentLine));
        return true;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
