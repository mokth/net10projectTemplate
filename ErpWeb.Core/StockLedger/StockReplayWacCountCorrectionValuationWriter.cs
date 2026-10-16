using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayWacCountCorrectionValuationResult(
    StockValuationFact? Reversal,
    StockValuationFact? Replacement);

/// <summary>
/// Values a changed physical count generation after its exact ADJ reversal and
/// replacement histories have been persisted in the same replay root. The
/// WAC and physical permits must have been issued before those mutations.
/// </summary>
public static class StockReplayWacCountCorrectionValuationWriter
{
    public static async Task<StockReplayWacCountCorrectionValuationResult> AppendAsync(
        StockPostingContext context,
        StockReplayWacWritePermit wacPermit,
        StockReplayWacInsertionPlan wacPlan,
        StockCountCorrectionWritePermit countPermit,
        int countLineId,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(wacPermit);
        ArgumentNullException.ThrowIfNull(wacPlan);
        ArgumentNullException.ThrowIfNull(countPermit);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || countLineId <= 0 || reversalPostingLineNo <= 0
            || replacementPostingLineNo <= 0
            || reversalPostingLineNo == replacementPostingLineNo
            || wacPermit.RootPostingId != context.Posting.Id
            || countPermit.RootPostingId != context.Posting.Id
            || wacPermit.LedgerEpochId != context.Epoch.Id
            || countPermit.LedgerEpochId != context.Epoch.Id
            || wacPermit.CompanyCode != context.CompanyCode
            || wacPermit.BranchCode != context.BranchCode
            || countPermit.CompanyCode != context.CompanyCode
            || countPermit.BranchCode != context.BranchCode
            || wacPermit.ItemCode != wacPlan.ItemCode
            || wacPermit.PlanHash != wacPlan.PlanHash
            || countPermit.PlanHash != wacPlan.PhysicalPlan.PlanHash
            || countPermit.BalLocId != wacPermit.ChangedPileId)
            throw Invalid("Changed-count WAC valuation has no matching replay permits.");

        var correctionLines = countPermit.Lines
            .Where(l => l.LineId == countLineId).ToArray();
        if (correctionLines.Length != 1)
            throw Invalid($"Count line {countLineId} has no unique physical correction.");
        var correction = correctionLines[0];
        var oldEvents = wacPlan.Tail.LaterEvents
            .Where(e => e.EventId == -correction.PreviousGenerationId
                && e.Key.LineIdentity == $"C{countLineId:D10}").ToArray();
        var projected = wacPlan.Projection.LaterSteps
            .Where(s => s.EventId == -correction.PreviousGenerationId).ToArray();
        if (oldEvents.Length != 1 || projected.Length != 1
            || correction.NewSignedAdjustmentQty == correction.OldSignedAdjustmentQty
            || projected[0].SignedQuantity != correction.NewSignedAdjustmentQty
            || oldEvents[0].OldSignedQuantity != correction.OldSignedAdjustmentQty)
            throw Invalid($"Count line {countLineId} has no matching changed WAC step.");
        var oldEvent = oldEvents[0];
        var step = projected[0];

