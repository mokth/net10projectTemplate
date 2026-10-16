using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayStandardCountCorrectionValuationResult(
    StockValuationFact? Reversal, StockValuationFact? Replacement);

/// <summary>
/// Values a changed later count after its physical ADJ correction has been
/// persisted by the same initial-count replay root.
/// </summary>
public static class StockReplayStandardCountCorrectionValuationWriter
{
    public static async Task<StockReplayStandardCountCorrectionValuationResult> AppendAsync(
        StockPostingContext context,
        StockReplayStandardWritePermit standardPermit,
        StockReplayStandardCountSheetInsertionPlan plan,
        StockCountCorrectionWritePermit countPermit,
        int countLineId, int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(standardPermit);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(countPermit);
        context.EnsureUnsealed();
        StockReplayStandardCountSheetInsertionPlanner.ValidatePreparedPlan(plan);
        var physical = plan.CountedPiles.SingleOrDefault(p =>
            p.BalLocId == countPermit.BalLocId);
        if (context.CostMethod != StockCostMethods.Standard
            || context.Db.Database.CurrentTransaction is null
            || countLineId <= 0 || reversalPostingLineNo <= 0
            || replacementPostingLineNo <= 0
            || reversalPostingLineNo == replacementPostingLineNo
            || standardPermit.RootPostingId != context.Posting.Id
            || standardPermit.RootPostingSequence != context.Posting.PostingSequence
            || countPermit.RootPostingId != context.Posting.Id
            || standardPermit.LedgerEpochId != context.Epoch.Id
            || countPermit.LedgerEpochId != context.Epoch.Id
            || standardPermit.CompanyCode != context.CompanyCode
            || standardPermit.BranchCode != context.BranchCode
            || countPermit.CompanyCode != context.CompanyCode
            || countPermit.BranchCode != context.BranchCode
            || standardPermit.ItemCode != plan.ItemCode
            || standardPermit.PlanHash != plan.PlanHash
            || standardPermit.CurrentQuantity != plan.Tail.CurrentQuantity
            || standardPermit.CurrentValue != plan.Tail.CurrentValue
            || standardPermit.FinalQuantity != plan.Projection.FinalQuantity
            || standardPermit.FinalValue != plan.Projection.FinalValue
            || physical is null || physical.PlanHash != countPermit.PlanHash
            || !standardPermit.ChangedPiles.TryGetValue(countPermit.BalLocId,
                out var finalPile) || finalPile != countPermit.FinalQuantity)
            throw Invalid("Changed Standard count has no matching prepared root.");
        var corrections = countPermit.Lines.Where(l => l.LineId == countLineId)
            .ToArray();
        if (corrections.Length != 1)
            throw Invalid($"Count line {countLineId} has no unique physical correction.");
        var correction = corrections[0];
        var oldEvents = plan.Tail.LaterEvents.Where(e =>
            e.EventId == -correction.PreviousGenerationId
            && e.Kind == StockReplayStandardEventKinds.Count
            && e.Key.LineIdentity == $"C{countLineId:D10}").ToArray();
        var steps = plan.Projection.LaterSteps.Where(s =>
            s.EventId == -correction.PreviousGenerationId).ToArray();
        if (oldEvents.Length != 1 || steps.Length != 1
            || correction.NewSignedAdjustmentQty == correction.OldSignedAdjustmentQty
            || steps[0].SignedQuantity != correction.NewSignedAdjustmentQty
            || oldEvents[0].OldSignedQuantity != correction.OldSignedAdjustmentQty)
            throw Invalid($"Count line {countLineId} has no matching Standard step.");
        var oldEvent = oldEvents[0];
        var step = steps[0];
        var db = context.Db;
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.StockCountLineId == countLineId
                && g.CompanyCode == context.CompanyCode
                && g.BranchCode == context.BranchCode)
            .ToArrayAsync(cancellationToken);
        var active = StockCountGenerationResolver.ResolveActive(generations)
            .GetValueOrDefault(countLineId);
        var prior = generations.SingleOrDefault(g =>
            g.Id == correction.PreviousGenerationId);
        if (prior is null || active is null || prior.IsRemoval
            || prior.AnchorPostingId != correction.AnchorPostingId
            || prior.AnchorPostingSequence != correction.AnchorPostingSequence
            || prior.EffectiveAt != correction.EffectiveAt
            || prior.SignedAdjustmentQty != correction.OldSignedAdjustmentQty
            || active.PreviousGenerationId != prior.Id
            || active.WritePostingId != context.Posting.Id
            || active.AnchorPostingId != prior.AnchorPostingId
            || active.AnchorPostingSequence != prior.AnchorPostingSequence
            || active.EffectiveAt != prior.EffectiveAt
            || active.IsRemoval || active.SignedAdjustmentQty != step.SignedQuantity
            || active.ReversalHistoryId.HasValue
                != prior.AdjustmentHistoryId.HasValue
            || active.AdjustmentHistoryId.HasValue
                != (step.SignedQuantity != 0m))
            throw Invalid($"Count line {countLineId} has no exact correction generation.");
        var line = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.Id == countLineId)
            .Select(l => new { l.PhysicalQty, l.BalLocId, l.ICode,
                HeaderId = l.Header.Id, l.Header.CountDate,
                l.Header.CountNo, l.Header.Status,
                l.Header.CompanyCode, l.Header.BranchCode })
            .SingleOrDefaultAsync(cancellationToken);
        var anchor = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == prior.AnchorPostingId,
                cancellationToken);
        var basis = await db.StockCountCostBases.AsNoTracking()
            .SingleOrDefaultAsync(b => b.StockCountLineId == countLineId,
                cancellationToken);
        if (line is null || line.PhysicalQty != correction.PhysicalQty
            || line.BalLocId != countPermit.BalLocId
            || line.ICode != plan.ItemCode
            || line.Status != IvStockCountStatuses.Posted
            || line.CompanyCode != context.CompanyCode
            || line.BranchCode != context.BranchCode
            || anchor is null || anchor.SealedAtUtc is null
            || anchor.CompanyCode != context.CompanyCode
            || anchor.BranchCode != context.BranchCode
            || anchor.LedgerEpochId != context.Epoch.Id
            || anchor.PostingSequence != prior.AnchorPostingSequence
            || anchor.EffectiveAt != prior.EffectiveAt
            || anchor.BusinessDate != line.CountDate.Date
            || anchor.SourceDocumentType != "IV_STOCK_COUNT"
            || anchor.SourceDocumentId != line.HeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || anchor.SourceDocumentNo != line.CountNo
            || (line.PhysicalQty > 0m) != (basis is not null))
            throw Invalid($"Count line {countLineId} lacks its sealed Standard owner.");
        var revision = await new ItemStandardCostResolver().ResolveAsync(db,
            context.CompanyCode, context.BranchCode, plan.ItemCode,
            prior.EffectiveAt, cancellationToken);
        StockValuationFact? oldFact = null;
        if (prior.AdjustmentHistoryId is int oldHistoryId)
        {
            var oldHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.Id == oldHistoryId, cancellationToken);
            var resolved = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, context.CompanyCode, context.BranchCode,
                    [oldHistory], cancellationToken);
            if (resolved[oldHistoryId].Count != 1)
                throw Invalid($"Count line {countLineId} has no single active fact.");
            oldFact = resolved[oldHistoryId][0];
        }
        StockValuationFact? originalFact = null;
        if (oldFact is not null && oldFact.StockPostingId != prior.WritePostingId)
            originalFact = await db.StockValuationFacts.AsNoTracking()
                .SingleOrDefaultAsync(f => f.InventoryHistoryId
                    == prior.AdjustmentHistoryId
                    && f.StockPostingId == prior.WritePostingId
                    && f.OriginalValuationFactId == null
                    && f.ReversesValuationFactId == null,
                    cancellationToken);
        var oldPhysical = new StockReplayPhysicalEvent(-prior.Id,
            oldEvent.Key, prior.SignedAdjustmentQty, line.PhysicalQty);
        if (oldEvent != StockReplayStandardCountEventFactory.CreateActive(
                oldPhysical, prior, plan.ItemCode, revision,
                oldFact is null ? [] : [oldFact], basis, originalFact)
            || step.SignedValue != StockLedgerPrecision.Money(
                step.SignedQuantity * revision.TotalStandardCost)
            || oldFact is not null
                && (oldFact.TransactionCurrency is not null
                    || oldFact.TransactionCostAmount is not null
                    || oldFact.ExchangeRate is not null
                    || oldFact.BaseCostAmount != oldFact.CostAmount))
            throw Invalid($"Count line {countLineId} differs from its Standard authority.");
        var periodRefusal = await IvPeriodCloseGuard.EnsureOpenAsync(db,
            context.CompanyCode, context.BranchCode, prior.EffectiveAt,
            cancellationToken);
        if (periodRefusal is not null)
            throw new StockLedgerException(new(StockLedgerErrorCodes.ClosedPeriod,
                periodRefusal));
        var occupied = await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
            f.StockPostingId == context.Posting.Id
            && (f.PostingLineNo == reversalPostingLineNo
                || f.PostingLineNo == replacementPostingLineNo),
            cancellationToken);
        if (occupied || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && (e.Entity.PostingLineNo == reversalPostingLineNo
                    || e.Entity.PostingLineNo == replacementPostingLineNo)))
            throw Invalid("Changed Standard count root valuation line is occupied.");

        StockValuationFact? reversal = null;
        if (oldFact is not null)
        {
            reversal = (await StockReplayValuationReversalWriter.AppendAsync(
                context, prior.AdjustmentHistoryId!.Value,
                [new StockReplayValuationReversalLine(oldFact.Id,
                    reversalPostingLineNo, 0, active.ReversalHistoryId)],
                cancellationToken)).Single();
            var reversalHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.Id == active.ReversalHistoryId, cancellationToken);
            if (reversalHistory.PostingLineNo != reversalPostingLineNo)
                throw Invalid("Standard count reversal fact differs from its ADJ line.");
            reversalHistory.ValuationStatus = StockValuationStatuses.Reversed;
        }
        StockValuationFact? replacement = null;
        if (active.AdjustmentHistoryId is int replacementHistoryId)
        {
            var history = await db.IvTrxHistories.SingleAsync(h =>
                h.Id == replacementHistoryId, cancellationToken);
            var direction = Math.Sign(step.SignedQuantity);
            var quantity = Math.Abs(step.SignedQuantity);
            var amount = Math.Abs(step.SignedValue);
            if (history.PostingLineNo != replacementPostingLineNo
                || history.StockPostingId != context.Posting.Id
                || history.LedgerEpochId != context.Epoch.Id
                || history.ICode != plan.ItemCode
                || history.EntryRole != "ORIGINAL"
                || history.BatchStatus != IvBatchStatuses.Posted
                || history.TrxDtTime != anchor.BusinessDate.Date
                || history.ValuationStatus != StockValuationStatuses.Unvalued
                || history.StockCountCostBasisId != (direction > 0 ? basis!.Id : null)
                || (direction > 0
                    ? history.ToBalLocId != countPermit.BalLocId
                        || history.ToStdQty != quantity
                        || string.IsNullOrWhiteSpace(history.ToStdUom)
                    : history.FromBalLocId != countPermit.BalLocId
                        || history.FrStdQty != quantity
                        || string.IsNullOrWhiteSpace(history.FrStdUom)))
                throw Invalid("Standard count replacement ADJ differs from its projection.");
            replacement = new StockValuationFact
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = replacementPostingLineNo,
                SplitOrdinal = 0,
                SourceLineId = oldFact?.SourceLineId
                    ?? $"IV_STOCK_COUNT:{anchor.SourceDocumentId}:{history.TrxLineNo}:{replacementPostingLineNo}:0",
                SourceDocumentType = oldFact?.SourceDocumentType ?? anchor.SourceDocumentType,
                SourceDocumentId = oldFact?.SourceDocumentId ?? anchor.SourceDocumentId,
                SourceDocumentNo = oldFact?.SourceDocumentNo ?? anchor.SourceDocumentNo,
                SourceDocumentLine = oldFact?.SourceDocumentLine
                    ?? history.TrxLineNo.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                EffectiveAt = prior.EffectiveAt,
                BusinessDate = anchor.BusinessDate,
                PeriodKey = anchor.PeriodKey,
                ItemCode = plan.ItemCode,
                WarehouseCode = direction > 0 ? history.ToWarehouse : history.FrWarehouse,
                LocationCode = direction > 0 ? history.ToLocation : history.FrLocation,
                LotId = direction > 0 ? history.ToLotId : history.FromLotId,
                LotNo = direction > 0 ? history.ToLotNo : history.FrLotNo,
                ItemStatus = history.IStatus,
                BaseUom = direction > 0 ? history.ToStdUom! : history.FrStdUom!,
                MovementCode = direction > 0 ? "ADJUST_IN" : "ADJUST_OUT",
                Direction = direction,
                BaseQty = quantity,
                CostMethod = StockCostMethods.Standard,
                UnitCost = revision.TotalStandardCost,
                CostAmount = amount,
                BaseCurrency = oldFact?.BaseCurrency,
                BaseCostAmount = amount,
                ValuationSource = StockValuationSources.Standard,
                ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = 1,
                InventoryHistoryId = history.Id,
                FromBalLocId = history.FromBalLocId,
                ToBalLocId = history.ToBalLocId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            };
            db.StockValuationFacts.Add(replacement);
            history.ValuationStatus = StockValuationStatuses.Valued;
        }
        return new(reversal, replacement);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
