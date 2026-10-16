using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Appends an exact old-generation reversal and a complete replacement for a
/// quantity-unchanged WAC issue. The caller remains responsible for the locked
/// replay plan, live cost state, and dependent Procurement/Production/Sales costs.
/// </summary>
public static class StockReplayWacIssueRepricingWriter
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
            throw Invalid("WAC issue replacement has invalid root or event identity.");

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
            || history.FrStdQty is not > 0m
            || history.ToStdQty is > 0m)
            throw Invalid($"History {activeHistoryId} is not an active V2 issue.");
        var originals = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode,
            [history], cancellationToken))[activeHistoryId]
            .OrderBy(f => f.PostingLineNo).ThenBy(f => f.SplitOrdinal).ToArray();
        if (activeEvent.Key.LineIdentity.Length != 11
            || activeEvent.Key.LineIdentity[0] != 'H'
            || !int.TryParse(activeEvent.Key.LineIdentity.AsSpan(1), out var ownerHistoryId)
            || ownerHistoryId <= 0
            || StockReplayWacHistoryEventFactory.Create(history, ownerHistoryId,
                activeEvent.Key, originals) != activeEvent)
            throw Invalid($"History {activeHistoryId} WAC event differs from its active valuation.");
        if (originals.Any(f => f.CostMethod != StockCostMethods.MovingAverage
                || f.Direction != -1
                || f.ValuationSource != StockValuationSources.MovingAverage
                || f.TransactionCurrency is not null
                || f.TransactionCostAmount is not null
                || f.ExchangeRate is not null
                || f.BaseCostAmount != f.CostAmount)
            || originals.Length > 1000)
            throw Invalid($"History {activeHistoryId} has unsupported WAC issue splits.");

        var oldQty = -StockLedgerPrecision.Quantity(originals.Sum(f => f.BaseQty));
        var oldValue = -StockLedgerPrecision.Money(originals.Sum(f => f.CostAmount));
        if (activeEvent.OldSignedQuantity != oldQty
            || activeEvent.OldSignedValue != oldValue
            || activeEvent.ReplaySignedQuantity != oldQty
            || activeEvent.AbsoluteCountQty.HasValue
            || activeEvent.FixedSignedValue.HasValue
            || projectedStep.QuantityBefore < -oldQty
            || projectedStep.ValueBefore < 0m
            || IvQty.Round(projectedStep.QuantityBefore) != projectedStep.QuantityBefore
            || StockLedgerPrecision.Money(projectedStep.ValueBefore)
                != projectedStep.ValueBefore)
            throw Invalid($"History {activeHistoryId} issue projection does not match its active facts.");
        var issueQty = -oldQty;
        var replayed = StockReplayWacSimulator.SimulateInsertion(
            projectedStep.QuantityBefore, projectedStep.ValueBefore,
            activeEvent with { OldSignedQuantity = 0m, OldSignedValue = 0m }, []);
        if (projectedStep != replayed.Candidate)
            throw Invalid($"History {activeHistoryId} has an invalid replayed WAC issue amount.");
        var expectedAmount = -replayed.Candidate.SignedValue;
        if (expectedAmount == -oldValue)
            return [];

        var occupied = await db.StockValuationFacts.AsNoTracking()
            .AnyAsync(f => f.StockPostingId == context.Posting.Id
                && (f.PostingLineNo == reversalPostingLineNo
                    || f.PostingLineNo == replacementPostingLineNo), cancellationToken);
        if (occupied || db.ChangeTracker.Entries<StockValuationFact>()
            .Any(e => e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && (e.Entity.PostingLineNo == reversalPostingLineNo
                    || e.Entity.PostingLineNo == replacementPostingLineNo)))
            throw Invalid("A WAC replay root valuation line is already used.");

        // Money is allocated by original split quantity. Give the final split
        // the exact residual so the replacement generation always reconciles.
        var amounts = new decimal[originals.Length];
        decimal allocated = 0m;
        for (var i = 0; i < originals.Length; i++)
        {
            amounts[i] = i == originals.Length - 1
                ? StockLedgerPrecision.Money(expectedAmount - allocated)
                : StockLedgerPrecision.Money(
                    expectedAmount * originals[i].BaseQty / issueQty);
            allocated = StockLedgerPrecision.Money(allocated + amounts[i]);
        }
        if (allocated != expectedAmount || amounts.Any(x => x < 0m))
            throw Invalid("The WAC issue split cannot represent the projected value.");

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
                Direction = -1,
                BaseQty = original.BaseQty,
                CostMethod = StockCostMethods.MovingAverage,
                UnitCost = StockLedgerPrecision.Money(amounts[i] / original.BaseQty),
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
