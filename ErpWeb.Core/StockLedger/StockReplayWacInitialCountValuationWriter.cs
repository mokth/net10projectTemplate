using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Appends the initial WAC fact for one historical count line after its ADJ
/// history and generation have been written. Both permits must have been
/// obtained before the count mutated the pile or its header in this root.
/// </summary>
public static class StockReplayWacInitialCountValuationWriter
{
    public static async Task<StockValuationFact?> AppendAsync(
        StockPostingContext context,
        StockCountSheetWritePermit countPermit,
        StockReplayWacWritePermit wacPermit,
        StockReplayWacInsertionPlan wacPlan,
        int countLineId,
        int postingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(countPermit);
        ArgumentNullException.ThrowIfNull(wacPermit);
        ArgumentNullException.ThrowIfNull(wacPlan);
        context.EnsureUnsealed();
        StockReplayWacInsertionPlanner.ValidatePreparedPlan(wacPlan);
        var physicalPlan = wacPlan.PhysicalPlan;
        var projected = wacPlan.Projection.Candidate;
        var prepared = countPermit.Lines.Where(l => l.LineId == countLineId)
            .ToArray();
        if (context.CostMethod != StockCostMethods.MovingAverage
            || countLineId <= 0 || postingLineNo <= 0
            || countPermit.CountStatus != "COUNTED"
            || countPermit.RootPostingId != context.Posting.Id
            || wacPermit.RootPostingId != context.Posting.Id
            || countPermit.LedgerEpochId != context.Epoch.Id
            || wacPermit.LedgerEpochId != context.Epoch.Id
            || countPermit.CompanyCode != context.CompanyCode
            || countPermit.BranchCode != context.BranchCode
            || wacPermit.CompanyCode != context.CompanyCode
            || wacPermit.BranchCode != context.BranchCode
            || countPermit.PostingSequence != context.Posting.PostingSequence
            || wacPermit.RootPostingSequence != context.Posting.PostingSequence
            || countPermit.EffectiveAt != context.Posting.EffectiveAt
            || physicalPlan.CountHeaderId != countPermit.CountHeaderId
            || physicalPlan.CountLineId != countLineId
            || physicalPlan.CountStatus != "COUNTED"
            || physicalPlan.LedgerEpochId != context.Epoch.Id
            || physicalPlan.Candidate.Key.OriginalPostingSequence
                != context.Posting.PostingSequence
            || physicalPlan.Candidate.Key.EffectiveAt != context.Posting.EffectiveAt
            || physicalPlan.Candidate.EventId != projected.EventId
            || wacPlan.Candidate.EventId != projected.EventId
            || wacPlan.Candidate.Key != physicalPlan.Candidate.Key
            || wacPermit.ItemCode != wacPlan.ItemCode
            || wacPermit.PlanHash != wacPlan.PlanHash
            || wacPermit.ChangedPileId != physicalPlan.BalLocId
            || wacPermit.ChangedPileFinalQuantity
                != physicalPlan.Projection.FinalQuantity
            || prepared.Length != 1
            || prepared[0].BalLocId != physicalPlan.BalLocId
            || prepared[0].PhysicalQty
                != physicalPlan.Candidate.AbsoluteCountQty
            || prepared[0].SignedAdjustmentQty != projected.SignedQuantity
            || prepared[0].FinalQuantity
                != physicalPlan.Projection.FinalQuantity)
            throw Invalid("The initial count WAC fact has no matching prepared replay permits.");

        var db = context.Db;
        var line = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.Id == countLineId)
            .Select(l => new
            {
                l.Id, l.BalLocId, l.ICode, l.PhysicalQty,
                StockCountId = l.Header.Id,
                l.Header.CountNo, l.Header.CountDate,
                l.Header.Status, l.Header.CompanyCode, l.Header.BranchCode
            }).SingleOrDefaultAsync(cancellationToken);
        if (line is null || line.Id != countLineId
            || line.BalLocId != physicalPlan.BalLocId
            || line.ICode != wacPlan.ItemCode
            || line.PhysicalQty != prepared[0].PhysicalQty
            || line.StockCountId != countPermit.CountHeaderId
            || line.CompanyCode != context.CompanyCode
            || line.BranchCode != context.BranchCode
            || line.Status != "POSTED"
            || line.CountNo != context.Posting.SourceDocumentNo
            || line.CountDate != context.Posting.BusinessDate
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId != countPermit.CountHeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid($"Count line {countLineId} differs from its WAC posting owner.");
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.StockCountLineId == countLineId
                && g.CompanyCode == context.CompanyCode
                && g.BranchCode == context.BranchCode)
            .ToArrayAsync(cancellationToken);
        var generation = StockCountGenerationResolver.ResolveActive(generations)
            [countLineId];
        if (generations.Length != 1 || generation.IsRemoval
            || generation.AnchorPostingId != context.Posting.Id
            || generation.WritePostingId != context.Posting.Id
            || generation.AnchorPostingSequence
                != context.Posting.PostingSequence
            || generation.EffectiveAt != context.Posting.EffectiveAt
            || generation.SignedAdjustmentQty != projected.SignedQuantity
            || generation.AdjustmentHistoryId.HasValue
                != (projected.SignedQuantity != 0m))
            throw Invalid($"Count line {countLineId} has no exact initial generation.");

