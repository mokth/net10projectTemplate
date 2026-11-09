using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

// Verifies complete, closed FG correction generations before another replay.
internal static class ProductionFinishedGoodCorrectionHistory
{
    internal sealed record Cleared(long LastFactId, long LastSequence, int Count);

    public static async Task<Cleared?> ReadAsync(AppDbContext db, string company, string branch,
        long movementId, long? activeRestatementId, long? activeDeltaId, CancellationToken ct,
        long? activeDeltaReversalId = null)
    {
        var fact = await db.ProductionFinishedGoodFactRows.AsNoTracking().SingleOrDefaultAsync(f =>
            f.ProductionMovementId == movementId && f.ReversesFactId == null, ct);
        if (fact is null || (activeRestatementId is null) != (activeDeltaId is null)
            || activeDeltaReversalId is not null && activeDeltaId is null) return null;
        var movement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == movementId, ct);
        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == fact.InventoryHistoryId, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == fact.StockPostingId, ct);
        var receipts = await db.StockValuationFacts.AsNoTracking().Where(v =>
            v.InventoryHistoryId == history.Id && v.StockPostingId == owner.Id
            && v.OriginalValuationFactId == null && v.ReversesValuationFactId == null).ToArrayAsync(ct);
        if (receipts.Length != 1 || owner.CompanyCode != company || owner.BranchCode != branch
            || owner.SealedAtUtc is null || movement.TotalCost != fact.TotalValue) return null;
        var receipt = receipts[0];
        if (receipt.CostMethod != StockCostMethods.MovingAverage || receipt.CostAmount != fact.TotalValue
            || receipt.BaseQty != fact.BaseQty || receipt.BaseQty <= 0m) return null;
        var physicalDescendants = await db.StockValuationFacts.AsNoTracking().Where(v =>
            v.OriginalValuationFactId == receipt.Id && v.InventoryHistoryId != null).ToArrayAsync(ct);
        if (physicalDescendants.Length > (activeDeltaReversalId is null ? 0 : 1)
            || physicalDescendants.Any(v => v.ReversesValuationFactId != receipt.Id)) return null;
        var production = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(r => r.ProductionBalLotMovementId == movementId).ToArrayAsync(ct);
        var deltas = await db.StockValuationFacts.AsNoTracking().Where(v =>
            v.OriginalValuationFactId == receipt.Id && v.InventoryHistoryId == null).ToArrayAsync(ct);
        if (activeRestatementId is long activeProduction
            && (production.Count(r => r.Id == activeProduction && r.ReversesRestatementFactId == null) != 1
                || production.Any(r => r.ReversesRestatementFactId == activeProduction)
                || deltas.Count(d => d.Id == activeDeltaId && d.ReversesValuationFactId == null) != 1
                || deltas.Any(d => d.ReversesValuationFactId == activeDeltaId && d.Id != activeDeltaReversalId)
                || activeDeltaReversalId is long inverseId && deltas.Count(d =>
                    d.Id == inverseId && d.ReversesValuationFactId == activeDeltaId) != 1)) return null;
        var closedProduction = production.Where(r => r.Id != activeRestatementId).ToArray();
        var closedDeltas = deltas.Where(d => d.Id != activeDeltaId && d.Id != activeDeltaReversalId).ToArray();
        var replacements = closedProduction.Where(r => r.ReversesRestatementFactId == null).ToArray();
        if (closedProduction.Length != replacements.Length * 2 || closedDeltas.Length != closedProduction.Length
            || closedDeltas.Count(d => d.ReversesValuationFactId == null) != replacements.Length) return null;

        var rootIds = closedDeltas.Select(d => d.StockPostingId).Distinct().ToArray();
        var roots = await db.StockPostings.AsNoTracking().Where(p => rootIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var sourceIds = roots.Values.Where(p => p.PostingRole == "REVERSAL" && p.ReversesPostingId.HasValue)
            .Select(p => p.ReversesPostingId!.Value).Distinct().ToArray();
        var sources = await db.StockPostings.AsNoTracking().Where(p => sourceIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var sourceReversals = await db.StockPostings.AsNoTracking().Where(p =>
            p.ReversesPostingId.HasValue && sourceIds.Contains(p.ReversesPostingId.Value)
            && p.SealedAtUtc != null).ToArrayAsync(ct);
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r => rootIds.Contains(r.RootStockPostingId))
            .ToDictionaryAsync(r => r.RootStockPostingId, ct);
        var deltaIds = closedDeltas.Select(d => d.Id).ToArray();
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && deltaIds.Contains(e.AffectedEntityId))
            .ToArrayAsync(ct);
        if (roots.Count != rootIds.Length || runs.Count != rootIds.Length || effects.Length != closedDeltas.Length
            || effects.GroupBy(e => e.AffectedEntityId).Any(g => g.Count() != 1)) return null;
        var intervals = new List<(long Start, long End, long LastFact)>();
        foreach (var row in replacements)
        {
            var reversals = closedProduction.Where(r => r.ReversesRestatementFactId == row.Id).ToArray();
            var matchingDeltas = closedDeltas.Where(d => d.ReversesValuationFactId == null
                && d.StockPostingId == row.ReplayStockPostingId).ToArray();
            if (reversals.Length != 1 || matchingDeltas.Length != 1) return null;
            var delta = matchingDeltas[0];
            var inverse = closedDeltas.Where(d => d.ReversesValuationFactId == delta.Id).ToArray();
            if (inverse.Length != 1 || inverse[0].StockPostingId != reversals[0].ReplayStockPostingId
                || !roots.TryGetValue(delta.StockPostingId, out var root)
                || !roots.TryGetValue(inverse[0].StockPostingId, out var rollback)
                || !runs.TryGetValue(root.Id, out var run) || !runs.TryGetValue(rollback.Id, out var rollbackRun)) return null;
            var reverse = inverse[0];
            var ordinaryPair = root.PostingRole == "PRIMARY" && root.CommandType == $"IV_POST_{root.SourceDocumentType}"
                && rollback.PostingRole == "REVERSAL" && rollback.CommandType == $"IV_ROLLBACK_{root.SourceDocumentType}"
                && rollback.ReversesPostingId == root.Id && rollback.DocumentRevision == root.DocumentRevision;
            var restoredSourcePair = root.PostingRole == "REVERSAL"
                && root.CommandType == $"IV_ROLLBACK_{root.SourceDocumentType}"
                && root.ReversesPostingId is long sourceId && sources.TryGetValue(sourceId, out var source)
                && source.CompanyCode == company && source.BranchCode == branch && source.SealedAtUtc is not null
                && source.LedgerEpochId == owner.LedgerEpochId && source.SourceModule == "INVENTORY"
                && source.SourceDocumentType == root.SourceDocumentType && source.SourceDocumentId == root.SourceDocumentId
                && source.SourceDocumentNo == root.SourceDocumentNo && source.PostingRole == "PRIMARY"
                && source.CommandType == $"IV_POST_{root.SourceDocumentType}"
                && source.PostingSequence < root.PostingSequence && root.DocumentRevision == source.DocumentRevision
                && source.EffectiveAt == root.EffectiveAt && source.BusinessDate == root.BusinessDate && source.PeriodKey == root.PeriodKey
                && sourceReversals.Count(p => p.ReversesPostingId == source.Id) == 1
                && sourceReversals.Any(p => p.Id == root.Id)
                && rollback.PostingRole == "PRIMARY" && rollback.CommandType == $"IV_POST_{root.SourceDocumentType}"
                && rollback.ReversesPostingId is null && rollback.DocumentRevision > source.DocumentRevision
                && rollback.EffectiveAt == source.EffectiveAt && rollback.BusinessDate == source.BusinessDate
                && rollback.PeriodKey == source.PeriodKey;
            if (root.CompanyCode != company || root.BranchCode != branch || root.SealedAtUtc is null
                || rollback.CompanyCode != company || rollback.BranchCode != branch || rollback.SealedAtUtc is null
                || root.LedgerEpochId != owner.LedgerEpochId || rollback.LedgerEpochId != owner.LedgerEpochId
                || root.SourceModule != "INVENTORY" || root.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                || rollback.SourceModule != root.SourceModule || rollback.SourceDocumentType != root.SourceDocumentType
                || rollback.SourceDocumentId != root.SourceDocumentId || rollback.SourceDocumentNo != root.SourceDocumentNo
                || !ordinaryPair && !restoredSourcePair
                || root.PostingSequence <= owner.PostingSequence || rollback.PostingSequence <= root.PostingSequence
                || root.EffectiveAt > owner.EffectiveAt || root.EffectiveAt.Year != owner.EffectiveAt.Year
                || root.EffectiveAt.Month != owner.EffectiveAt.Month
                || run.CompanyCode != company || run.BranchCode != branch || rollbackRun.CompanyCode != company || rollbackRun.BranchCode != branch
                || root.RequestId != run.RootRequestId || rollback.RequestId != rollbackRun.RootRequestId
                || run.RootDocumentType != root.SourceDocumentType || run.RootDocumentId != root.SourceDocumentId
                || rollbackRun.RootDocumentType != rollback.SourceDocumentType || rollbackRun.RootDocumentId != rollback.SourceDocumentId
                || run.EarliestEffectiveAt > owner.EffectiveAt || run.LatestEffectiveAt < owner.EffectiveAt
                || rollbackRun.EarliestEffectiveAt > owner.EffectiveAt || rollbackRun.LatestEffectiveAt < owner.EffectiveAt
                || string.IsNullOrWhiteSpace(run.PlanHash) || string.IsNullOrWhiteSpace(rollbackRun.PlanHash)
                || delta.SignedValue != row.DeltaCost || delta.ValuationSource != StockValuationSources.ProductionActual
                || delta.BaseCostAmount != delta.CostAmount || delta.ValuationVersion != receipt.ValuationVersion + 1
                || delta.UnitCost != StockLedgerPrecision.Money(delta.CostAmount / receipt.BaseQty)
                || delta.MovementCode != receipt.MovementCode || delta.BaseUom != receipt.BaseUom
                || delta.FromBalLocId != receipt.FromBalLocId || delta.ToBalLocId != receipt.ToBalLocId
                || delta.WarehouseCode != receipt.WarehouseCode || delta.LocationCode != receipt.LocationCode
                || delta.LotId != receipt.LotId || delta.LotNo != receipt.LotNo || delta.ItemStatus != receipt.ItemStatus
                || delta.TransactionCurrency is not null || delta.TransactionCostAmount is not null || delta.ExchangeRate is not null
                || reverse.Direction != -delta.Direction || reverse.CostAmount != delta.CostAmount
                || reverse.BaseCostAmount != delta.BaseCostAmount || reverse.BaseQty != 0m
                || reverse.ValuationStatus != StockValuationStatuses.Reversed || reverse.ValuationSource != StockValuationSources.OriginalReversal
                || reverse.ValuationVersion != delta.ValuationVersion || reverse.CompanyCode != company || reverse.BranchCode != branch
                || reverse.LedgerEpochId != owner.LedgerEpochId || reverse.ItemCode != delta.ItemCode
                || reverse.CostMethod != StockCostMethods.MovingAverage || reverse.EffectiveAt != delta.EffectiveAt
                || reverse.BusinessDate != delta.BusinessDate || reverse.PeriodKey != delta.PeriodKey
                || reverse.SourceLineId != delta.SourceLineId || reverse.SourceDocumentType != delta.SourceDocumentType
                || reverse.SourceDocumentId != delta.SourceDocumentId || reverse.SourceDocumentNo != delta.SourceDocumentNo
                || reverse.SourceDocumentLine != delta.SourceDocumentLine || reverse.MovementCode != delta.MovementCode
                || reverse.BaseCurrency != delta.BaseCurrency || reverse.BaseUom != delta.BaseUom
                || reverse.WarehouseCode != delta.WarehouseCode || reverse.LocationCode != delta.LocationCode
                || reverse.LotId != delta.LotId || reverse.LotNo != delta.LotNo || reverse.ItemStatus != delta.ItemStatus
                || reverse.FromBalLocId != delta.FromBalLocId || reverse.ToBalLocId != delta.ToBalLocId
                || effects.Any(e => (e.AffectedEntityId == delta.Id || e.AffectedEntityId == reverse.Id)
                    && (e.CompanyCode != company || e.BranchCode != branch || e.EffectKind != StockReplayEffectTypes.FinishedGoodReceiptValue
                        || e.BusinessOwnerPostingId != owner.Id || e.OriginalEntityId != receipt.Id))
                || effects.Count(e => e.AffectedEntityId == delta.Id && e.RootStockPostingId == root.Id && e.ReplayRunId == run.Id) != 1
                || effects.Count(e => e.AffectedEntityId == reverse.Id && e.RootStockPostingId == rollback.Id && e.ReplayRunId == rollbackRun.Id) != 1)
                return null;
            StockReplayWacValueEventFactory.CreateReceiptRestatement(delta, receipt, history, owner, root);
            intervals.Add((root.PostingSequence, rollback.PostingSequence, reverse.Id));
        }
        var ordered = intervals.OrderBy(i => i.Start).ToArray();
        if (ordered.Zip(ordered.Skip(1), (a, b) => a.End < b.Start).Any(valid => !valid)) return null;
        if (activeDeltaId is long activeId && ordered.Length > 0)
        {
            var activeRootId = deltas.Single(d => d.Id == activeId).StockPostingId;
            var activeRoot = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p =>
                p.Id == activeRootId, ct);
            if (activeRoot is null || activeRoot.PostingSequence <= ordered[^1].End) return null;
        }
        await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, [movementId], ct);
        return ordered.Length == 0 ? new(0, 0, 0) : new(ordered[^1].LastFact, ordered[^1].End, ordered.Length);
    }

    public static async Task<bool> MatchesOutputAsync(AppDbContext db, long finishedId, long produceId, CancellationToken ct)
    {
        var finished = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == finishedId, ct);
        var produce = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == produceId, ct);
        if (produce.BaseQty <= 0m || finished.BaseQty <= 0m || finished.BaseQty > produce.BaseQty) return false;
        var rows = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(r => r.ProductionBalLotMovementId == finishedId || r.ProductionBalLotMovementId == produceId).ToArrayAsync(ct);
        var fg = rows.Where(r => r.ProductionBalLotMovementId == finishedId).ToArray();
        var output = rows.Where(r => r.ProductionBalLotMovementId == produceId).ToArray();
        if (fg.GroupBy(r => r.ReplayStockPostingId).Any(g => g.Count() != 1)
            || output.GroupBy(r => r.ReplayStockPostingId).Any(g => g.Count() != 1)) return false;
        decimal Share(decimal value) => finished.BaseQty == produce.BaseQty ? value
            : StockLedgerPrecision.Money(value * finished.BaseQty / produce.BaseQty);
        var shiftedBaseline = output.Length == fg.Length + 1 && fg.Length > 0;
        if (fg.Length != output.Length && !shiftedBaseline) return false;
        Dictionary<long, StockPosting>? owners = null;
        StockPosting? firstSource = null;
        if (shiftedBaseline)
        {
            await ProductionReplayEffectiveCostReader.ReadAsync(db, finished.CompanyCode, finished.BranchCode,
                [finishedId, produceId], ct);
            var extra = output.Where(r => fg.All(f => f.ReplayStockPostingId != r.ReplayStockPostingId)).ToArray();
            if (extra.Length != 1 || extra[0].ReversesRestatementFactId is not null
                || Share(extra[0].PriorEffectiveTotalCost) != Share(produce.TotalCost)
                || Share(extra[0].CorrectedEffectiveTotalCost) != finished.TotalCost
                || finished.StockPostingId is not long fgOwnerId) return false;
            var ids = output.Select(r => r.ReplayStockPostingId).Append(fgOwnerId).Distinct().ToArray();
            owners = await db.StockPostings.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            if (!owners.TryGetValue(extra[0].ReplayStockPostingId, out firstSource)
                || !owners.TryGetValue(fgOwnerId, out var fgOwner)
                || firstSource.CompanyCode != finished.CompanyCode || firstSource.BranchCode != finished.BranchCode
                || firstSource.LedgerEpochId != finished.LedgerEpochId || firstSource.SealedAtUtc is null
                || firstSource.SourceModule != "INVENTORY"
                || firstSource.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                || firstSource.PostingRole != "PRIMARY"
                || firstSource.CommandType != $"IV_POST_{firstSource.SourceDocumentType}"
                || firstSource.PostingSequence >= fgOwner.PostingSequence
                || firstSource.EffectiveAt > fgOwner.EffectiveAt) return false;
        }
        foreach (var row in fg)
        {
            var other = output.SingleOrDefault(r => r.ReplayStockPostingId == row.ReplayStockPostingId);
            if (other is null || Share(other.PriorEffectiveTotalCost) != row.PriorEffectiveTotalCost
                || Share(other.CorrectedEffectiveTotalCost) != row.CorrectedEffectiveTotalCost) return false;
            if (shiftedBaseline)
            {
                if ((row.ReversesRestatementFactId is null) == (other.ReversesRestatementFactId is null)
                    || owners is null || firstSource is null || !owners.TryGetValue(row.ReplayStockPostingId, out var root)
                    || root.SourceModule != firstSource.SourceModule || root.SourceDocumentType != firstSource.SourceDocumentType
                    || root.SourceDocumentId != firstSource.SourceDocumentId || root.SourceDocumentNo != firstSource.SourceDocumentNo
                    || root.EffectiveAt != firstSource.EffectiveAt || root.PostingSequence <= owners[finished.StockPostingId!.Value].PostingSequence
                    || row.ReversesRestatementFactId is null && (root.PostingRole != "REVERSAL"
                        || output.SingleOrDefault(r => r.Id == other.ReversesRestatementFactId)?.ReplayStockPostingId != root.ReversesPostingId)
                    || row.ReversesRestatementFactId is not null && root.PostingRole != "PRIMARY") return false;
            }
            else if ((row.ReversesRestatementFactId is null) != (other.ReversesRestatementFactId is null)) return false;
            if (!shiftedBaseline && row.ReversesRestatementFactId is long originalId
                && (other.ReversesRestatementFactId is not long otherOriginalId
                    || fg.SingleOrDefault(r => r.Id == originalId)?.ReplayStockPostingId
                        != output.SingleOrDefault(r => r.Id == otherOriginalId)?.ReplayStockPostingId)) return false;
        }
        return true;
    }
}
