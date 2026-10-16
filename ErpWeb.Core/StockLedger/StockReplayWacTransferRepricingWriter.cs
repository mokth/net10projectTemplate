using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Reprices both legs of an unchanged WAC transfer to the historical moving
/// average. The item pool stays value-neutral; each leg keeps its exact
/// quantity and source identity in a complete append-only replacement set.
/// </summary>
public static class StockReplayWacTransferRepricingWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        int activeHistoryId,
        StockReplayWacEvent activeEvent,
        StockReplayWacStep projectedStep,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(activeEvent);
        ArgumentNullException.ThrowIfNull(projectedStep);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || activeHistoryId <= 0 || reversalPostingLineNo <= 0
            || replacementPostingLineNo <= 0
            || reversalPostingLineNo == replacementPostingLineNo
            || activeEvent.EventId != activeHistoryId
            || projectedStep.EventId != activeHistoryId)
            throw Invalid("WAC transfer replacement has invalid root or event identity.");
        var db = context.Db;
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == activeHistoryId
                && h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode, cancellationToken);
        if (history is null || history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.EntryRole != "ORIGINAL" || history.BatchStatus != "POSTED"
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.FromBalLocId is not > 0
            || history.ToBalLocId is not > 0
            || history.FrStdQty is not > 0m
            || history.ToStdQty != history.FrStdQty)
            throw Invalid($"History {activeHistoryId} is not an active V2 transfer.");
        var originals = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode,
            [history], cancellationToken))[activeHistoryId]
            .OrderBy(f => f.PostingLineNo).ThenBy(f => f.SplitOrdinal).ToArray();
        if (activeEvent.Key.LineIdentity.Length != 11
            || activeEvent.Key.LineIdentity[0] != 'H'
            || !int.TryParse(activeEvent.Key.LineIdentity.AsSpan(1), out var ownerHistoryId)
            || ownerHistoryId <= 0
            || StockReplayWacHistoryEventFactory.Create(history, ownerHistoryId,
                activeEvent.Key, originals) != activeEvent
            || originals.Any(f => f.CostMethod != StockCostMethods.MovingAverage
                || f.ValuationSource != StockValuationSources.MovingAverage
                || f.TransactionCurrency is not null
                || f.TransactionCostAmount is not null
                || f.ExchangeRate is not null
                || f.BaseCostAmount != f.CostAmount))
            throw Invalid($"History {activeHistoryId} has unsupported WAC transfer facts.");
        var outbound = originals.Where(f => f.Direction < 0).ToArray();
        var inbound = originals.Where(f => f.Direction > 0).ToArray();
        if (outbound.Length == 0 || inbound.Length == 0
            || outbound.Sum(f => f.BaseQty) != history.FrStdQty
            || inbound.Sum(f => f.BaseQty) != history.ToStdQty)
            throw Invalid($"History {activeHistoryId} has incomplete transfer legs.");
        var oldAmount = StockLedgerPrecision.Money(
            outbound.Sum(f => f.CostAmount));
        if (oldAmount != StockLedgerPrecision.Money(
                inbound.Sum(f => f.CostAmount))
            || activeEvent.OldSignedQuantity != 0m
            || activeEvent.OldSignedValue != 0m
            || activeEvent.ReplaySignedQuantity != 0m
            || activeEvent.FixedSignedValue != 0m
            || projectedStep.SignedQuantity != 0m
            || projectedStep.SignedValue != 0m
            || projectedStep.QuantityBefore != projectedStep.QuantityAfter
            || projectedStep.ValueBefore != projectedStep.ValueAfter
            || projectedStep.QuantityBefore < history.FrStdQty
            || projectedStep.ValueBefore < 0m
            || IvQty.Round(projectedStep.QuantityBefore)
                != projectedStep.QuantityBefore
            || StockLedgerPrecision.Money(projectedStep.ValueBefore)
                != projectedStep.ValueBefore)
            throw Invalid($"History {activeHistoryId} transfer projection is invalid.");
        var transferQty = history.FrStdQty!.Value;
        var expectedAmount = transferQty == projectedStep.QuantityBefore
            ? projectedStep.ValueBefore
            : StockLedgerPrecision.Money(transferQty
                * StockLedgerPrecision.Money(projectedStep.ValueBefore
                    / projectedStep.QuantityBefore));
        if (expectedAmount == oldAmount)
            return [];

        var occupied = await db.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.StockPostingId == context.Posting.Id
                && (f.PostingLineNo == reversalPostingLineNo
                    || f.PostingLineNo == replacementPostingLineNo),
                cancellationToken);
        if (occupied || db.ChangeTracker.Entries<StockValuationFact>()
            .Any(e => e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && (e.Entity.PostingLineNo == reversalPostingLineNo
                    || e.Entity.PostingLineNo == replacementPostingLineNo)))
            throw Invalid("A WAC transfer replay root valuation line is already used.");
        var amounts = new decimal[originals.Length];
        foreach (var direction in new[] { -1, 1 })
        {
            var indices = Enumerable.Range(0, originals.Length)
                .Where(i => originals[i].Direction == direction).ToArray();
            decimal allocated = 0m;
            for (var ordinal = 0; ordinal < indices.Length; ordinal++)
            {
                var index = indices[ordinal];
                amounts[index] = ordinal == indices.Length - 1
                    ? StockLedgerPrecision.Money(expectedAmount - allocated)
                    : StockLedgerPrecision.Money(expectedAmount
                        * originals[index].BaseQty / transferQty);
                allocated = StockLedgerPrecision.Money(
                    allocated + amounts[index]);
            }
            if (allocated != expectedAmount || indices.Any(i => amounts[i] < 0m))
                throw Invalid("The WAC transfer splits cannot represent the projected cost.");
        }

        await StockReplayValuationReversalWriter.AppendAsync(context,
            activeHistoryId, originals.Select((f, i) =>
                new StockReplayValuationReversalLine(
                    f.Id, reversalPostingLineNo, i)).ToArray(),
            cancellationToken);
        var now = DateTime.UtcNow;
        var replacements = originals.Select((original, i) =>
            new StockValuationFact
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = replacementPostingLineNo,
                SplitOrdinal = i,
                SourceLineId = original.SourceLineId,
                SourceDocumentType = original.SourceDocumentType,
                SourceDocumentId = original.SourceDocumentId,
                SourceDocumentNo = original.SourceDocumentNo,
                SourceDocumentLine = original.SourceDocumentLine,
                EffectiveAt = original.EffectiveAt,
                BusinessDate = original.BusinessDate,
                PeriodKey = original.PeriodKey,
                ItemCode = original.ItemCode,
                WarehouseCode = original.WarehouseCode,
                LocationCode = original.LocationCode,
                LotId = original.LotId,
                LotNo = original.LotNo,
                ItemStatus = original.ItemStatus,
                BaseUom = original.BaseUom,
                MovementCode = original.MovementCode,
                Direction = original.Direction,
                BaseQty = original.BaseQty,
                CostMethod = StockCostMethods.MovingAverage,
                UnitCost = StockLedgerPrecision.Money(
                    amounts[i] / original.BaseQty),
                CostAmount = amounts[i],
                BaseCurrency = original.BaseCurrency,
                BaseCostAmount = amounts[i],
                ValuationSource = StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = checked(original.ValuationVersion + 1),
                InventoryHistoryId = original.InventoryHistoryId,
                FromBalLocId = original.FromBalLocId,
                ToBalLocId = original.ToBalLocId,
                ProductionMovementId = original.ProductionMovementId,
                WorkOrderId = original.WorkOrderId,
                WorkOrderOperationId = original.WorkOrderOperationId,
                ProductionPostingLinkId = original.ProductionPostingLinkId,
                OriginalValuationFactId = original.OriginalValuationFactId
                    ?? original.Id,
                CreatedAtUtc = now,
                CreatedBy = context.UserId
            }).ToArray();
        db.StockValuationFacts.AddRange(replacements);
        return replacements;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