        var basis = await db.StockCountCostBases.AsNoTracking()
            .SingleOrDefaultAsync(b => b.StockCountLineId == countLineId,
                cancellationToken);
        if (line.PhysicalQty > 0m
            && (basis is null || basis.CompanyCode != context.CompanyCode
                || basis.BranchCode != context.BranchCode
                || !StockCountAnchorCommandFactory.ValidBasis(basis))
            || line.PhysicalQty == 0m && basis is not null
            || !StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, basis is null ? [] : [basis])
            || wacPlan.Candidate
                != StockReplayWacCountEventFactory.CreateInitial(
                    physicalPlan, basis))
            throw Invalid($"Count line {countLineId} differs from its frozen WAC basis.");
        var candidate = StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
            wacPlan.Candidate, physicalPlan.Projection.Candidate);
        var verified = StockReplayWacSimulator.SimulateInsertion(
            wacPlan.Projection.HistoricalQuantityBeforeCandidate,
            wacPlan.Projection.HistoricalValueBeforeCandidate,
            candidate, []);
        if (verified.Candidate != projected)
            throw Invalid($"Count line {countLineId} has a mismatched WAC projection.");

        if (projected.SignedQuantity == 0m)
        {
            if (projected.SignedValue != 0m
                || await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                    f.StockPostingId == context.Posting.Id
                    && f.PostingLineNo == postingLineNo, cancellationToken))
                throw Invalid("A zero count adjustment has an unexpected valuation.");
            return null;
        }
        var history = await db.IvTrxHistories.SingleOrDefaultAsync(h =>
            h.Id == generation.AdjustmentHistoryId, cancellationToken);
        var direction = Math.Sign(projected.SignedQuantity);
        var quantity = Math.Abs(projected.SignedQuantity);
        var amount = Math.Abs(projected.SignedValue);
        if (history is null || history.StockPostingId != context.Posting.Id
            || history.LedgerEpochId != context.Epoch.Id
            || history.PostingLineNo != postingLineNo
            || history.CompanyCode != context.CompanyCode
            || history.BranchCode != context.BranchCode
            || history.ICode != wacPlan.ItemCode
            || history.TrxDtTime != line.CountDate
            || history.TrxType != IvTrxTypes.StockAdjustment
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.EntryRole != "ORIGINAL"
            || history.DocumentRevision != 0
            || history.ValuationStatus != StockValuationStatuses.Unvalued
            || (direction > 0
                ? history.ToBalLocId != physicalPlan.BalLocId
                    || history.ToStdQty != quantity
                    || history.StockCountCostBasisId != basis!.Id
                    || string.IsNullOrWhiteSpace(history.ToStdUom)
                : history.FromBalLocId != physicalPlan.BalLocId
                    || history.FrStdQty != quantity
                    || history.StockCountCostBasisId is not null
                    || string.IsNullOrWhiteSpace(history.FrStdUom))
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.InventoryHistoryId == history.Id
                || f.StockPostingId == context.Posting.Id
                    && f.PostingLineNo == postingLineNo, cancellationToken))
            throw Invalid($"Count line {countLineId} has no unvalued original ADJ history.");

        var fallback = direction > 0 && projected.QuantityBefore == 0m;
        var fact = new StockValuationFact
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLineNo,
            SplitOrdinal = 0,
            SourceLineId = $"IV_STOCK_COUNT:{countPermit.CountHeaderId}:{history.TrxLineNo}:{postingLineNo}:0",
            SourceDocumentType = context.Posting.SourceDocumentType,
            SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo,
            SourceDocumentLine = history.TrxLineNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            EffectiveAt = context.Posting.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = wacPlan.ItemCode,
            WarehouseCode = direction > 0 ? history.ToWarehouse : history.FrWarehouse,
            LocationCode = direction > 0 ? history.ToLocation : history.FrLocation,
            LotId = direction > 0 ? history.ToLotId : history.FromLotId,
            LotNo = direction > 0 ? history.ToLotNo : history.FrLotNo,
            ItemStatus = history.IStatus,
            BaseUom = direction > 0 ? history.ToStdUom! : history.FrStdUom!,
            MovementCode = direction > 0 ? "ADJUST_IN" : "ADJUST_OUT",
            Direction = direction,
            BaseQty = quantity,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = StockLedgerPrecision.Money(amount / quantity),
            CostAmount = amount,
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
        db.StockValuationFacts.Add(fact);
        history.ValuationStatus = StockValuationStatuses.Valued;
        return fact;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
