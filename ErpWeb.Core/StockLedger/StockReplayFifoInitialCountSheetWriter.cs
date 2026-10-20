using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoInitialCountSheetResult(
    StockCountInitialHistoryResult History,
    IReadOnlyList<StockValuationFact> Facts,
    IReadOnlyList<int> RequiredActiveHistoryIds);

/// <summary>
/// Initial FIFO count adjustments against the verified current layer pool
/// with later Inventory issues. A changed issue receives an append-only
/// correction, including one that consumes a count-created FIFO layer.
/// Fixed-value later MR receipts retain their original layers. The caller owns the transaction
/// and must run replay completion before committing.
/// </summary>
public static class StockReplayFifoInitialCountSheetWriter
{
    public static async Task<StockReplayFifoInitialCountSheetResult> AppendWithinRootAsync(
        StockPostingContext context,
        StockReplayFifoPreparedCountSheet prepared,
        IRunningNumberService numbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        StockReplayFifoCountSheetPostingPlanner.ValidatePreparedPlan(prepared.Plan);
        var sheet = prepared.Plan.PhysicalSheet;
        var physicalPermit = prepared.PhysicalPermit;
        if (context.Posting.PostingRole != "PRIMARY"
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId != sheet.CountHeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || physicalPermit.RootPostingId != context.Posting.Id
            || physicalPermit.LedgerEpochId != context.Epoch.Id
            || physicalPermit.PostingSequence != context.Posting.PostingSequence
            || physicalPermit.PlanHash != sheet.PlanHash
            || physicalPermit.Lines.Length != sheet.Lines.Count
            || prepared.ItemPermits.Count != prepared.Plan.Items.Count
            || prepared.Plan.Items.Any(item =>
                !prepared.ItemPermits.TryGetValue(item.ItemCode,
                    out var permit)
                || permit.RootPostingId != context.Posting.Id
                || permit.PlanHash != item.PlanHash
                || permit.SnapshotHash != item.Snapshot.SnapshotHash
                || permit.FinalQuantity != item.Projection.FinalQuantity
                || permit.FinalValue != item.Projection.FinalValue
                || item.Projection.LaterIssues.Count
                    != item.Projection.BeforeFirstCount.LaterConsumptions
                        .Select(c => c.IssueKey).Distinct().Count()
                || item.Projection.LaterIssues.Any(issue =>
                    !issue.DepletesPool || issue.IsCandidate
                    || issue.PreviousConsumptions.Count == 0
                    || issue.PreviousConsumptions.Sum(c => c.Quantity)
                        != issue.Quantity
                    || issue.Allocations.Sum(a => a.Quantity)
                        != issue.Quantity
                    || issue.Allocations.Sum(a => a.Value)
                        != issue.Value)
                || item.Projection.Counts.Any(c =>
                    c.SignedAdjustmentQuantity > 0m && c.Receipt is null
                    || c.SignedAdjustmentQuantity < 0m && c.Issue is null
                    || c.SignedAdjustmentQuantity == 0m
                        && (c.Receipt is not null || c.Issue is not null))))
            throw Invalid("FIFO initial count needs a complete supported issue tail.");
        if (!StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, prepared.Plan.FrozenBases))
            throw Invalid("FIFO count root has different approved cost bases.");

