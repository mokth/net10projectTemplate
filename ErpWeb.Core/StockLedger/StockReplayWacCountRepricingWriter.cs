using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Reprices one unchanged physical count adjustment inside an unsealed WAC
/// replay root. A changed physical delta requires a new count generation and
/// is deliberately outside this cost-only writer.
/// </summary>
public static class StockReplayWacCountRepricingWriter
{
    public static async Task<StockValuationFact?> AppendAsync(
        StockPostingContext context,
        int countLineId,
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
            || countLineId <= 0 || reversalPostingLineNo <= 0
            || replacementPostingLineNo <= 0
            || reversalPostingLineNo == replacementPostingLineNo)
            throw Invalid("Count WAC repricing has invalid root or line identity.");

        var db = context.Db;
        var generation = (await StockCountGenerationResolver.ResolveActiveAsync(
            db, context.CompanyCode, context.BranchCode,
            [countLineId], cancellationToken))[countLineId];
        if (generation.IsRemoval || generation.SignedAdjustmentQty == 0m
            || generation.AdjustmentHistoryId is not int historyId)
            throw Invalid($"Count line {countLineId} has no valued adjustment to reprice.");
        var line = await db.IvStockCountLines.AsNoTracking()
            .SingleAsync(l => l.Id == countLineId, cancellationToken);
        var physicalQty = line.PhysicalQty
            ?? throw Invalid($"Count line {countLineId} has no frozen observation.");
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleAsync(h => h.Id == historyId, cancellationToken);
        if (history.ValuationStatus != StockValuationStatuses.Valued
            || history.LedgerEpochId != context.Epoch.Id)
            throw Invalid($"Count line {countLineId} has no valued V2 history.");
        var basis = await db.StockCountCostBases.AsNoTracking()
            .SingleOrDefaultAsync(b => b.StockCountLineId == countLineId,
                cancellationToken);
        if (physicalQty > 0m
                && (basis is null || history.StockCountCostBasisId != basis.Id)
            || physicalQty == 0m && basis is not null)
            throw Invalid($"Count line {countLineId} has no matching frozen cost basis.");

        var facts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode,
            [history], cancellationToken))[historyId];
        if (facts.Count != 1)
            throw Invalid($"Count line {countLineId} has no single WAC adjustment fact.");
        var prior = facts[0];
        if (prior.TransactionCurrency is not null
            || prior.TransactionCostAmount is not null
            || prior.ExchangeRate is not null
            || prior.BaseCostAmount != prior.CostAmount)
            throw Invalid($"Count line {countLineId} has unsupported currency evidence.");
        StockValuationFact? origin = null;
        if (prior.StockPostingId != generation.WritePostingId)
        {
            origin = await db.StockValuationFacts.AsNoTracking()
                .SingleOrDefaultAsync(f => f.InventoryHistoryId == historyId
                    && f.StockPostingId == generation.WritePostingId
                    && f.OriginalValuationFactId == null
                    && f.ReversesValuationFactId == null,
                    cancellationToken);
        }
        var physical = new StockReplayPhysicalEvent(-generation.Id,
            activeEvent.Key, generation.SignedAdjustmentQty, physicalQty);
        var expected = StockReplayWacCountEventFactory.Create(
            physical, generation, facts, basis, origin);
        var preCountPileQty = IvQty.Round(
            physicalQty - generation.SignedAdjustmentQty);
        if (activeEvent != expected || projectedStep.EventId != activeEvent.EventId
            || projectedStep.SignedQuantity != generation.SignedAdjustmentQty
            || preCountPileQty < 0m
            || projectedStep.QuantityBefore < preCountPileQty
            || projectedStep.ValueBefore < 0m
            || IvQty.Round(projectedStep.QuantityBefore)
                != projectedStep.QuantityBefore
            || StockLedgerPrecision.Money(projectedStep.ValueBefore)
                != projectedStep.ValueBefore)
            throw Invalid($"Count line {countLineId} WAC projection differs from its active adjustment.");
        var delta = generation.SignedAdjustmentQty;
        var replayed = StockReplayWacSimulator.SimulateInsertion(
            projectedStep.QuantityBefore, projectedStep.ValueBefore,
            expected with
            {
                OldSignedQuantity = 0m,
                OldSignedValue = 0m,
                CountPreQuantity = preCountPileQty
            }, []);
        if (projectedStep != replayed.Candidate)
            throw Invalid($"Count line {countLineId} has an invalid projected WAC amount.");
        var signedValue = replayed.Candidate.SignedValue;
        if (signedValue == prior.SignedValue)
            return null;

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
            throw Invalid("A count WAC replay root valuation line is already used.");

        await StockReplayValuationReversalWriter.AppendAsync(context,
            historyId, [new StockReplayValuationReversalLine(
                prior.Id, reversalPostingLineNo, 0)], cancellationToken);
        var amount = Math.Abs(signedValue);
        var source = delta > 0m && projectedStep.QuantityBefore == 0m
            ? basis!.CostBasisKind == StockCountCostBasisKinds.LedgerWac
                ? StockValuationSources.CountFrozenV2
                : StockValuationSources.ManualApproved
            : StockValuationSources.MovingAverage;
        var replacement = new StockValuationFact
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = replacementPostingLineNo,
            SplitOrdinal = 0,
            SourceLineId = prior.SourceLineId,
            SourceDocumentType = prior.SourceDocumentType,
            SourceDocumentId = prior.SourceDocumentId,
            SourceDocumentNo = prior.SourceDocumentNo,
            SourceDocumentLine = prior.SourceDocumentLine,
            EffectiveAt = prior.EffectiveAt,
            BusinessDate = prior.BusinessDate,
            PeriodKey = prior.PeriodKey,
            ItemCode = prior.ItemCode,
            WarehouseCode = prior.WarehouseCode,
            LocationCode = prior.LocationCode,
            LotId = prior.LotId,
            LotNo = prior.LotNo,
            ItemStatus = prior.ItemStatus,
            BaseUom = prior.BaseUom,
            MovementCode = prior.MovementCode,
            Direction = Math.Sign(delta),
            BaseQty = Math.Abs(delta),
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = StockLedgerPrecision.Money(amount / Math.Abs(delta)),
            CostAmount = amount,
            BaseCurrency = prior.BaseCurrency,
            BaseCostAmount = amount,
            ValuationSource = source,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = checked(prior.ValuationVersion + 1),
            InventoryHistoryId = historyId,
            FromBalLocId = prior.FromBalLocId,
            ToBalLocId = prior.ToBalLocId,
            ProductionMovementId = prior.ProductionMovementId,
            WorkOrderId = prior.WorkOrderId,
            WorkOrderOperationId = prior.WorkOrderOperationId,
            ProductionPostingLinkId = prior.ProductionPostingLinkId,
            OriginalValuationFactId = prior.OriginalValuationFactId ?? prior.Id,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        };
        db.StockValuationFacts.Add(replacement);
        return replacement;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
