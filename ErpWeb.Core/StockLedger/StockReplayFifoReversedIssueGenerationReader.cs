using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

internal sealed record StockReplayFifoReversedIssueGenerationEvidence(
    StockFifoReversedIssueGeneration Generation,
    IReadOnlyList<StockFifoReversedIssueSplit> Splits,
    long? ActiveIssueRestatementId,
    long? ActiveReversalRestatementId,
    IReadOnlyList<long> ValueFactIds,
    IReadOnlyList<long> RootPostingIds);

/// <summary>
/// Resolves a complete append-only split history for an already-reversed issue.
/// The original issue and reversal remain immutable; a linked generation records
/// the allocation which is effective after one sealed receipt replay.
/// </summary>
internal static class StockReplayFifoReversedIssueGenerationReader
{
    internal const string GenerationEffect = "FIFO_REVERSED_ISSUE_GENERATION";
    internal const string GenerationEntity = "FIFO_REVERSED_ISSUE_GENERATION";
    internal const string ValueEffect = "FIFO_REVERSED_ISSUE_VALUE";
    internal const string ValueMovement = "FIFO_REVERSED_ISSUE_REPLAY_VALUE";

    internal static string SplitHash(StockFifoReversedIssueGeneration generation,
        IEnumerable<StockFifoReversedIssueSplit> splits) =>
        StockPostingFingerprint.Create(new
        {
            generation.CompanyCode, generation.BranchCode, generation.ReplayRunId,
            generation.ReplayStockPostingId, generation.IssueHistoryId, generation.ReversalHistoryId,
            generation.IssueMovementId, generation.ReversalMovementId, generation.PreviousGenerationId,
            baseQty = ExactDecimal(generation.BaseQty), totalCost = ExactDecimal(generation.TotalCost),
            splits = splits.OrderBy(s => s.SplitOrdinal).Select(s => new
            {
                s.CompanyCode, s.BranchCode, s.SplitOrdinal, s.FifoLayerId,
                consumedQty = ExactDecimal(s.ConsumedQty), consumedValue = ExactDecimal(s.ConsumedValue)
            }).ToArray()
        }, new { }).RequestFingerprint;