        var db = context.Db;
        var countLineIds = physicalPermit.Lines.Select(l => l.LineId).ToArray();
        var pileIds = physicalPermit.Lines.Select(l => l.BalLocId).ToArray();
        var piles = await db.IvBalLocs
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && pileIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        var states = await db.StockCostStates
            .Where(s => s.CompanyCode == context.CompanyCode
                && s.BranchCode == context.BranchCode
                && prepared.Plan.Items.Select(i => i.ItemCode)
                    .Contains(s.ItemCode))
            .ToDictionaryAsync(s => s.ItemCode, cancellationToken);
        if (piles.Count != pileIds.Length
            || sheet.Lines.Any(p => !piles.TryGetValue(p.BalLocId,
                    out var pile) || pile.StdQty != p.CurrentQuantity)
            || states.Count != prepared.Plan.Items.Count
            || states.Values.Any(s => s.CostMethod != StockCostMethods.Fifo)
            || db.ChangeTracker.Entries<StockFifoLayer>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified
                    or EntityState.Deleted)
            || db.ChangeTracker.Entries<StockFifoLayerConsumption>().Any(e =>
                e.State is EntityState.Added or EntityState.Modified
                    or EntityState.Deleted)
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f =>
                f.StockPostingId == context.Posting.Id, cancellationToken))
            throw Invalid("FIFO count physical or cost state changed before writing.");
        foreach (var item in prepared.Plan.Items)
        {
            var fresh = await StockReplayFifoActiveLayerReader.ReadAsync(db,
                context.CompanyCode, context.BranchCode, item.ItemCode,
                cancellationToken);
            if (!StockReplayFifoActiveLayerReader.SameSnapshot(
                    item.Snapshot, fresh)
                || states[item.ItemCode].OnHandBaseQty
                    != item.Snapshot.CurrentQuantity
                || states[item.ItemCode].InventoryValue
                    != item.Snapshot.CurrentValue)
                throw Invalid("FIFO count layer evidence changed before writing.");
            var countedIssues = await ValidateLaterIssuesAsync(
                context, item, cancellationToken);
            await ValidateLaterReceiptsAsync(context, item,
                countedIssues, cancellationToken);
        }

        // All checks above precede the first mutation. Subsequent writes remain
        // in the caller's transaction and are discarded if any proof fails.
        context.CostMethod = StockCostMethods.Fifo;
        await StockCountRepostBasisWriter.StageOrVerifyAsync(db,
            physicalPermit, prepared.Plan.FrozenBases, cancellationToken);
        var physicalByPile = sheet.Lines.ToDictionary(p => p.BalLocId);
        foreach (var line in physicalPermit.Lines)
        {
            var pile = piles[line.BalLocId];
            pile.StdQty = line.FinalQuantity;
            var physical = physicalByPile[line.BalLocId];
            pile.TransDate = physical.LaterEvents.Count == 0
                ? physical.Candidate.Key.EffectiveAt
                : physical.LaterEvents.MaxBy(e => e.Key)!.Key.EffectiveAt;
        }
        await db.SaveChangesAsync(cancellationToken);
        var history = await StockCountInitialHistoryWriter.AppendAsync(
            context, physicalPermit, numbers, cancellationToken);

        var generations = history.Generations.ToDictionary(
            g => g.StockCountLineId);
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw Invalid("FIFO count company has no base currency.");
        var facts = new List<StockValuationFact>();
        var correctedHistoryIds = new List<int>();
        var nextCorrectionLine = checked(sheet.Lines.Count + 1);
        foreach (var item in prepared.Plan.Items)
        {
            var state = states[item.ItemCode];
            var ordered = item.CountedPiles.OrderBy(p => p.Candidate.Key).ToArray();
            var hasFact = false;
            var poolBefore = StockLedgerPrecision.Quantity(
                item.Projection.BeforeFirstCount.Layers.Sum(l =>
                    l.RemainingQuantity));
            var virtualLayers = new Dictionary<long, StockFifoLayer>();
            for (var index = 0; index < ordered.Length; index++)
            {
                var physical = ordered[index];
                var projected = item.Projection.Counts[index];
                var lineId = physical.CountLineId!.Value;
                var generation = generations[lineId];
                if (projected.Key != physical.Candidate.Key
                    || generation.AnchorPostingId != context.Posting.Id
                    || generation.WritePostingId != context.Posting.Id
                    || generation.SignedAdjustmentQty
                        != projected.SignedAdjustmentQuantity)
                    throw Invalid("FIFO count generation differs from projection.");
                if (projected.SignedAdjustmentQuantity == 0m)
                {
                    if (generation.AdjustmentHistoryId is not null)
                        throw Invalid("Zero FIFO count has an ADJ history.");
                    continue;
                }
                if (projected.SignedAdjustmentQuantity < 0m)
                {
                    if (projected.Issue is not { } issue
                        || generation.AdjustmentHistoryId is not int issueHistoryId)
                        throw Invalid("Negative FIFO count has no projected issue history.");
                    var outbound = await db.IvTrxHistories.SingleOrDefaultAsync(h =>
                        h.Id == issueHistoryId, cancellationToken);
                    if (outbound is null
                        || outbound.StockPostingId != context.Posting.Id
                        || outbound.LedgerEpochId != context.Epoch.Id
                        || outbound.PostingLineNo is not int outboundLineNo
                        || outboundLineNo <= 0
                        || outbound.ICode != item.ItemCode
                        || outbound.TrxType != IvTrxTypes.StockAdjustment
                        || outbound.BatchStatus != IvBatchStatuses.Posted
                        || outbound.EntryRole != "ORIGINAL"
                        || outbound.ValuationStatus != StockValuationStatuses.Unvalued
                        || outbound.FromBalLocId != physical.BalLocId
                        || outbound.FrStdQty != issue.Quantity
                        || outbound.FrStdUom != item.Candidates[index].BaseUom
                        || outbound.StockCountCostBasisId is not null
                        || issue.Quantity != -projected.SignedAdjustmentQuantity
                        || issue.Allocations.Count == 0
                        || issue.Allocations.Sum(a => a.Quantity) != issue.Quantity
                        || issue.Allocations.Sum(a => a.Value) != issue.Value
                        || issue.Allocations.Any(a => a.LayerId <= 0
                            || a.Quantity <= 0m || a.Value < 0m
                            || !item.Snapshot.Layers.Any(l =>
                                l.LayerId == a.LayerId)
                                && !virtualLayers.ContainsKey(a.LayerId)))
                        throw Invalid("Negative FIFO count differs from its ADJ allocation.");
                    for (var ordinal = 0; ordinal < issue.Allocations.Count; ordinal++)
                    {
                        var allocation = issue.Allocations[ordinal];
                        var issueFact = BuildFact(context, physicalPermit.CountHeaderId,
                            outbound, outboundLineNo, ordinal,
                            allocation.Quantity, allocation.Value,
                            allocation.UnitCost, -1,
                            StockValuationSources.Fifo, baseCurrency);
                        db.StockValuationFacts.Add(issueFact);
                        db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
                        {
                            CompanyCode = context.CompanyCode,
                            BranchCode = context.BranchCode,
                            IssueValuationFact = issueFact,
                            FifoLayerId = virtualLayers.TryGetValue(
                                allocation.LayerId, out var newLayer)
                                ? newLayer.Id : allocation.LayerId,
                            ConsumedQty = allocation.Quantity,
                            ConsumedValue = allocation.Value,
                            SplitOrdinal = ordinal,
                            CreatedAtUtc = DateTime.UtcNow,
                            CreatedBy = context.UserId
                        });
                        facts.Add(issueFact);
                    }
                    outbound.ValuationStatus = StockValuationStatuses.Valued;
                    hasFact = true;
                    poolBefore -= issue.Quantity;
                    continue;
                }
                if (projected.Receipt is not { } receipt
                    || generation.AdjustmentHistoryId is not int historyId)
                    throw Invalid("Positive FIFO count has no receipt history.");
                var adjustment = await db.IvTrxHistories.SingleOrDefaultAsync(h =>
                    h.Id == historyId, cancellationToken);
                if (adjustment is null
                    || adjustment.StockPostingId != context.Posting.Id
                    || adjustment.LedgerEpochId != context.Epoch.Id
                    || adjustment.PostingLineNo is not int postingLineNo
                    || postingLineNo <= 0
                    || adjustment.ICode != item.ItemCode
                    || adjustment.TrxType != IvTrxTypes.StockAdjustment
                    || adjustment.BatchStatus != IvBatchStatuses.Posted
                    || adjustment.EntryRole != "ORIGINAL"
                    || adjustment.ValuationStatus != StockValuationStatuses.Unvalued
                    || adjustment.ToBalLocId != physical.BalLocId
                    || adjustment.ToStdQty != receipt.OriginalQuantity
                    || adjustment.ToStdUom != receipt.BaseUom
                    || adjustment.StockCountCostBasisId is not > 0)
                    throw Invalid("FIFO count has no exact unvalued ADJ history.");
                var basis = prepared.Plan.FrozenBases.Single(b =>
                    b.StockCountLineId == lineId);
                if (adjustment.StockCountCostBasisId != basis.Id)
                    throw Invalid("FIFO count ADJ lost its approved basis.");
                var sourceLine = adjustment.TrxLineNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                var now = DateTime.UtcNow;
                var fact = new StockValuationFact
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    LedgerEpochId = context.Epoch.Id,
                    StockPostingId = context.Posting.Id,
                    PostingLineNo = postingLineNo,
                    SplitOrdinal = 0,
                    SourceLineId = $"IV_STOCK_COUNT:{physicalPermit.CountHeaderId}:{sourceLine}:{postingLineNo}:0",
                    SourceDocumentType = context.Posting.SourceDocumentType,
                    SourceDocumentId = context.Posting.SourceDocumentId,
                    SourceDocumentNo = context.Posting.SourceDocumentNo,
                    SourceDocumentLine = sourceLine,
                    EffectiveAt = context.Posting.EffectiveAt,
                    BusinessDate = context.Posting.BusinessDate,
                    PeriodKey = context.Posting.PeriodKey,
                    ItemCode = item.ItemCode,
                    WarehouseCode = adjustment.ToWarehouse,
                    LocationCode = adjustment.ToLocation,
                    LotId = adjustment.ToLotId,
                    LotNo = adjustment.ToLotNo,
                    ItemStatus = adjustment.IStatus,
                    BaseUom = receipt.BaseUom,
                    MovementCode = "ADJUST_IN",
                    Direction = 1,
                    BaseQty = receipt.OriginalQuantity,
                    CostMethod = StockCostMethods.Fifo,
                    UnitCost = receipt.CurrentUnitCost,
                    CostAmount = receipt.OriginalValue,
                    BaseCurrency = baseCurrency,
                    BaseCostAmount = receipt.OriginalValue,
                    ValuationSource = poolBefore > 0m
                        ? StockValuationSources.Fifo
                        : StockValuationSources.ManualApproved,
                    ValuationStatus = StockValuationStatuses.Valued,
                    ValuationVersion = 1,
                    InventoryHistoryId = adjustment.Id,
                    ToBalLocId = adjustment.ToBalLocId,
                    CreatedAtUtc = now,
                    CreatedBy = context.UserId
                };
                db.StockValuationFacts.Add(fact);
                var savedLayer = new StockFifoLayer
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    ItemCode = item.ItemCode,
                    BaseUom = receipt.BaseUom,
                    OriginValuationFact = fact,
                    OriginStockPosting = context.Posting,
                    ReceiptEffectiveAt = context.Posting.EffectiveAt,
                    OriginalQty = receipt.OriginalQuantity,
                    RemainingQty = receipt.RemainingQuantity,
                    OriginalValue = receipt.OriginalValue,
                    AccumulatedAdjustment = 0m,
                    RemainingValue = receipt.RemainingValue,
                    CurrentUnitCost = receipt.CurrentUnitCost,
                    SourceDocumentType = fact.SourceDocumentType,
                    SourceDocumentNo = fact.SourceDocumentNo,
                    SourceDocumentLine = fact.SourceDocumentLine,
                    WarehouseCode = fact.WarehouseCode,
                    LotId = fact.LotId,
                    LotNo = fact.LotNo,
                    Status = StockFifoLayerStatuses.Open
                };
                db.StockFifoLayers.Add(savedLayer);
                virtualLayers.Add(receipt.LayerId, savedLayer);
                adjustment.ValuationStatus = StockValuationStatuses.Valued;
                facts.Add(fact);
                hasFact = true;
                poolBefore += receipt.OriginalQuantity;
                await db.SaveChangesAsync(cancellationToken);
            }
            foreach (var laterIssue in item.Projection.LaterIssues
                .Where(NeedsCorrection).OrderBy(i => i.Key))
            {
                var reversalLine = nextCorrectionLine;
                var replacementLine = checked(reversalLine + 1);
                var replacements = await StockReplayFifoIssueRepricingWriter
                    .AppendCountAsync(context, item,
                        prepared.ItemPermits[item.ItemCode], laterIssue.Key,
                        reversalLine, replacementLine, virtualLayers,
                        cancellationToken);
                if (replacements.Count == 0
                    || laterIssue.Key.LineIdentity.Length != 11
                    || !int.TryParse(
                        laterIssue.Key.LineIdentity.AsSpan(1),
                        out var correctedHistoryId))
                    throw Invalid("FIFO count lost its later issue correction.");
                var reversals = db.ChangeTracker.Entries<StockValuationFact>()
                    .Where(e => e.State == EntityState.Added
                        && e.Entity.StockPostingId == context.Posting.Id
                        && e.Entity.PostingLineNo == reversalLine
                        && e.Entity.ReversesValuationFactId.HasValue)
                    .Select(e => e.Entity).ToArray();
                if (reversals.Length != laterIssue.PreviousConsumptions.Count)
                    throw Invalid("FIFO count issue correction has incomplete reversals.");
                facts.AddRange(reversals);
                facts.AddRange(replacements);
                correctedHistoryIds.Add(correctedHistoryId);
                nextCorrectionLine = checked(replacementLine + 1);
                hasFact = true;
            }
            if (item.Projection.Counts.Any(c =>
                    c.SignedAdjustmentQuantity < 0m)
                || item.Projection.LaterIssues.Any(NeedsCorrection))
                await ApplyLayersAsync(db, item, virtualLayers,
                    cancellationToken);
            state.OnHandBaseQty = item.Projection.FinalQuantity;
            state.InventoryValue = item.Projection.FinalValue;
            state.AverageUnitCost = state.OnHandBaseQty == 0m
                ? 0m : StockLedgerPrecision.Money(
                    state.InventoryValue / state.OnHandBaseQty);
            state.CurrentUnitCost = state.AverageUnitCost;
            if (hasFact)
                state.LastPostingSequence = context.Posting.PostingSequence;
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var item in prepared.Plan.Items)
        {
            var itemFacts = facts.Where(f => f.ItemCode == item.ItemCode)
                .OrderBy(f => f.PostingLineNo)
                .ThenBy(f => f.SplitOrdinal).ToArray();
            var state = states[item.ItemCode];
            if (itemFacts.Length > 0)
                state.LastValuationFactId = itemFacts[^1].Id;
            var layers = await db.StockFifoLayers.AsNoTracking()
                .Where(l => l.CompanyCode == context.CompanyCode
                    && l.BranchCode == context.BranchCode
                    && l.ItemCode == item.ItemCode)
                .ToArrayAsync(cancellationToken);
            if (StockLedgerPrecision.Quantity(layers.Sum(l => l.RemainingQty))
                    != item.Projection.FinalQuantity
                || StockLedgerPrecision.Money(layers.Sum(l => l.RemainingValue))
                    != item.Projection.FinalValue
                || StockLedgerPrecision.Quantity(itemFacts.Sum(f => f.SignedQty))
                    != StockLedgerPrecision.Quantity(
                        item.Projection.FinalQuantity
                        - item.Snapshot.CurrentQuantity)
                || StockLedgerPrecision.Money(itemFacts.Sum(f => f.SignedValue))
                    != StockLedgerPrecision.Money(
                        item.Projection.FinalValue
                        - item.Snapshot.CurrentValue))
                throw Invalid("FIFO count layers or facts do not reconcile.");
        }
        await db.SaveChangesAsync(cancellationToken);
        var required = history.Generations
            .Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value)
            .Concat(correctedHistoryIds).Distinct().Order().ToArray();
        return new(history, facts, required);
    }

    private static bool NeedsCorrection(
        StockReplayFifoProjectedIssue issue) =>
        !issue.PreviousConsumptions.Select(c =>
                (c.LayerId, c.Quantity, c.Value))
            .OrderBy(c => c.LayerId)
            .SequenceEqual(issue.Allocations.Select(a =>
                (a.LayerId, a.Quantity, a.Value))
                .OrderBy(a => a.LayerId));

    private static async Task ValidateLaterReceiptsAsync(
        StockPostingContext context,
        StockReplayFifoCountSheetInsertionPlan item,
        int countedIssueCount,
        CancellationToken cancellationToken)
    {
        var receipts = item.Projection.BeforeFirstCount.LaterReceipts;
        var laterPhysical = item.CountedPiles.SelectMany(p =>
            p.LaterEvents.Select(e => (p.BalLocId, Event: e))).ToArray();
        if (receipts.Count == 0)
        {
            if (laterPhysical.Length != countedIssueCount)
                throw Invalid("FIFO count has an unsupported later pile movement.");
            return;
        }
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, item.PhysicalSheet.EffectiveAt.Date,
            receipts.Max(r => r.ReceiptEffectiveAt.Date),
            cancellationToken);
        var factIds = receipts.Select(r => r.OriginFactId).ToArray();
        var facts = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(f => factIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        var postingIds = receipts.Select(r => r.OriginPostingId).ToArray();
        var postings = await context.Db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var historyIds = facts.Values.Where(f => f.InventoryHistoryId.HasValue)
            .Select(f => f.InventoryHistoryId!.Value).ToArray();
        var histories = await context.Db.IvTrxHistories.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        var destinationIds = histories.Values.Where(h => h.ToBalLocId.HasValue)
            .Select(h => h.ToBalLocId!.Value).ToArray();
        var destinations = await context.Db.IvBalLocs.AsNoTracking()
            .Where(p => destinationIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var countedIds = item.CountedPiles.Select(p => p.BalLocId)
            .ToHashSet();
        var countedReceiptCount = receipts.Count(receipt =>
            facts.TryGetValue(receipt.OriginFactId, out var fact)
            && fact.InventoryHistoryId is int historyId
            && histories.TryGetValue(historyId, out var history)
            && history.ToBalLocId is int destinationId
            && countedIds.Contains(destinationId));
        if (facts.Count != receipts.Count
            || postings.Count != postingIds.Distinct().Count()
            || histories.Count != receipts.Count
            || destinations.Count != destinationIds.Distinct().Count()
            || laterPhysical.Length != countedReceiptCount + countedIssueCount
            || receipts.Any(receipt =>
                !facts.TryGetValue(receipt.OriginFactId, out var fact)
                || !postings.TryGetValue(receipt.OriginPostingId,
                    out var posting)
                || fact.InventoryHistoryId is not int historyId
                || !histories.TryGetValue(historyId, out var history)
                || history.ToBalLocId is not int destinationId
                || !destinations.TryGetValue(destinationId, out var destination)
                || countedIds.Contains(destinationId)
                    && !laterPhysical.Any(e =>
                        e.BalLocId == destinationId
                        && e.Event.EventId == historyId
                        && e.Event.Key == receipt.ReceiptKey
                        && e.Event.SignedDelta == receipt.OriginalQuantity
                        && e.Event.AbsoluteCountQty is null)
                || posting.CompanyCode != context.CompanyCode
                || posting.BranchCode != context.BranchCode
                || posting.LedgerEpochId != context.Epoch.Id
                || posting.SourceModule != "INVENTORY"
                || posting.SourceDocumentType
                    != IvTrxTypes.MiscellaneousReceipt
                || posting.PostingRole != "PRIMARY"
                || posting.SealedAtUtc is null
                || posting.EffectiveAt != receipt.ReceiptEffectiveAt
                || destination.CompanyCode != context.CompanyCode
                || destination.BranchCode != context.BranchCode
                || destination.ICode != item.ItemCode
                || destination.StdUom != receipt.BaseUom
                || history.CompanyCode != context.CompanyCode
                || history.BranchCode != context.BranchCode
                || history.ICode != item.ItemCode
                || history.TrxType != IvTrxTypes.MiscellaneousReceipt
                || history.EntryRole != "ORIGINAL"
                || history.LedgerVersion != 2
                || history.LedgerEpochId != context.Epoch.Id
                || history.StockPostingId != receipt.OriginPostingId
                || history.PostingLineNo != fact.PostingLineNo
                || posting.SourceDocumentId != history.BatchNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                || fact.SourceDocumentId != posting.SourceDocumentId
                || fact.SourceDocumentLine != history.TrxLineNo.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                || history.DocumentRevision != posting.DocumentRevision
                || history.BatchStatus != IvBatchStatuses.Posted
                || history.ValuationStatus != StockValuationStatuses.Valued
                || history.ToStdQty != receipt.OriginalQuantity
                || history.ToStdUom != receipt.BaseUom
                || history.TrxDtTime != receipt.ReceiptEffectiveAt
                || fact.StockPostingId != receipt.OriginPostingId
                || fact.LedgerEpochId != context.Epoch.Id
                || fact.SourceDocumentType != posting.SourceDocumentType
                || fact.MovementCode != "RECEIPT_IN"
                || fact.Direction != 1
                || fact.BaseQty != receipt.OriginalQuantity
                || fact.CostAmount != receipt.OriginalValue
                || fact.ValuationStatus != StockValuationStatuses.Valued))
            throw Invalid("A later FIFO receipt lacks its exact MR pile history.");
    }

    private static async Task<int> ValidateLaterIssuesAsync(
        StockPostingContext context,
        StockReplayFifoCountSheetInsertionPlan item,
        CancellationToken cancellationToken)
    {
        var issues = item.Projection.LaterIssues;
        if (issues.Count == 0)
            return 0;
        await StockReplayFifoIssueRepricingWriter.ValidateFifoPolicyAsync(
            context, item.PhysicalSheet.EffectiveAt.Date,
            issues.Max(i => i.Key.EffectiveAt.Date), cancellationToken);
        var historyIds = issues.Select(issue =>
        {
            if (issue.Key.LineIdentity.Length != 11
                || issue.Key.LineIdentity[0] != 'H'
                || !int.TryParse(issue.Key.LineIdentity.AsSpan(1),
                    out var historyId) || historyId <= 0)
                throw Invalid("A later FIFO issue has no ordinary history key.");
            return historyId;
        }).ToArray();
        var histories = await context.Db.IvTrxHistories.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        var sourceIds = histories.Values.Where(h => h.FromBalLocId.HasValue)
            .Select(h => h.FromBalLocId!.Value).ToArray();
        var sources = await context.Db.IvBalLocs.AsNoTracking()
            .Where(p => sourceIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var factIds = issues.SelectMany(i => i.PreviousConsumptions)
            .Select(c => c.IssueFactId).Distinct().ToArray();
        var facts = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(f => factIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        var laterPhysical = item.CountedPiles.SelectMany(p =>
            p.LaterEvents.Select(e => (p.BalLocId, Event: e))).ToArray();
        var countedIssueCount = 0;
        if (histories.Count != issues.Count
            || sources.Count != sourceIds.Distinct().Count()
            || facts.Count != factIds.Length)
            throw Invalid("A later FIFO issue has incomplete posted evidence.");
        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            var historyId = historyIds[index];
            if (!histories.TryGetValue(historyId, out var history)
                || history.FromBalLocId is not int sourceId
                || !sources.TryGetValue(sourceId, out var source)
                || !StockReplayFifoInventoryIssueKinds.IsSupported(
                    history.TrxType)
                || history.CompanyCode != context.CompanyCode
                || history.BranchCode != context.BranchCode
                || history.ICode != item.ItemCode
                || history.LedgerVersion != 2
                || history.LedgerEpochId != context.Epoch.Id
                || history.EntryRole != "ORIGINAL"
                || history.BatchStatus != IvBatchStatuses.Posted
                || history.ValuationStatus != StockValuationStatuses.Valued
                || history.TrxDtTime != issue.Key.EffectiveAt
                || history.FrStdQty != issue.Quantity
                || history.ToBalLocId is > 0
                || source.CompanyCode != context.CompanyCode
                || source.BranchCode != context.BranchCode
                || source.ICode != item.ItemCode
                || source.StdUom != history.FrStdUom
                || issue.PreviousConsumptions.Any(c =>
                    !c.DepletesPool
                    || !facts.TryGetValue(c.IssueFactId, out var fact)
                    || fact.CompanyCode != context.CompanyCode
                    || fact.BranchCode != context.BranchCode
                    || fact.LedgerEpochId != context.Epoch.Id
                    || fact.InventoryHistoryId != historyId
                    || fact.ItemCode != item.ItemCode
                    || fact.BaseUom != history.FrStdUom
                    || fact.CostMethod != StockCostMethods.Fifo
                    || fact.ValuationSource != StockValuationSources.Fifo
                    || fact.ValuationStatus != StockValuationStatuses.Valued
                    || fact.EffectiveAt != issue.Key.EffectiveAt
                    || fact.SplitOrdinal != c.SplitOrdinal
                    || fact.MovementCode !=
                        StockReplayFifoInventoryIssueKinds.MovementCode(
                            history.TrxType)
                    || fact.Direction != -1
                    || fact.BaseQty != c.Quantity
                    || fact.CostAmount != c.Value))
                throw Invalid("A later FIFO issue lacks supported Inventory evidence.");
            if (item.CountedPiles.Any(p => p.BalLocId == sourceId))
            {
                if (!laterPhysical.Any(e => e.BalLocId == sourceId
                    && e.Event.EventId == historyId
                    && e.Event.Key == issue.Key
                    && e.Event.SignedDelta == -issue.Quantity
                    && e.Event.AbsoluteCountQty is null))
                    throw Invalid("A later FIFO issue lacks its counted-pile movement.");
                countedIssueCount++;
            }
        }
        return countedIssueCount;
    }

    private static async Task ApplyLayersAsync(AppDbContext db,
        StockReplayFifoCountSheetInsertionPlan item,
        IReadOnlyDictionary<long, StockFifoLayer> virtualLayers,
        CancellationToken cancellationToken)
    {
        var layerIds = item.Snapshot.Layers.Select(l => l.LayerId).ToArray();
        var persisted = await db.StockFifoLayers
            .Where(l => l.CompanyCode == item.PhysicalSheet.CompanyCode
                && l.BranchCode == item.PhysicalSheet.BranchCode
                && l.ItemCode == item.ItemCode && layerIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, cancellationToken);
        var final = item.Projection.FinalLayers.ToDictionary(l => l.LayerId);
        if (persisted.Count != layerIds.Length
            || final.Count != layerIds.Length + virtualLayers.Count)
            throw Invalid("FIFO count issue has incomplete layer coverage.");
        foreach (var before in item.Snapshot.Layers)
        {
            if (!persisted.TryGetValue(before.LayerId, out var layer)
                || !final.TryGetValue(before.LayerId, out var after)
                || layer.OriginValuationFactId != before.OriginFactId
                || layer.OriginStockPostingId != before.OriginPostingId
                || layer.OriginalQty != before.OriginalQuantity
                || layer.OriginalValue != before.OriginalValue
                || layer.AccumulatedAdjustment != before.AccumulatedAdjustment
                || layer.RemainingQty != before.RemainingQuantity
                || layer.RemainingValue != before.RemainingValue
                || layer.CurrentUnitCost != before.CurrentUnitCost
                || layer.Status != before.Status
                || Convert.ToHexString(layer.RowVersion) != before.RowVersionHex
                || after.OriginalQuantity != before.OriginalQuantity
                || after.OriginalValue != before.OriginalValue
                || after.AccumulatedAdjustment != before.AccumulatedAdjustment)
                throw Invalid("FIFO count issue layer changed after planning.");
            layer.RemainingQty = after.RemainingQuantity;
            layer.RemainingValue = after.RemainingValue;
            layer.CurrentUnitCost = after.CurrentUnitCost;
            layer.Status = after.Status;
        }
        foreach (var (virtualId, layer) in virtualLayers)
        {
            if (!final.TryGetValue(virtualId, out var after)
                || layer.Id <= 0
                || layer.OriginValuationFactId <= 0
                || layer.OriginalQty != after.OriginalQuantity
                || layer.OriginalValue != after.OriginalValue
                || layer.AccumulatedAdjustment != 0m
                || layer.RemainingQty != layer.OriginalQty
                || layer.RemainingValue != layer.OriginalValue
                || after.AccumulatedAdjustment != 0m)
                throw Invalid("FIFO count-created layer changed before allocation.");
            layer.RemainingQty = after.RemainingQuantity;
            layer.RemainingValue = after.RemainingValue;
            layer.CurrentUnitCost = after.CurrentUnitCost;
            layer.Status = after.Status;
        }
    }

    private static StockValuationFact BuildFact(StockPostingContext context,
        int countHeaderId, IvTrxHistory history, int postingLineNo,
        int splitOrdinal, decimal quantity, decimal value, decimal unitCost,
        int direction, string source, string baseCurrency)
    {
        var sourceLine = history.TrxLineNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return new StockValuationFact
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLineNo,
            SplitOrdinal = splitOrdinal,
            SourceLineId = $"IV_STOCK_COUNT:{countHeaderId}:{sourceLine}:{postingLineNo}:{splitOrdinal}",
            SourceDocumentType = context.Posting.SourceDocumentType,
            SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo,
            SourceDocumentLine = sourceLine,
            EffectiveAt = context.Posting.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = history.ICode,
            WarehouseCode = direction > 0
                ? history.ToWarehouse : history.FrWarehouse,
            LocationCode = direction > 0
                ? history.ToLocation : history.FrLocation,
            LotId = direction > 0 ? history.ToLotId : history.FromLotId,
            LotNo = direction > 0 ? history.ToLotNo : history.FrLotNo,
            ItemStatus = history.IStatus,
            BaseUom = direction > 0 ? history.ToStdUom! : history.FrStdUom!,
            MovementCode = direction > 0 ? "ADJUST_IN" : "ADJUST_OUT",
            Direction = direction,
            BaseQty = quantity,
            CostMethod = StockCostMethods.Fifo,
            UnitCost = unitCost,
            CostAmount = value,
            BaseCurrency = baseCurrency,
            BaseCostAmount = value,
            ValuationSource = source,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1,
            InventoryHistoryId = history.Id,
            FromBalLocId = history.FromBalLocId,
            ToBalLocId = history.ToBalLocId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = context.UserId
        };
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