        var db = context.Db;
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.StockCountLineId == countLineId
                && g.CompanyCode == context.CompanyCode
                && g.BranchCode == context.BranchCode)
            .ToArrayAsync(cancellationToken);
        // The new generation belongs to this unsealed root, so the sealed-only
        // read resolver is deliberately inapplicable until completion.
        var activeGeneration = StockCountGenerationResolver
            .ResolveActive(generations)[countLineId];
        var oldGeneration = generations.SingleOrDefault(g =>
            g.Id == correction.PreviousGenerationId);
        if (oldGeneration is null || oldGeneration.IsRemoval
            || oldGeneration.SignedAdjustmentQty != correction.OldSignedAdjustmentQty
            || activeGeneration.PreviousGenerationId != oldGeneration.Id
            || activeGeneration.WritePostingId != context.Posting.Id
            || activeGeneration.IsRemoval
            || activeGeneration.SignedAdjustmentQty != step.SignedQuantity
            || activeGeneration.AnchorPostingId != oldGeneration.AnchorPostingId
            || activeGeneration.AnchorPostingSequence
                != oldGeneration.AnchorPostingSequence
            || activeGeneration.EffectiveAt != oldGeneration.EffectiveAt
            || activeGeneration.ReversalHistoryId.HasValue
                != (oldGeneration.SignedAdjustmentQty != 0m)
            || activeGeneration.AdjustmentHistoryId.HasValue
                != (step.SignedQuantity != 0m))
            throw Invalid($"Count line {countLineId} has no exact active correction generation.");
        var line = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.Id == countLineId)
            .Select(l => new
            {
                l.PhysicalQty, l.BalLocId, l.ICode, l.LineNumber,
                StockCountId = l.Header.Id, l.Header.CountDate,
                l.Header.CompanyCode, l.Header.BranchCode,
                l.Header.Status, l.Header.CountNo
            }).SingleOrDefaultAsync(cancellationToken);
        var anchor = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == oldGeneration.AnchorPostingId,
                cancellationToken);
        if (line is null || line.PhysicalQty != correction.PhysicalQty
            || line.BalLocId != countPermit.BalLocId
            || line.ICode != wacPermit.ItemCode
            || line.CompanyCode != context.CompanyCode
            || line.BranchCode != context.BranchCode
            || line.Status != IvStockCountStatuses.Posted
            || anchor is null || anchor.SealedAtUtc is null
            || anchor.LedgerEpochId != context.Epoch.Id
            || anchor.CompanyCode != context.CompanyCode
            || anchor.BranchCode != context.BranchCode
            || anchor.PostingSequence != oldGeneration.AnchorPostingSequence
            || anchor.EffectiveAt != oldGeneration.EffectiveAt
            || anchor.BusinessDate != line.CountDate.Date
            || anchor.SourceDocumentType != "IV_STOCK_COUNT"
            || anchor.SourceDocumentId != line.StockCountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || anchor.SourceDocumentNo != line.CountNo)
            throw Invalid($"Count line {countLineId} has no sealed original count owner.");
        var basis = await db.StockCountCostBases.AsNoTracking()
            .SingleOrDefaultAsync(b => b.StockCountLineId == countLineId,
                cancellationToken);
        if (line.PhysicalQty > 0m && basis is null
            || line.PhysicalQty == 0m && basis is not null)
            throw Invalid($"Count line {countLineId} has no matching frozen cost basis.");

        StockValuationFact? oldFact = null;
        if (oldGeneration.AdjustmentHistoryId is int oldHistoryId)
        {
            var oldHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.Id == oldHistoryId, cancellationToken);
            var facts = (await StockReplayValuationGenerationResolver.ResolveAsync(
                db, context.CompanyCode, context.BranchCode,
                [oldHistory], cancellationToken))[oldHistoryId];
            if (facts.Count != 1)
                throw Invalid($"Count line {countLineId} has no single prior WAC fact.");
            oldFact = facts[0];
        }
        StockValuationFact? originalFact = null;
        if (oldFact is not null
            && oldFact.StockPostingId != oldGeneration.WritePostingId)
            originalFact = await db.StockValuationFacts.AsNoTracking()
                .SingleOrDefaultAsync(f => f.InventoryHistoryId
                    == oldGeneration.AdjustmentHistoryId
                    && f.StockPostingId == oldGeneration.WritePostingId
                    && f.OriginalValuationFactId == null
                    && f.ReversesValuationFactId == null,
                    cancellationToken);
        var oldPhysical = new StockReplayPhysicalEvent(-oldGeneration.Id,
            oldEvent.Key, oldGeneration.SignedAdjustmentQty,
            line.PhysicalQty);
        var expectedOld = StockReplayWacCountEventFactory.Create(
            oldPhysical, oldGeneration,
            oldFact is null ? [] : [oldFact], basis, originalFact);
        if (oldEvent != expectedOld
            || oldFact is not null && (oldFact.TransactionCurrency is not null
                || oldFact.TransactionCostAmount is not null
                || oldFact.ExchangeRate is not null
                || oldFact.BaseCostAmount != oldFact.CostAmount))
            throw Invalid($"Count line {countLineId} differs from the planned WAC authority.");
        var preCountPileQty = IvQty.Round(line.PhysicalQty!.Value
            - step.SignedQuantity);
        if (preCountPileQty < 0m)
            throw Invalid($"Count line {countLineId} has a negative pre-count pile.");
        var replayed = StockReplayWacSimulator.SimulateInsertion(
            step.QuantityBefore, step.ValueBefore,
            oldEvent with
            {
                OldSignedQuantity = 0m,
                OldSignedValue = 0m,
                ReplaySignedQuantity = step.SignedQuantity,
                CountPreQuantity = preCountPileQty
            }, []);
        if (replayed.Candidate != step)
            throw Invalid($"Count line {countLineId} has an invalid projected WAC value.");

        var periodRefusal = await IvPeriodCloseGuard.EnsureOpenAsync(db,
            context.CompanyCode, context.BranchCode,
            oldGeneration.EffectiveAt, cancellationToken);
        if (periodRefusal is not null)
            throw new StockLedgerException(new(StockLedgerErrorCodes.ClosedPeriod,
                periodRefusal));
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
            throw Invalid("Changed-count root valuation lines are already used.");

        StockValuationFact? reversalFact = null;
        if (oldFact is not null)
        {
            var reversals = await StockReplayValuationReversalWriter.AppendAsync(
                context, oldGeneration.AdjustmentHistoryId!.Value,
                [new StockReplayValuationReversalLine(oldFact.Id,
                    reversalPostingLineNo, 0,
                    activeGeneration.ReversalHistoryId)],
                cancellationToken);
            reversalFact = reversals.Single();
            var reversalHistory = await db.IvTrxHistories.SingleAsync(h =>
                h.Id == activeGeneration.ReversalHistoryId, cancellationToken);
            if (reversalHistory.PostingLineNo != reversalPostingLineNo)
                throw Invalid("Count reversal fact differs from its ADJ history line.");
            reversalHistory.ValuationStatus = StockValuationStatuses.Reversed;
        }

        StockValuationFact? replacementFact = null;
        if (activeGeneration.AdjustmentHistoryId is int newHistoryId)
        {
            var history = await db.IvTrxHistories.SingleAsync(h =>
                h.Id == newHistoryId, cancellationToken);
            if (history.PostingLineNo != replacementPostingLineNo
                || history.StockPostingId != context.Posting.Id
                || history.LedgerEpochId != context.Epoch.Id
                || history.ICode != wacPermit.ItemCode
                || history.EntryRole != "ORIGINAL"
                || history.BatchStatus != "POSTED"
                || history.TrxDtTime != anchor.BusinessDate.Date
                || history.StockCountCostBasisId != (step.SignedQuantity > 0m
                    ? basis!.Id : null)
                || string.IsNullOrWhiteSpace(step.SignedQuantity > 0m
                    ? history.ToStdUom : history.FrStdUom))
                throw Invalid("Count replacement ADJ history differs from its WAC projection.");
            var direction = Math.Sign(step.SignedQuantity);
            var quantity = Math.Abs(step.SignedQuantity);
            var amount = Math.Abs(step.SignedValue);
            var fallback = direction > 0 && step.QuantityBefore == 0m;
            replacementFact = new StockValuationFact
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = replacementPostingLineNo,
                SplitOrdinal = 0,
                SourceLineId = oldFact?.SourceLineId
                    ?? $"IV_STOCK_COUNT:{anchor.SourceDocumentId}:{history.TrxLineNo}:{replacementPostingLineNo}:0",
                SourceDocumentType = oldFact?.SourceDocumentType
                    ?? anchor.SourceDocumentType,
                SourceDocumentId = oldFact?.SourceDocumentId
                    ?? anchor.SourceDocumentId,
                SourceDocumentNo = oldFact?.SourceDocumentNo
                    ?? anchor.SourceDocumentNo,
                SourceDocumentLine = oldFact?.SourceDocumentLine
                    ?? history.TrxLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EffectiveAt = oldGeneration.EffectiveAt,
                BusinessDate = anchor.BusinessDate,
                PeriodKey = anchor.PeriodKey,
                ItemCode = wacPermit.ItemCode,
                WarehouseCode = direction > 0 ? history.ToWarehouse : history.FrWarehouse,
                LocationCode = direction > 0 ? history.ToLocation : history.FrLocation,
                LotId = direction > 0 ? history.ToLotId : history.FromLotId,
                LotNo = direction > 0 ? history.ToLotNo : history.FrLotNo,
                ItemStatus = history.IStatus,
                BaseUom = direction > 0 ? history.ToStdUom ?? "" : history.FrStdUom ?? "",
                MovementCode = direction > 0 ? "ADJUST_IN" : "ADJUST_OUT",
                Direction = direction,
                BaseQty = quantity,
                CostMethod = StockCostMethods.MovingAverage,
                UnitCost = StockLedgerPrecision.Money(amount / quantity),
                CostAmount = amount,
                BaseCurrency = oldFact?.BaseCurrency,
                BaseCostAmount = amount,
                ValuationSource = fallback
                    ? basis!.CostBasisKind == StockCountCostBasisKinds.LedgerWac
                        ? StockValuationSources.CountFrozenV2
                        : StockValuationSources.ManualApproved
                    : StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = 1,
                InventoryHistoryId = history.Id,
                FromBalLocId = history.FromBalLocId,
                ToBalLocId = history.ToBalLocId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            };
            db.StockValuationFacts.Add(replacementFact);
            history.ValuationStatus = StockValuationStatuses.Valued;
        }
        return new(reversalFact, replacementFact);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
