using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Values every counted pile of one item in canonical count order against the
/// same WAC pool. Both permits are obtained before any count mutation.
/// </summary>
public static class StockReplayWacInitialCountSheetValuationWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        StockCountSheetWritePermit countPermit,
        StockReplayWacWritePermit wacPermit,
        StockReplayWacCountSheetInsertionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(countPermit);
        ArgumentNullException.ThrowIfNull(wacPermit);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        StockReplayWacCountSheetInsertionPlanner.ValidatePreparedPlan(plan);
        var selected = plan.CountedPiles.OrderBy(p => p.Candidate.Key).ToArray();
        var candidateIds = selected.Select(p => p.Candidate.EventId).ToHashSet();
        var steps = new[] { plan.Projection.Candidate }
            .Concat(plan.Projection.LaterSteps.Where(s => candidateIds.Contains(s.EventId)))
            .ToDictionary(s => s.EventId);
        var prepared = countPermit.Lines.ToDictionary(l => l.LineId);
        if (context.CostMethod != StockCostMethods.MovingAverage
            || countPermit.CountStatus != "COUNTED"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId != countPermit.CountHeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
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
            || countPermit.PlanHash != plan.PhysicalSheet.PlanHash
            || countPermit.CountHeaderId != plan.PhysicalSheet.CountHeaderId
            || wacPermit.ItemCode != plan.ItemCode
            || wacPermit.PlanHash != plan.PlanHash
            || wacPermit.CurrentQuantity != plan.Tail.CurrentQuantity
            || wacPermit.CurrentValue != plan.Tail.CurrentValue
            || wacPermit.FinalQuantity != plan.Projection.FinalQuantity
            || wacPermit.FinalValue != plan.Projection.FinalValue
            || wacPermit.ChangedPiles.Count != selected.Length
            || steps.Count != selected.Length
            || selected.Any(p => p.CountLineId is not int lineId
                || !prepared.TryGetValue(lineId, out var line)
                || line.BalLocId != p.BalLocId
                || line.PhysicalQty != p.Candidate.AbsoluteCountQty
                || line.SignedAdjustmentQty
                    != p.Projection.Candidate.SignedDelta
                || line.FinalQuantity != p.Projection.FinalQuantity
                || !wacPermit.ChangedPiles.TryGetValue(p.BalLocId,
                    out var finalQuantity)
                || finalQuantity != p.Projection.FinalQuantity
                || p.Candidate.Key.OriginalPostingSequence
                    != context.Posting.PostingSequence
                || p.Candidate.Key.EffectiveAt != context.Posting.EffectiveAt))
            throw Invalid("The count sheet WAC permits do not match the posted root.");

        var db = context.Db;
        var allLineIds = countPermit.Lines.Select(l => l.LineId).ToArray();
        var selectedLineIds = selected.Select(p => p.CountLineId!.Value).ToArray();
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => selectedLineIds.Contains(l.Id))
            .Include(l => l.Header)
            .ToDictionaryAsync(l => l.Id, cancellationToken);
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => selectedLineIds.Contains(g.StockCountLineId)
                && g.CompanyCode == context.CompanyCode
                && g.BranchCode == context.BranchCode)
            .ToArrayAsync(cancellationToken);
        var bases = await db.StockCountCostBases.AsNoTracking()
            .Where(b => allLineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        if (lines.Count != selected.Length
            || generations.Length != selected.Length
            || generations.Select(g => g.StockCountLineId).Distinct().Count()
                != selected.Length
            || !StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, bases))
            throw Invalid("The count sheet lacks exact lines, generations, or frozen bases.");
        var generationByLine = generations.ToDictionary(g => g.StockCountLineId);
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);
        var existingFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == context.Posting.Id
                && f.ItemCode == plan.ItemCode)
            .Select(f => new { f.PostingLineNo, f.InventoryHistoryId })
            .ToArrayAsync(cancellationToken);
        if (existingFacts.Length != 0
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e =>
                e.Entity.StockPostingId == context.Posting.Id
                && e.Entity.ItemCode == plan.ItemCode))
            throw Invalid("The count root already has WAC facts for this item.");
        var newFacts = new List<StockValuationFact>();
        var historiesToValue = new List<ErpWeb.Model.Entities.Inventory.IvTrxHistory>();
        var occupiedLines = new HashSet<int>();
        foreach (var physical in selected)
        {
            var lineId = physical.CountLineId!.Value;
            var line = lines[lineId];
            var generation = generationByLine[lineId];
            var projected = steps[physical.Candidate.EventId];
            var preparedLine = prepared[lineId];
            var basis = basisByLine.GetValueOrDefault(lineId);
            var candidate = plan.Candidates.Single(c =>
                c.EventId == physical.Candidate.EventId);
            if (line.BalLocId != physical.BalLocId
                || line.ICode != plan.ItemCode
                || line.PhysicalQty != preparedLine.PhysicalQty
                || line.Header.Id != countPermit.CountHeaderId
                || line.Header.CompanyCode != context.CompanyCode
                || line.Header.BranchCode != context.BranchCode
                || line.Header.Status != "POSTED"
                || line.Header.CountNo != context.Posting.SourceDocumentNo
                || line.Header.CountDate != context.Posting.BusinessDate
                || generation.IsRemoval
                || generation.AnchorPostingId != context.Posting.Id
                || generation.WritePostingId != context.Posting.Id
                || generation.AnchorPostingSequence
                    != context.Posting.PostingSequence
                || generation.EffectiveAt != context.Posting.EffectiveAt
                || generation.SignedAdjustmentQty != projected.SignedQuantity
                || generation.AdjustmentHistoryId.HasValue
                    != (projected.SignedQuantity != 0m)
                || (line.PhysicalQty > 0m
                    ? basis is null || basis.CompanyCode != context.CompanyCode
                        || basis.BranchCode != context.BranchCode
                        || !StockCountAnchorCommandFactory.ValidBasis(basis)
                    : basis is not null)
                || candidate != StockReplayWacCountEventFactory.CreateInitial(
                    physical, basis))
                throw Invalid($"Count line {lineId} differs from its frozen WAC authority.");
            var bound = StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
                candidate, physical.Projection.Candidate);
            var verified = StockReplayWacSimulator.SimulateInsertion(
                projected.QuantityBefore, projected.ValueBefore, bound, []);
            if (verified.Candidate != projected)
                throw Invalid($"Count line {lineId} has a mismatched WAC projection.");
            if (projected.SignedQuantity == 0m)
            {
                if (projected.SignedValue != 0m)
                    throw Invalid($"Zero count line {lineId} has a valuation.");
                continue;
            }
            var history = await db.IvTrxHistories.SingleOrDefaultAsync(h =>
                h.Id == generation.AdjustmentHistoryId, cancellationToken);
            var direction = Math.Sign(projected.SignedQuantity);
            var quantity = Math.Abs(projected.SignedQuantity);
            var amount = Math.Abs(projected.SignedValue);
            if (history is null || history.StockPostingId != context.Posting.Id
                || history.LedgerEpochId != context.Epoch.Id
                || history.PostingLineNo is not int postingLineNo
                || postingLineNo <= 0 || !occupiedLines.Add(postingLineNo)
                || existingFacts.Any(f => f.PostingLineNo == postingLineNo
                    || f.InventoryHistoryId == history.Id)
                || history.CompanyCode != context.CompanyCode
                || history.BranchCode != context.BranchCode
                || history.ICode != plan.ItemCode
                || history.TrxDtTime != line.Header.CountDate
                || history.TrxType != IvTrxTypes.StockAdjustment
                || history.BatchStatus != IvBatchStatuses.Posted
                || history.EntryRole != "ORIGINAL"
                || history.DocumentRevision != 0
                || history.ValuationStatus != StockValuationStatuses.Unvalued
                || (direction > 0
                    ? history.ToBalLocId != physical.BalLocId
                        || history.ToStdQty != quantity
                        || history.StockCountCostBasisId != basis!.Id
                        || string.IsNullOrWhiteSpace(history.ToStdUom)
                    : history.FromBalLocId != physical.BalLocId
                        || history.FrStdQty != quantity
                        || history.StockCountCostBasisId is not null
                        || string.IsNullOrWhiteSpace(history.FrStdUom)))
                throw Invalid($"Count line {lineId} has no unvalued original ADJ history.");
            var fallback = direction > 0 && projected.QuantityBefore == 0m;
            newFacts.Add(new StockValuationFact
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
            });
            historiesToValue.Add(history);
        }
        db.StockValuationFacts.AddRange(newFacts);
        foreach (var history in historiesToValue)
            history.ValuationStatus = StockValuationStatuses.Valued;
        return newFacts;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