    private static string ExactDecimal(decimal value) =>
        value.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);

    internal static async Task ValidateForSnapshotAsync(AppDbContext db, string company, string branch,
        IReadOnlyCollection<long> correctionRootIds, CancellationToken ct)
    {
        if (correctionRootIds.Count == 0) return;
        var roots = correctionRootIds.Distinct().ToArray();
        var generations = await db.StockFifoReversedIssueGenerations.AsNoTracking()
            .Where(g => roots.Contains(g.ReplayStockPostingId)).ToArrayAsync(ct);
        if (generations.Length != roots.Length || generations.Select(g => g.ReplayStockPostingId).Distinct().Count() != roots.Length
            || generations.Any(g => g.CompanyCode != company || g.BranchCode != branch))
            throw Invalid("A FIFO period correction has no unique allocation generation.");
        foreach (var group in generations.GroupBy(g => (g.IssueHistoryId, g.ReversalHistoryId, g.IssueMovementId, g.ReversalMovementId)))
        {
            var historyIds = new[] { group.Key.IssueHistoryId, group.Key.ReversalHistoryId };
            var histories = await db.IvTrxHistories.AsNoTracking().Where(h => historyIds.Contains(h.Id))
                .ToDictionaryAsync(h => h.Id, ct);
            var movementIds = new[] { group.Key.IssueMovementId, group.Key.ReversalMovementId };
            var movements = await db.ProductionBalLotMovements.AsNoTracking().Where(m => movementIds.Contains(m.Uid))
                .ToDictionaryAsync(m => m.Uid, ct);
            if (histories.Count != 2 || movements.Count != 2
                || !histories.TryGetValue(group.Key.IssueHistoryId, out var issue)
                || !histories.TryGetValue(group.Key.ReversalHistoryId, out var reversal)
                || !movements.TryGetValue(group.Key.IssueMovementId, out var movement)
                || !movements.TryGetValue(group.Key.ReversalMovementId, out var reverseMovement)
                || issue.StockPostingId is not long issuePostingId)
                throw Invalid("A FIFO period correction has missing issue owners.");
            var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == issuePostingId, ct);
            if (owner is null) throw Invalid("A FIFO period correction has no issue posting.");
            var key = new StockReplayEventKey(issue.TrxDtTime, StockReplayEventKey.OrdinaryClass,
                owner.PostingSequence, $"H{issue.Id:D10}", 0);
            var factIds = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == issue.Id)
                .Select(f => f.Id).ToArrayAsync(ct);
            var rows = await db.StockFifoLayerConsumptions.AsNoTracking()
                .Where(c => factIds.Contains(c.IssueValuationFactId)).ToArrayAsync(ct);
            var baseline = rows.Select(c => new StockReplayFifoConsumptionPosition(c.Id, c.IssueValuationFactId,
                c.FifoLayerId, c.ConsumedQty, c.ConsumedValue, c.SplitOrdinal, true, key)).ToArray();
            var current = await ReadAsync(db, company, branch, issue, reversal, movement, reverseMovement, baseline, ct);
            if (current is null || group.Any(g => !current.RootPostingIds.Contains(g.ReplayStockPostingId)))
                throw Invalid("A FIFO period correction is missing from its effective generation chain.");
        }
    }

    internal static async Task<StockReplayFifoReversedIssueGenerationEvidence?> ReadAsync(
        AppDbContext db, string company, string branch, IvTrxHistory issue, IvTrxHistory reversal,
        ProductionBalLotMovement movement, ProductionBalLotMovement reverseMovement,
        IReadOnlyList<StockReplayFifoConsumptionPosition> original, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (issue.Id <= 0 || reversal.Id <= 0 || issue.CompanyCode != company || issue.BranchCode != branch
            || reversal.CompanyCode != company || reversal.BranchCode != branch
            || issue.EntryRole != "ORIGINAL" || reversal.EntryRole != "REVERSAL"
            || reversal.ReversesHistoryId != issue.Id || issue.TrxType != IvTrxTypes.IssueToProduction
            || reversal.TrxType != issue.TrxType || issue.StockPostingId is not > 0
            || reversal.StockPostingId is not > 0 || issue.LedgerEpochId is not > 0
            || reversal.LedgerEpochId != issue.LedgerEpochId || issue.ICode != reversal.ICode
            || issue.FrStdQty is not > 0m || reversal.ToStdQty != issue.FrStdQty
            || movement.Uid <= 0 || reverseMovement.Uid <= 0
            || movement.CompanyCode != company || movement.BranchCode != branch
            || reverseMovement.CompanyCode != company || reverseMovement.BranchCode != branch
            || movement.InventoryHistoryId != issue.Id
            || reverseMovement.InventoryHistoryId is int reverseHistoryId && reverseHistoryId != reversal.Id
            || movement.StockPostingId != issue.StockPostingId || reverseMovement.StockPostingId != reversal.StockPostingId
            || movement.MovementType != ProductionBalLotMovementTypes.Issue
            || reverseMovement.MovementType != ProductionBalLotMovementTypes.IssueReversal
            || reverseMovement.OriginalMovementId != movement.Uid || movement.BaseQty != issue.FrStdQty
            || reverseMovement.BaseQty != movement.BaseQty || reverseMovement.TotalCost != movement.TotalCost
            || original.Count == 0 || original.Sum(x => x.Quantity) != movement.BaseQty
            || original.Sum(x => x.Value) != movement.TotalCost)
            throw Invalid("The FIFO issue and reversal do not have exact original ownership.");

        var generations = await db.StockFifoReversedIssueGenerations.AsNoTracking()
            .Where(g => g.IssueHistoryId == issue.Id || g.ReversalHistoryId == reversal.Id).ToArrayAsync(ct);
        if (generations.Length == 0) return null;
        if (generations.Any(g => g.CompanyCode != company || g.BranchCode != branch
                || g.IssueHistoryId != issue.Id || g.ReversalHistoryId != reversal.Id
                || g.IssueMovementId != movement.Uid || g.ReversalMovementId != reverseMovement.Uid
                || g.BaseQty != movement.BaseQty || g.TotalCost < 0m
                || StockLedgerPrecision.Money(g.TotalCost) != g.TotalCost)
            || generations.Count(g => g.PreviousGenerationId is null) != 1
            || generations.Where(g => g.PreviousGenerationId.HasValue)
                .GroupBy(g => g.PreviousGenerationId).Any(g => g.Count() != 1))
            throw Invalid("The FIFO issue has ambiguous correction generations.");
        var first = generations.Single(g => g.PreviousGenerationId is null);
        var ordered = new List<StockFifoReversedIssueGeneration> { first };
        while (ordered.Count < generations.Length)
        {
            var next = generations.SingleOrDefault(g => g.PreviousGenerationId == ordered[^1].Id);
            if (next is null || ordered.Any(g => g.Id == next.Id))
                throw Invalid("The FIFO issue correction chain is incomplete or cyclic.");
            ordered.Add(next);
        }
        if (generations.Any(g => g.PreviousGenerationId == ordered[^1].Id))
            throw Invalid("The FIFO issue correction chain is cyclic.");

        var ids = ordered.Select(g => g.Id).ToArray();
        var rootIds = ordered.Select(g => g.ReplayStockPostingId).ToArray();
        if (rootIds.Distinct().Count() != rootIds.Length)
            throw Invalid("The FIFO issue has duplicate correction roots.");
        var splits = await db.StockFifoReversedIssueSplits.AsNoTracking()
            .Where(s => ids.Contains(s.GenerationId)).ToArrayAsync(ct);
        var layerIds = splits.Select(s => s.FifoLayerId).Distinct().ToArray();
        var layers = await db.StockFifoLayers.AsNoTracking().Where(l => layerIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);
        var originFactIds = layers.Values.Select(l => l.OriginValuationFactId).Distinct().ToArray();
        var origins = await db.StockValuationFacts.AsNoTracking().Where(f => originFactIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);
        var layerOwnerIds = layers.Values.Select(l => l.OriginStockPostingId).Distinct().ToArray();
        var roots = await db.StockPostings.AsNoTracking().Where(p => rootIds.Contains(p.Id)
                || p.Id == issue.StockPostingId || p.Id == reversal.StockPostingId
                || layerOwnerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        if (!roots.TryGetValue(issue.StockPostingId!.Value, out var issueOwner)
            || !roots.TryGetValue(reversal.StockPostingId!.Value, out var reverseOwner)
            || issueOwner.SealedAtUtc is null || reverseOwner.SealedAtUtc is null
            || issueOwner.CompanyCode != company || issueOwner.BranchCode != branch
            || reverseOwner.CompanyCode != company || reverseOwner.BranchCode != branch
            || issueOwner.LedgerEpochId != issue.LedgerEpochId || reverseOwner.LedgerEpochId != issue.LedgerEpochId
            || issueOwner.EffectiveAt != issue.TrxDtTime || reverseOwner.EffectiveAt != reversal.TrxDtTime
            || reverseOwner.ReversesPostingId != issueOwner.Id || reverseOwner.PostingSequence <= issueOwner.PostingSequence)
            throw Invalid("The FIFO issue correction has unsealed original owners.");
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r => rootIds.Contains(r.RootStockPostingId))
            .ToDictionaryAsync(r => r.RootStockPostingId, ct);
        var values = await db.StockValuationFacts.AsNoTracking().Where(f => rootIds.Contains(f.StockPostingId)
                && f.MovementCode == ValueMovement).ToArrayAsync(ct);
        var movementIds = new[] { movement.Uid, reverseMovement.Uid };
        var costs = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => movementIds.Contains(f.ProductionBalLotMovementId)).ToArrayAsync(ct);
        if (costs.Any(f => !rootIds.Contains(f.ReplayStockPostingId)))
            throw Invalid("The FIFO issue has a Production correction outside its verified receipt history.");
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e => rootIds.Contains(e.RootStockPostingId)
                && (e.AffectedEntityType == GenerationEntity || e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                    && e.EffectKind == ValueEffect || e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT"))
            .ToArrayAsync(ct);
        var originalFactIds = original.Select(c => c.IssueFactId).ToArray();
        var anchor = await db.StockValuationFacts.AsNoTracking().Where(f => originalFactIds.Contains(f.Id))
            .OrderBy(f => f.SplitOrdinal).ThenBy(f => f.Id).FirstOrDefaultAsync(ct);
        var reverseAnchor = anchor is null ? null : await db.StockValuationFacts.AsNoTracking()
            .SingleOrDefaultAsync(f => f.ReversesValuationFactId == anchor.Id, ct);
        if (anchor is null || reverseAnchor is null || anchor.InventoryHistoryId != issue.Id
            || reverseAnchor.InventoryHistoryId != reversal.Id || anchor.CostMethod != StockCostMethods.Fifo
            || reverseAnchor.CostMethod != StockCostMethods.Fifo)
            throw Invalid("The FIFO issue correction has no original fact anchors.");

        decimal previousCost = movement.TotalCost;
        StockPosting? previousRoot = null;
        long? issueActiveCost = null, reverseActiveCost = null;
        var valueIds = new List<long>();
        foreach (var generation in ordered)
        {
            if (!roots.TryGetValue(generation.ReplayStockPostingId, out var root)
                || !runs.TryGetValue(root.Id, out var run) || root.SealedAtUtc is null
                || root.CompanyCode != company || root.BranchCode != branch
                || root.LedgerEpochId != issue.LedgerEpochId || root.SourceModule != "INVENTORY"
                || root.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
                || root.PostingRole is not ("PRIMARY" or "REVERSAL")
                || root.PostingSequence <= reverseOwner.PostingSequence || root.EffectiveAt > issue.TrxDtTime
                || run.Id != generation.ReplayRunId || run.CompanyCode != company || run.BranchCode != branch
                || run.RootRequestId != root.RequestId || run.RootDocumentType != root.SourceDocumentType
                || run.RootDocumentId != root.SourceDocumentId || run.EarliestEffectiveAt > issue.TrxDtTime
                || run.LatestEffectiveAt < reversal.TrxDtTime || string.IsNullOrWhiteSpace(run.PlanHash)
                || previousRoot is not null && (root.PostingSequence <= previousRoot.PostingSequence
                    || root.SourceDocumentType != previousRoot.SourceDocumentType
                    || root.SourceDocumentId != previousRoot.SourceDocumentId
                    || root.SourceDocumentNo != previousRoot.SourceDocumentNo
                    || root.PostingRole == previousRoot.PostingRole
                    || root.PostingRole == "REVERSAL" && root.ReversesPostingId != previousRoot.Id)
                || !HasEffect(generation.Id, GenerationEntity, GenerationEffect, issueOwner.Id, issue.Id, run))
                throw Invalid("A FIFO issue correction has invalid replay ownership.");
            var allocation = splits.Where(s => s.GenerationId == generation.Id).OrderBy(s => s.SplitOrdinal).ToArray();
            if (allocation.Length == 0 || allocation.Select((s, n) => s.SplitOrdinal == n).Any(ok => !ok)
                || allocation.Select(s => s.FifoLayerId).Distinct().Count() != allocation.Length
                || allocation.Any(s => s.CompanyCode != company || s.BranchCode != branch
                    || s.ConsumedQty <= 0m || s.ConsumedValue < 0m
                    || StockLedgerPrecision.Quantity(s.ConsumedQty) != s.ConsumedQty
                    || StockLedgerPrecision.Money(s.ConsumedValue) != s.ConsumedValue
                    || !layers.TryGetValue(s.FifoLayerId, out var layer)
                    || layer.CompanyCode != company || layer.BranchCode != branch || layer.ItemCode != issue.ICode
                    || layer.BaseUom != issue.FrStdUom || s.ConsumedQty > layer.OriginalQty
                    || s.ConsumedValue > layer.OriginalValue
                    || !origins.TryGetValue(layer.OriginValuationFactId, out var origin)
                    || origin.CompanyCode != company || origin.BranchCode != branch || origin.ItemCode != issue.ICode
                    || origin.StockPostingId != layer.OriginStockPostingId || origin.CostMethod != StockCostMethods.Fifo
                    || origin.BaseQty != layer.OriginalQty || origin.CostAmount != layer.OriginalValue
                    || origin.EffectiveAt != layer.ReceiptEffectiveAt || origin.ValuationStatus != StockValuationStatuses.Valued
                    || origin.Direction != 1 || origin.LedgerEpochId != issue.LedgerEpochId
                    || !roots.TryGetValue(layer.OriginStockPostingId, out var layerOwner)
                    || layerOwner.SealedAtUtc is null || layerOwner.CompanyCode != company || layerOwner.BranchCode != branch
                    || layerOwner.PostingSequence <= 0 || layerOwner.LedgerEpochId != issue.LedgerEpochId
                    || layer.ReceiptEffectiveAt > issue.TrxDtTime
                    || layer.ReceiptEffectiveAt == issue.TrxDtTime
                        && (layerOwner.SourceDocumentType == "IV_STOCK_COUNT"
                            || layerOwner.PostingSequence >= issueOwner.PostingSequence))
                || allocation.Sum(s => s.ConsumedQty) != generation.BaseQty
                || allocation.Sum(s => s.ConsumedValue) != generation.TotalCost
                || generation.SplitHash != SplitHash(generation, allocation))
                throw Invalid("A FIFO reversed issue has an incomplete or damaged allocation.");
            var delta = StockLedgerPrecision.Money(generation.TotalCost - previousCost);
            var rootValues = values.Where(f => f.StockPostingId == root.Id).ToArray();
            if (rootValues.Length != 2 || !MatchesValue(anchor, -delta) || !MatchesValue(reverseAnchor, delta))
                throw Invalid("A FIFO issue correction has incomplete Inventory value legs.");
            valueIds.AddRange(rootValues.Select(f => f.Id));
            issueActiveCost = CheckCost(movement, issueActiveCost);
            reverseActiveCost = CheckCost(reverseMovement, reverseActiveCost);
            previousCost = generation.TotalCost;
            previousRoot = root;
            continue;

            bool MatchesValue(StockValuationFact baseline, decimal signed)
            {
                var rows = rootValues.Where(f => f.OriginalValuationFactId == baseline.Id).ToArray();
                if (rows.Length != 1) return false;
                var f = rows[0];
                return f.CompanyCode == company && f.BranchCode == branch && f.LedgerEpochId == issue.LedgerEpochId
                    && f.InventoryHistoryId is null && f.ItemCode == issue.ICode && f.CostMethod == StockCostMethods.Fifo
                    && f.BaseQty == 0m && f.BaseUom == baseline.BaseUom && f.ValuationStatus == StockValuationStatuses.Valued
                    && f.ValuationSource == StockValuationSources.Fifo && f.ReversesValuationFactId is null
                    && f.EffectiveAt == baseline.EffectiveAt && f.BusinessDate == baseline.BusinessDate
                    && f.PeriodKey == baseline.PeriodKey && f.SignedValue == signed && f.CostAmount == f.BaseCostAmount
                    && HasEffect(f.Id, StockReplayEffectTypes.StockValuationFact, ValueEffect, baseline.StockPostingId, baseline.Id, run);
            }

            long? CheckCost(ProductionBalLotMovement owner, long? previousActive)
            {
                var rows = costs.Where(f => f.ReplayStockPostingId == root.Id
                    && f.ProductionBalLotMovementId == owner.Uid).ToArray();
                var expected = (previousActive.HasValue ? 1 : 0) + (generation.TotalCost != owner.TotalCost ? 1 : 0);
                if (rows.Length != expected || rows.Any(f => f.CompanyCode != company || f.BranchCode != branch
                        || f.ReplayRequestId != root.RequestId || f.EffectiveAt != owner.MovementDate
                        || f.DeltaCost != f.CorrectedEffectiveTotalCost - f.PriorEffectiveTotalCost
                        || f.CorrectedUnitCost != StockLedgerPrecision.Money(f.CorrectedEffectiveTotalCost / owner.BaseQty)
                        || !HasEffect(f.Id, "PRODUCTION_COST_RESTATEMENT_FACT", "PRODUCTION_COST_RESTATEMENT",
                            owner.StockPostingId!.Value, owner.Uid, run)))
                    throw Invalid("A FIFO issue correction has incomplete Production costs.");
                if (previousActive is long prior)
                {
                    var undo = rows.SingleOrDefault(f => f.ReversesRestatementFactId == prior);
                    if (undo is null || undo.PriorEffectiveTotalCost != previousCost
                        || undo.CorrectedEffectiveTotalCost != owner.TotalCost)
                        throw Invalid("A FIFO issue correction has an invalid Production cost reversal.");
                }
                var active = rows.SingleOrDefault(f => f.ReversesRestatementFactId is null);
                if (generation.TotalCost == owner.TotalCost)
                {
                    if (active is not null) throw Invalid("A FIFO issue correction retained an unexpected cost.");
                    return null;
                }
                if (active is null || active.PriorEffectiveTotalCost != owner.TotalCost
                    || active.CorrectedEffectiveTotalCost != generation.TotalCost)
                    throw Invalid("A FIFO issue correction has no replacement Production cost.");
                return active.Id;
            }
        }
        var effective = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, movementIds, ct);
        if (effective[movement.Uid].TotalCost != previousCost || effective[reverseMovement.Uid].TotalCost != previousCost
            || effective[movement.Uid].RestatementFactId != issueActiveCost
            || effective[reverseMovement.Uid].RestatementFactId != reverseActiveCost)
            throw Invalid("The FIFO issue and reversal effective Production costs do not match.");
        var last = ordered[^1];
        return new(last, splits.Where(s => s.GenerationId == last.Id).OrderBy(s => s.SplitOrdinal).ToArray(),
            issueActiveCost, reverseActiveCost, valueIds, rootIds);

        bool HasEffect(long affected, string type, string kind, long owner, long originalId, StockReplayRun run) =>
            effects.Count(e => e.CompanyCode == company && e.BranchCode == branch && e.ReplayRunId == run.Id
                && e.RootStockPostingId == run.RootStockPostingId && e.BusinessOwnerPostingId == owner
                && e.AffectedEntityType == type && e.AffectedEntityId == affected && e.EffectKind == kind
                && e.OriginalEntityId == originalId) == 1;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
