using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private static async Task<string?> CheckWacTransferDescendantTailAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, DateTime candidateDate,
        int sourcePileId, int destinationPileId,
        IReadOnlyList<StockReplayPhysicalEvent> sourceLater,
        IReadOnlyList<StockReplayPhysicalEvent> destinationLater,
        StockReplayWacFinancialTail tail,
        IReadOnlyList<StockReplayWacStep> projectedSteps,
        CancellationToken cancellationToken)
    {
        const string unsupported =
            "Backdated WAC transfer supports only unchanged Inventory MR/MI and transfer descendants in the same open month.";
        if (tail.LaterEvents.Count is 0 or > 100
            || projectedSteps.Count != tail.LaterEvents.Count
            || !tail.PhysicalByPile.TryGetValue(sourcePileId,
                out var sourceFinancial)
            || !tail.PhysicalByPile.TryGetValue(destinationPileId,
                out var destinationFinancial)
            || !sourceLater.SequenceEqual(sourceFinancial)
            || !destinationLater.SequenceEqual(destinationFinancial)
            || tail.LaterEvents.Any(e => e.EventId is <= 0 or > int.MaxValue
                || e.Key.EffectiveAt.Year != candidateDate.Year
                || e.Key.EffectiveAt.Month != candidateDate.Month
                || e.AbsoluteCountQty is not null
                || e.ReplaySignedQuantity != e.OldSignedQuantity)
            || tail.LaterEvents.Where((e, i) =>
                    projectedSteps[i].EventId != e.EventId
                    || projectedSteps[i].SignedQuantity != e.OldSignedQuantity
                    || projectedSteps[i].SignedValue != e.OldSignedValue)
                .Any())
            return unsupported;
        if (await CheckSimpleWacTailAsync(db, tail.LaterEvents,
                candidateDate, cancellationToken) is string ownerError)
            return ownerError;
        var ids = tail.LaterEvents.Select(e => checked((int)e.EventId)).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => ids.Contains(h.Id)).ToArrayAsync(cancellationToken);
        if (histories.Length != ids.Length
            || histories.Any(h => h.CompanyCode != companyCode
                || h.BranchCode != branchCode || h.ICode != itemCode
                || h.StockPostingId is not > 0))
            return unsupported;
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, companyCode, branchCode, histories, cancellationToken);
        if (await CheckWacTransferDescendantGenerationsAsync(db,
                companyCode, branchCode, histories, active,
                cancellationToken) is string generationError)
            return generationError;
        var eventById = tail.LaterEvents.ToDictionary(e => checked((int)e.EventId));
        var stepById = projectedSteps.ToDictionary(s => checked((int)s.EventId));
        foreach (var history in histories)
        {
            var movement = eventById[history.Id];
            var step = stepById[history.Id];
            var facts = active[history.Id];
            if (facts.Any(f => f.CostMethod != StockCostMethods.MovingAverage
                    || f.BaseCostAmount != f.CostAmount))
                return "A later WAC movement has an inconsistent active generation.";
            switch (history.TrxType)
            {
                case IvTrxTypes.MiscellaneousReceipt:
                    if (history.FromBalLocId is not null
                        || history.ToBalLocId != sourcePileId
                            && history.ToBalLocId != destinationPileId
                        || facts.Count != 1 || facts[0].Direction != 1
                        || facts[0].ValuationSource is not
                            (StockValuationSources.ManualApproved
                                or StockValuationSources.OpeningApproved)
                        || movement.OldSignedQuantity <= 0m
                        || facts[0].CostAmount != movement.OldSignedValue)
                        return unsupported;
                    break;
                case IvTrxTypes.MiscellaneousIssue:
                    if (history.ToBalLocId is not null
                        || history.FromBalLocId != sourcePileId
                            && history.FromBalLocId != destinationPileId
                        || facts.Count != 1 || facts[0].Direction != -1
                        || facts[0].ValuationSource != StockValuationSources.MovingAverage
                        || movement.OldSignedQuantity >= 0m
                        || facts[0].CostAmount != -movement.OldSignedValue)
                        return unsupported;
                    break;
                case IvTrxTypes.StockTransfer:
                    if (history.FromBalLocId is not > 0
                        || history.ToBalLocId is not > 0
                        || history.FromBalLocId != sourcePileId
                            && history.ToBalLocId != sourcePileId
                            && history.FromBalLocId != destinationPileId
                            && history.ToBalLocId != destinationPileId
                        || !tail.CurrentQuantityByPile.ContainsKey(
                            history.FromBalLocId.Value)
                        || !tail.CurrentQuantityByPile.ContainsKey(
                            history.ToBalLocId.Value)
                        || history.FrStdQty is not > 0m
                        || facts.Count != 2
                        || facts.Any(f => f.ValuationSource
                            != StockValuationSources.MovingAverage)
                        || movement.OldSignedQuantity != 0m
                        || facts.Where(f => f.Direction < 0)
                            .Sum(f => f.CostAmount)
                            != WacTransferAmount(history.FrStdQty.Value,
                                step.QuantityBefore, step.ValueBefore))
                        return unsupported;
                    break;
                default:
                    return unsupported;
            }
        }
        return null;
    }

    private static async Task<string?> CheckWacTransferDescendantGenerationsAsync(
        AppDbContext db, string companyCode, string branchCode,
        IReadOnlyList<IvTrxHistory> histories,
        IReadOnlyDictionary<int, IReadOnlyList<StockValuationFact>> active,
        CancellationToken cancellationToken)
    {
        const string unsupported =
            "A later WAC movement has no exact original or Inventory MR/MI correction generation.";
        var corrected = histories.Where(h => active[h.Id]
            .Any(f => f.StockPostingId != h.StockPostingId)).ToArray();
        var correctedIds = corrected.Select(h => h.Id).ToHashSet();
        if (histories.Where(h => !correctedIds.Contains(h.Id))
            .Any(h => active[h.Id]
                .Any(f => f.StockPostingId != h.StockPostingId
                    || f.OriginalValuationFactId is not null)))
            return unsupported;
        if (corrected.Length == 0)
            return null;
        if (corrected.Any(h => h.TrxType is not
                    (IvTrxTypes.StockTransfer or IvTrxTypes.MiscellaneousIssue)
                || active[h.Id]
                .Select(f => f.StockPostingId).Distinct().Count() != 1
                || active[h.Id].Any(f => f.OriginalValuationFactId is not > 0)))
            return unsupported;
        var ownerIds = corrected.Select(h => active[h.Id][0].StockPostingId)
            .Distinct().ToArray();
        var owners = await db.StockPostings.AsNoTracking()
            .Where(p => ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (owners.Count != ownerIds.Length)
            return unsupported;
        var batchIds = new List<int>();
        foreach (var owner in owners.Values)
        {
            if (owner.CompanyCode != companyCode || owner.BranchCode != branchCode
                || owner.SealedAtUtc is null
                || owner.SourceModule != "INVENTORY"
                || owner.SourceDocumentType is not
                    (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.MiscellaneousIssue)
                || owner.PostingRole is not ("PRIMARY" or "REVERSAL")
                || owner.CommandType != $"IV_{(owner.PostingRole == "REVERSAL"
                    ? "ROLLBACK" : "POST")}_{owner.SourceDocumentType}"
                || !int.TryParse(owner.SourceDocumentId,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var batchId))
                return unsupported;
            batchIds.Add(batchId);
        }
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => batchIds.Contains(b.Id)
                && b.CompanyCode == companyCode && b.BranchCode == branchCode)
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (batches.Count != batchIds.Distinct().Count()
            || owners.Values.Any(o => !int.TryParse(o.SourceDocumentId,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var id)
                || !batches.TryGetValue(id, out var batch)
                || batch.TrxType != o.SourceDocumentType
                || batch.BatchStatus != (o.PostingRole == "PRIMARY"
                    ? IvBatchStatuses.Posted : IvBatchStatuses.New)
                || o.EffectiveAt != batch.TrxDtTime
                || o.SourceDocumentNo != batch.BatchNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)))
            return unsupported;
        var originIds = corrected.SelectMany(h => active[h.Id])
            .Select(f => f.OriginalValuationFactId!.Value).ToArray();
        if (originIds.Distinct().Count() != originIds.Length)
            return unsupported;
        var origins = await db.StockValuationFacts.AsNoTracking()
            .Where(f => originIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        if (origins.Count != originIds.Length)
            return unsupported;
        foreach (var history in corrected)
        {
            foreach (var fact in active[history.Id])
            {
                if (!owners.TryGetValue(fact.StockPostingId, out var correction)
                    || correction.LedgerEpochId != history.LedgerEpochId
                    || !origins.TryGetValue(
                        fact.OriginalValuationFactId!.Value, out var original)
                    || original.CompanyCode != companyCode
                    || original.BranchCode != branchCode
                    || original.ItemCode != history.ICode
                    || original.StockPostingId != history.StockPostingId
                    || original.LedgerEpochId != history.LedgerEpochId
                    || original.InventoryHistoryId != history.Id
                    || original.OriginalValuationFactId is not null
                    || original.ReversesValuationFactId is not null
                    || original.ValuationStatus != StockValuationStatuses.Valued
                    || original.EffectiveAt != history.TrxDtTime
                    || original.CostMethod != StockCostMethods.MovingAverage
                    || original.Direction != fact.Direction
                    || original.BaseQty != fact.BaseQty
                    || original.MovementCode != fact.MovementCode
                    || original.FromBalLocId != fact.FromBalLocId
                    || original.ToBalLocId != fact.ToBalLocId)
                    return unsupported;
            }
        }
        return null;
    }
}
