using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

internal sealed record StockReplayFifoSingleCountProof(
    int HeaderId, int LineId, long GenerationId, string Fingerprint);

internal sealed record StockReplayFifoSingleCountPlan(
    string CompanyCode,
    string BranchCode,
    string ItemCode,
    int BalLocId,
    bool IsReceipt,
    bool IsRemoval,
    StockReplayEventKey CandidateKey,
    decimal Quantity,
    decimal? ReceiptValue,
    string? ReceiptBaseUom,
    StockReplayPhysicalPlan? PhysicalInsert,
    StockReplayPhysicalRemovalPlan? PhysicalRemoval,
    long? RemovingOwnerId,
    StockReplayFifoActiveLayerSnapshot Snapshot,
    StockReplayFifoHistoricalPool BeforeCandidate,
    StockReplayFifoSingleCountProof Proof,
    StockReplayEventKey CountKey,
    decimal OldSignedAdjustmentQty,
    decimal NewSignedAdjustmentQty,
    decimal NewCountValue,
    bool NewCountUsedFallback,
    long? RemovedLayerId,
    IReadOnlyList<StockReplayFifoIssueAllocation> CandidateAllocations,
    IReadOnlyList<StockReplayFifoIssueAllocation> NewCountAllocations,
    IReadOnlyList<StockReplayFifoLayerPosition> FinalLayers,
    decimal FinalItemQuantity,
    decimal FinalItemValue,
    string Fingerprint);

internal static class StockReplayFifoSingleCountReplay
{
    internal const long CandidateLayerPlaceholderId = long.MaxValue;
    internal const long NewCountLayerPlaceholderId = long.MaxValue - 1;

    internal static async Task<StockReplayFifoSingleCountPlan?> PlanAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string itemCode,
        int balLocId,
        StockReplayEventKey candidateKey,
        decimal quantity,
        bool isReceipt,
        bool isRemoval,
        decimal? receiptValue,
        string? receiptBaseUom,
        StockReplayPhysicalPlan? physicalInsert,
        StockReplayPhysicalRemovalPlan? physicalRemoval,
        long? removingOwnerId,
        CancellationToken cancellationToken)
    {
        var laterEvents = isRemoval ? physicalRemoval!.LaterEvents : physicalInsert!.LaterEvents;
        var currentPileQty = isRemoval ? physicalRemoval!.CurrentQuantity : physicalInsert!.CurrentQuantity;
        var finalPileQty = isRemoval ? physicalRemoval!.Projection.FinalQuantity : physicalInsert!.Projection.FinalQuantity;
        if (quantity <= 0m || laterEvents.Count != 1 || currentPileQty != finalPileQty)
            return null;

        var movement = laterEvents[0];
        if (movement.EventId >= 0 || movement.EventId == long.MinValue
            || movement.AbsoluteCountQty is not >= 0m
            || movement.Key.ClassPriority != StockReplayEventKey.CountClass
            || movement.Key.EffectiveAt.Year != candidateKey.EffectiveAt.Year
            || movement.Key.EffectiveAt.Month != candidateKey.EffectiveAt.Month)
            return null;

        var generation = await db.StockCountReplayGenerations.AsNoTracking().SingleOrDefaultAsync(g =>
            g.Id == -movement.EventId && g.CompanyCode == companyCode && g.BranchCode == branchCode,
            cancellationToken);
        if (generation is null || generation.IsRemoval || generation.SignedAdjustmentQty != movement.SignedDelta)
            return null;

        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, companyCode, branchCode, [generation.StockCountLineId], cancellationToken);
        if (!active.TryGetValue(generation.StockCountLineId, out var leaf) || leaf.Id != generation.Id)
            return null;

        var line = await db.IvStockCountLines.AsNoTracking().Include(l => l.Header)
            .SingleOrDefaultAsync(l => l.Id == generation.StockCountLineId, cancellationToken);
        if (line is null || line.Header.CompanyCode != companyCode || line.Header.BranchCode != branchCode
            || line.Header.Status != IvStockCountStatuses.Posted || line.BalLocId != balLocId
            || line.ICode != itemCode || line.PhysicalQty != movement.AbsoluteCountQty
            || StockReplayEventKey.CountEndOfDay(line.Header.CountDate) != movement.Key.EffectiveAt)
            return null;

        var hasOtherCounts = await db.StockCountReplayGenerations.AsNoTracking().AnyAsync(g =>
            g.CompanyCode == companyCode && g.BranchCode == branchCode
            && g.EffectiveAt > candidateKey.EffectiveAt && !g.IsRemoval
            && g.StockCountLineId != line.Id
            && db.IvStockCountLines.Any(l => l.Id == g.StockCountLineId && l.ICode == itemCode
                && l.Header.CompanyCode == companyCode && l.Header.BranchCode == branchCode
                && l.Header.Status == IvStockCountStatuses.Posted),
            cancellationToken);
        if (hasOtherCounts)
            return null;

        var signedNetCandidateDelta = isReceipt
            ? (isRemoval ? -quantity : quantity)
            : (isRemoval ? quantity : -quantity);
        var newSignedAdj = IvQty.Round(movement.SignedDelta - signedNetCandidateDelta);
        if (newSignedAdj == movement.SignedDelta)
            return null;

        if (isRemoval)
        {
            if (removingOwnerId is null || generation.WritePostingId != removingOwnerId.Value)
                return null;
            var previous = generation.PreviousGenerationId is long previousId
                ? await db.StockCountReplayGenerations.AsNoTracking()
                    .SingleOrDefaultAsync(g => g.Id == previousId, cancellationToken)
                : null;
            if (previous is null || previous.IsRemoval
                || previous.StockCountLineId != line.Id
                || previous.AnchorPostingId != generation.AnchorPostingId
                || previous.SignedAdjustmentQty != newSignedAdj)
                return null;
        }

        // Verify no other unneutralized StockValuationFacts for this item exist after candidateKey.
        var laterFacts = await db.StockValuationFacts.AsNoTracking()
            .Include(f => f.StockPosting)
            .Where(f => f.CompanyCode == companyCode && f.BranchCode == branchCode
                && f.ItemCode == itemCode
                && (f.EffectiveAt > candidateKey.EffectiveAt
                    || f.EffectiveAt == candidateKey.EffectiveAt
                        && f.StockPosting!.PostingSequence > candidateKey.OriginalPostingSequence)
                && f.StockPosting!.SealedAtUtc != null)
            .ToArrayAsync(cancellationToken);
        var neutralized = await IvInventoryPostingService.ReadExactFifoReversalPairsAsync(
            db, companyCode, branchCode, itemCode, laterFacts, cancellationToken);
        if (neutralized is null)
            return null;
        if (laterFacts.Any(f => !neutralized.Contains(f.Id)
                && f.InventoryHistoryId != generation.AdjustmentHistoryId))
            return null;

        var snapshot = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, companyCode, branchCode, itemCode, cancellationToken);
        if (snapshot.Layers.Any(l => l.AccumulatedAdjustment != 0m
                || l.SalesReturn is not null || l.ProductionReturn is not null
                || l.ReceiptKey is { } rKey && rKey.CompareTo(movement.Key) > 0)
            || snapshot.ActiveConsumptions.Any(c => c.IssueKey.CompareTo(movement.Key) > 0))
            return null;

        var beforeCandidate = StockReplayFifoLayerRewinder.RewindBefore(snapshot, candidateKey);
        if (isRemoval)
        {
            if (beforeCandidate.LaterReceipts.Any(l =>
                    l.ReceiptKey != candidateKey && l.ReceiptKey != movement.Key)
                || beforeCandidate.LaterConsumptions.Any(c =>
                    c.IssueKey != candidateKey && c.IssueKey != movement.Key))
                return null;
        }
        else
        {
            if (beforeCandidate.LaterReceipts.Any(l => l.ReceiptKey != movement.Key)
                || beforeCandidate.LaterConsumptions.Any(c => c.IssueKey != movement.Key))
                return null;
        }

        var opening = beforeCandidate.Layers.ToList();
        long? removedLayerId = null;
        IReadOnlyList<StockReplayFifoIssueAllocation> candidateAllocations = [];
        var effectiveBaseUom = receiptBaseUom ?? snapshot.Layers.FirstOrDefault()?.BaseUom ?? line.StdUom ?? "EA";
        if (snapshot.Layers.Any(l => l.BaseUom != effectiveBaseUom))
            return null;

        if (!isRemoval && isReceipt)
        {
            if (receiptValue is not decimal val || val < 0m
                || StockLedgerPrecision.Money(val) != val
                || string.IsNullOrWhiteSpace(receiptBaseUom))
                return null;
            opening.Add(new StockReplayFifoLayerPosition(
                CandidateLayerPlaceholderId, long.MaxValue, long.MaxValue,
                candidateKey.OriginalPostingSequence, candidateKey.EffectiveAt,
                quantity, val, 0m, quantity, val,
                StockLedgerPrecision.Money(val / quantity), effectiveBaseUom,
                StockFifoLayerStatuses.Open, "PROJECTED", candidateKey));
        }
        else if (!isRemoval && !isReceipt)
        {
            var allocated = StockReplayFifoLayerAllocator.AllocateIssue(opening, candidateKey, quantity);
            candidateAllocations = allocated.Allocations;
            opening = allocated.Layers.ToList();
        }
        else if (isRemoval && isReceipt)
        {
            var removedLayers = beforeCandidate.LaterReceipts.Where(l => l.ReceiptKey == candidateKey).ToArray();
            if (removedLayers.Length != 1
                || removedLayers[0].OriginalQuantity != quantity
                || beforeCandidate.LaterConsumptions.Any(c => c.LayerId == removedLayers[0].LayerId
                    && c.IssueKey != movement.Key))
                return null;
            removedLayerId = removedLayers[0].LayerId;
        }
        else
        {
            var removedCons = beforeCandidate.LaterConsumptions.Where(c => c.IssueKey == candidateKey).ToArray();
            if (removedCons.Length == 0 || removedCons.Any(c => !c.DepletesPool)
                || StockLedgerPrecision.Quantity(removedCons.Sum(c => c.Quantity)) != quantity)
                return null;
        }

        StockCountCostBasis? basis = null;
        if (line.PhysicalQty > 0m)
        {
            basis = await db.StockCountCostBases.AsNoTracking()
                .SingleOrDefaultAsync(b => b.StockCountLineId == line.Id, cancellationToken);
            if (basis is null || !StockCountAnchorCommandFactory.ValidBasis(basis))
                return null;
        }

        var newCountValue = 0m;
        var newCountUsedFallback = false;
        IReadOnlyList<StockReplayFifoIssueAllocation> newCountAllocations = [];
        List<StockReplayFifoLayerPosition> finalLayers;

        if (newSignedAdj == 0m)
        {
            finalLayers = opening;
        }
        else if (newSignedAdj > 0m)
        {
            if (basis is null)
                throw Invalid("Positive FIFO count correction has no frozen cost basis.");
            var fallbackCost = basis.FrozenFallbackUnitCost;
            var poolBeforeQty = StockLedgerPrecision.Quantity(opening.Sum(l => l.RemainingQuantity));
            var poolBeforeVal = StockLedgerPrecision.Money(opening.Sum(l => l.RemainingValue));
            decimal unitCost;
            if (poolBeforeQty > 0m)
                unitCost = StockLedgerPrecision.Money(poolBeforeVal / poolBeforeQty);
            else
            {
                unitCost = fallbackCost;
                newCountUsedFallback = true;
            }
            newCountValue = StockLedgerPrecision.Money(newSignedAdj * unitCost);
            opening.Add(new StockReplayFifoLayerPosition(
                NewCountLayerPlaceholderId, NewCountLayerPlaceholderId, long.MaxValue,
                generation.AnchorPostingSequence, movement.Key.EffectiveAt,
                newSignedAdj, newCountValue, 0m, newSignedAdj, newCountValue,
                StockLedgerPrecision.Money(newCountValue / newSignedAdj),
                effectiveBaseUom, StockFifoLayerStatuses.Open, "PROJECTED", movement.Key));
            finalLayers = opening;
        }
        else
        {
            var allocated = StockReplayFifoLayerAllocator.AllocateIssue(
                opening, movement.Key, -newSignedAdj);
            newCountAllocations = allocated.Allocations;
            newCountValue = allocated.TotalValue;
            finalLayers = allocated.Layers.ToList();
        }

        var finalItemQty = StockLedgerPrecision.Quantity(finalLayers.Sum(l => l.RemainingQuantity));
        var finalItemVal = StockLedgerPrecision.Money(finalLayers.Sum(l => l.RemainingValue));
        if (finalItemQty != snapshot.CurrentQuantity)
            throw Invalid("FIFO single-pile count replay does not conserve item quantity.");

        var proofHash = StockPostingFingerprint.Create(new
        {
            Kind = (isReceipt ? "FIFO_MR_" : "FIFO_MI_") + (isRemoval ? "REMOVE_CNT" : "INSERT_CNT")
        }, new
        {
            line.Id, line.StockCountId, line.RowVersion, line.BalLocId, line.ICode, line.PhysicalQty,
            HeaderVersion = line.Header.RowVersion, line.Header.Status, line.Header.CountDate,
            GenerationId = generation.Id, generation.PreviousGenerationId, generation.AnchorPostingId,
            generation.AnchorPostingSequence, generation.WritePostingId, generation.SignedAdjustmentQty,
            quantity, receiptValue, candidateKey, removingOwnerId,
            BasisId = basis?.Id, FallbackUnitCost = basis?.FrozenFallbackUnitCost
        }).SourceSnapshotHash;

        var physicalPlanHash = isRemoval ? physicalRemoval!.PlanHash : physicalInsert!.PlanHash;
        var fullFingerprint = StockPostingFingerprint.Create(new
        {
            proofHash, snapshot.SnapshotHash, physicalPlanHash,
            newSignedAdj, newCountValue, finalItemQty, finalItemVal,
            removedLayerId, CandidateAllocations = candidateAllocations,
            NewCountAllocations = newCountAllocations, FinalLayers = finalLayers
        }, new { }).RequestFingerprint;

        return new StockReplayFifoSingleCountPlan(
            companyCode, branchCode, itemCode, balLocId, isReceipt, isRemoval,
            candidateKey, quantity, receiptValue, effectiveBaseUom,
            physicalInsert, physicalRemoval, removingOwnerId, snapshot, beforeCandidate,
            new StockReplayFifoSingleCountProof(line.StockCountId, line.Id, generation.Id, proofHash),
            movement.Key, movement.SignedDelta, newSignedAdj, newCountValue, newCountUsedFallback,
            removedLayerId, candidateAllocations, newCountAllocations, finalLayers,
            finalItemQty, finalItemVal, fullFingerprint);
    }

    internal static async Task<IReadOnlyList<int>> ApplyAsync(
        StockPostingContext context,
        StockReplayFifoSingleCountPlan plan,
        StockCountCorrectionWritePermit countPermit,
        int batchNo,
        CancellationToken cancellationToken,
        bool goodsReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(countPermit);
        context.EnsureUnsealed();

        var db = context.Db;
        var countLineId = plan.Proof.LineId;
        if (countPermit.Lines.Length != 1 || countPermit.Lines[0].LineId != countLineId
            || countPermit.Lines[0].PreviousGenerationId != plan.Proof.GenerationId
            || countPermit.Lines[0].NewSignedAdjustmentQty != plan.NewSignedAdjustmentQty)
            throw Invalid("FIFO count correction does not match its prepared permit.");

        var state = await db.StockCostStates.SingleOrDefaultAsync(s =>
            s.CompanyCode == context.CompanyCode && s.BranchCode == context.BranchCode
            && s.ItemCode == plan.ItemCode, cancellationToken);
        if (state is null || state.CostMethod != StockCostMethods.Fifo
            || state.OnHandBaseQty != plan.Snapshot.CurrentQuantity
            || state.InventoryValue != plan.Snapshot.CurrentValue)
            throw Invalid("FIFO item state changed before single-count replay.");

        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw Invalid("FIFO replay company has no base currency.");

        // 1. Apply candidate physical mutation and load/create candidate history.
        IvTrxHistory candidateHistory;
        if (!plan.IsRemoval && plan.IsReceipt)
            candidateHistory = await ApplyReceiptPhysicalInsertAsync(
                context, plan, batchNo, goodsReceipt, cancellationToken);
        else if (plan.IsRemoval && plan.IsReceipt)
            candidateHistory = await ApplyReceiptPhysicalRemovalAsync(
                context, plan, batchNo, goodsReceipt, cancellationToken);
        else if (!plan.IsRemoval)
            candidateHistory = await ApplyIssuePhysicalInsertAsync(
                context, plan, batchNo, cancellationToken);
        else
            candidateHistory = await ApplyIssuePhysicalRemovalAsync(
                context, plan, batchNo, cancellationToken);

        // 2. Append count physical ADJ reversal/replacement histories & new generation (lines 2 & 3).
        var correctedCounts = await StockCountCorrectionHistoryWriter.AppendAsync(
            context, countPermit, [new StockCountCorrectionPostingLines(countLineId, 2, 3)],
            new RunningNumberService(), cancellationToken);
        if (correctedCounts.Count != 1)
            throw Invalid("FIFO count correction did not produce one active count generation.");
        var newGeneration = correctedCounts[0];
        var oldGeneration = await db.StockCountReplayGenerations.AsNoTracking()
            .SingleAsync(g => g.Id == plan.Proof.GenerationId, cancellationToken);
        var anchor = await db.StockPostings.AsNoTracking()
            .SingleAsync(p => p.Id == oldGeneration.AnchorPostingId, cancellationToken);
        var basis = plan.NewSignedAdjustmentQty > 0m
            ? await db.StockCountCostBases.AsNoTracking()
                .SingleAsync(b => b.StockCountLineId == countLineId, cancellationToken)
            : null;

        var layers = await db.StockFifoLayers
            .Where(l => l.CompanyCode == context.CompanyCode
                && l.BranchCode == context.BranchCode
                && l.ItemCode == plan.ItemCode)
            .ToArrayAsync(cancellationToken);
        var layersById = layers.ToDictionary(l => l.Id);

        var writtenFacts = new List<StockValuationFact>();

        // 3. Reverse old count valuation/layer/consumptions (postingLineNo = 2) FIRST so removed MR layers restore cleanly.
        long? closedOldCountLayerId = null;
        if (oldGeneration.AdjustmentHistoryId is int oldCountHistoryId)
        {
            var oldHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.Id == oldCountHistoryId, cancellationToken);
            var oldActiveFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(
                db, context.CompanyCode, context.BranchCode, [oldHistory], cancellationToken))[oldCountHistoryId]
                .OrderBy(f => f.SplitOrdinal).ToArray();
            var reversals = await StockReplayValuationReversalWriter.AppendAsync(
                context, oldCountHistoryId,
                oldActiveFacts.Select((f, idx) => new StockReplayValuationReversalLine(
                    f.Id, 2, idx, newGeneration.ReversalHistoryId)).ToArray(),
                cancellationToken);
            writtenFacts.AddRange(reversals);
            var revHistory = await db.IvTrxHistories.SingleAsync(
                h => h.Id == newGeneration.ReversalHistoryId, cancellationToken);
            revHistory.ValuationStatus = StockValuationStatuses.Reversed;

            if (plan.OldSignedAdjustmentQty > 0m)
            {
                if (oldActiveFacts.Length != 1)
                    throw Invalid("Old positive FIFO count has ambiguous valuation facts.");
                var oldCountLayer = layers.SingleOrDefault(l => l.OriginValuationFactId == oldActiveFacts[0].Id);
                if (oldCountLayer is null || oldCountLayer.RemainingQty != oldCountLayer.OriginalQty
                    || oldCountLayer.RemainingValue != oldCountLayer.OriginalValue)
                    throw Invalid("Old positive FIFO count layer is consumed or missing.");
                oldCountLayer.RemainingQty = 0m;
                oldCountLayer.RemainingValue = 0m;
                oldCountLayer.CurrentUnitCost = 0m;
                oldCountLayer.Status = StockFifoLayerStatuses.Closed;
                closedOldCountLayerId = oldCountLayer.Id;
            }
            else
            {
                var oldFactIds = oldActiveFacts.Select(f => f.Id).ToArray();
                var oldConsumptions = await db.StockFifoLayerConsumptions.AsNoTracking()
                    .Where(c => oldFactIds.Contains(c.IssueValuationFactId) && c.ReversesConsumptionId == null)
                    .OrderBy(c => c.SplitOrdinal).ThenBy(c => c.Id)
                    .ToArrayAsync(cancellationToken);
                if (oldConsumptions.Length != oldActiveFacts.Length)
                    throw Invalid("Old negative FIFO count lacks exact layer consumptions.");
                for (var i = 0; i < oldActiveFacts.Length; i++)
                {
                    var oldCons = oldConsumptions[i];
                    var revFact = reversals.Single(r => r.ReversesValuationFactId == oldCons.IssueValuationFactId);
                    db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
                    {
                        CompanyCode = context.CompanyCode,
                        BranchCode = context.BranchCode,
                        IssueValuationFact = revFact,
                        FifoLayerId = oldCons.FifoLayerId,
                        ConsumedQty = oldCons.ConsumedQty,
                        ConsumedValue = oldCons.ConsumedValue,
                        SplitOrdinal = oldCons.SplitOrdinal,
                        ReversesConsumptionId = oldCons.Id,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy = context.UserId
                    });
                }
            }
        }

        // 4. Write candidate valuation & layer/consumption changes (postingLineNo = 1).
        StockFifoLayer? insertedCandidateLayer = null;
        if (!plan.IsRemoval && plan.IsReceipt)
        {
            var projectedCandidateLayer = plan.FinalLayers.Single(l => l.LayerId == CandidateLayerPlaceholderId);
            var val = plan.ReceiptValue!.Value;
            var unit = StockLedgerPrecision.Money(val / plan.Quantity);
            var srcLine = candidateHistory.TrxLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var receiptFact = new StockValuationFact
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = 1,
                SplitOrdinal = 0,
                SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{srcLine}:1:0",
                SourceDocumentType = context.Posting.SourceDocumentType,
                SourceDocumentId = context.Posting.SourceDocumentId,
                SourceDocumentNo = context.Posting.SourceDocumentNo,
                SourceDocumentLine = srcLine,
                EffectiveAt = plan.CandidateKey.EffectiveAt,
                BusinessDate = context.Posting.BusinessDate,
                PeriodKey = context.Posting.PeriodKey,
                ItemCode = plan.ItemCode,
                WarehouseCode = BlankToNull(candidateHistory.ToWarehouse),
                LocationCode = BlankToNull(candidateHistory.ToLocation),
                LotId = candidateHistory.ToLotId,
                LotNo = candidateHistory.ToLotNo,
                ItemStatus = BlankToNull(candidateHistory.IStatus),
                BaseUom = plan.ReceiptBaseUom!,
                MovementCode = "RECEIPT_IN",
                Direction = 1,
                BaseQty = plan.Quantity,
                CostMethod = StockCostMethods.Fifo,
                UnitCost = unit,
                CostAmount = val,
                BaseCurrency = baseCurrency,
                BaseCostAmount = val,
                ValuationSource = StockValuationSources.Fifo,
                ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = 1,
                InventoryHistoryId = candidateHistory.Id,
                ToBalLocId = candidateHistory.ToBalLocId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = context.UserId
            };
            db.StockValuationFacts.Add(receiptFact);
            insertedCandidateLayer = new StockFifoLayer
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                ItemCode = plan.ItemCode,
                BaseUom = plan.ReceiptBaseUom!,
                OriginValuationFact = receiptFact,
                OriginStockPosting = context.Posting,
                ReceiptEffectiveAt = plan.CandidateKey.EffectiveAt,
                OriginalQty = plan.Quantity,
                RemainingQty = projectedCandidateLayer.RemainingQuantity,
                OriginalValue = val,
                AccumulatedAdjustment = 0m,
                RemainingValue = projectedCandidateLayer.RemainingValue,
                CurrentUnitCost = projectedCandidateLayer.CurrentUnitCost,
                SourceDocumentType = receiptFact.SourceDocumentType,
                SourceDocumentNo = receiptFact.SourceDocumentNo,
                SourceDocumentLine = receiptFact.SourceDocumentLine,
                WarehouseCode = receiptFact.WarehouseCode,
                LotId = receiptFact.LotId,
                LotNo = receiptFact.LotNo,
                Status = projectedCandidateLayer.Status
            };
            db.StockFifoLayers.Add(insertedCandidateLayer);
            candidateHistory.ValuationStatus = StockValuationStatuses.Valued;
            writtenFacts.Add(receiptFact);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (plan.IsRemoval && plan.IsReceipt)
        {
            var removedLayer = layersById[plan.RemovedLayerId!.Value];
            var origHistId = checked((int)plan.PhysicalRemoval!.RemovedActiveEvent.EventId);
            var removals = await StockReplayValuationReversalWriter.AppendAsync(
                context, origHistId,
                [new StockReplayValuationReversalLine(removedLayer.OriginValuationFactId, 1, 0, candidateHistory.Id)],
                cancellationToken);
            writtenFacts.AddRange(removals);
            candidateHistory.ValuationStatus = StockValuationStatuses.Reversed;
            removedLayer.RemainingQty = 0m;
            removedLayer.RemainingValue = 0m;
            removedLayer.CurrentUnitCost = 0m;
            removedLayer.Status = StockFifoLayerStatuses.Closed;
        }
        else if (!plan.IsRemoval)
        {
            var srcLine = candidateHistory.TrxLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var moveCode = StockReplayFifoInventoryIssueKinds.MovementCode(candidateHistory.TrxType);
            for (var ordinal = 0; ordinal < plan.CandidateAllocations.Count; ordinal++)
            {
                var alloc = plan.CandidateAllocations[ordinal];
                var issueFact = new StockValuationFact
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    LedgerEpochId = context.Epoch.Id,
                    StockPostingId = context.Posting.Id,
                    PostingLineNo = 1,
                    SplitOrdinal = ordinal,
                    SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{srcLine}:1:{ordinal}",
                    SourceDocumentType = context.Posting.SourceDocumentType,
                    SourceDocumentId = context.Posting.SourceDocumentId,
                    SourceDocumentNo = context.Posting.SourceDocumentNo,
                    SourceDocumentLine = srcLine,
                    EffectiveAt = plan.CandidateKey.EffectiveAt,
                    BusinessDate = context.Posting.BusinessDate,
                    PeriodKey = context.Posting.PeriodKey,
                    ItemCode = plan.ItemCode,
                    WarehouseCode = BlankToNull(candidateHistory.FrWarehouse),
                    LocationCode = BlankToNull(candidateHistory.FrLocation),
                    LotId = candidateHistory.FromLotId,
                    LotNo = candidateHistory.FrLotNo,
                    ItemStatus = BlankToNull(candidateHistory.IStatus),
                    BaseUom = candidateHistory.FrStdUom!,
                    MovementCode = moveCode,
                    Direction = -1,
                    BaseQty = alloc.Quantity,
                    CostMethod = StockCostMethods.Fifo,
                    UnitCost = alloc.UnitCost,
                    CostAmount = alloc.Value,
                    BaseCurrency = baseCurrency,
                    BaseCostAmount = alloc.Value,
                    ValuationSource = StockValuationSources.Fifo,
                    ValuationStatus = StockValuationStatuses.Valued,
                    ValuationVersion = 1,
                    InventoryHistoryId = candidateHistory.Id,
                    FromBalLocId = candidateHistory.FromBalLocId,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };
                db.StockValuationFacts.Add(issueFact);
                db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    IssueValuationFact = issueFact,
                    FifoLayerId = alloc.LayerId,
                    ConsumedQty = alloc.Quantity,
                    ConsumedValue = alloc.Value,
                    SplitOrdinal = ordinal,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = context.UserId
                });
                writtenFacts.Add(issueFact);
            }
            candidateHistory.ValuationStatus = StockValuationStatuses.Valued;
        }
        else
        {
            var origHistId = checked((int)plan.PhysicalRemoval!.RemovedActiveEvent.EventId);
            var origHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.Id == origHistId, cancellationToken);
            var origFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(
                db, context.CompanyCode, context.BranchCode, [origHistory], cancellationToken))[origHistId]
                .OrderBy(f => f.SplitOrdinal).ToArray();
            var origFactIds = origFacts.Select(f => f.Id).ToArray();
            var origCons = await db.StockFifoLayerConsumptions.AsNoTracking()
                .Where(c => origFactIds.Contains(c.IssueValuationFactId) && c.ReversesConsumptionId == null)
                .ToArrayAsync(cancellationToken);
            if (origCons.Length != origFacts.Length)
                throw Invalid("Removed FIFO issue lacks exact active layer consumptions.");
            var removals = await StockReplayValuationReversalWriter.AppendAsync(
                context, origHistId,
                origFacts.Select((f, idx) => new StockReplayValuationReversalLine(
                    f.Id, 1, idx, candidateHistory.Id)).ToArray(),
                cancellationToken);
            writtenFacts.AddRange(removals);
            foreach (var cons in origCons)
            {
                var revFact = removals.Single(r => r.ReversesValuationFactId == cons.IssueValuationFactId);
                db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    IssueValuationFact = revFact,
                    FifoLayerId = cons.FifoLayerId,
                    ConsumedQty = cons.ConsumedQty,
                    ConsumedValue = cons.ConsumedValue,
                    SplitOrdinal = cons.SplitOrdinal,
                    ReversesConsumptionId = cons.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    CreatedBy = context.UserId
                });
            }
            candidateHistory.ValuationStatus = StockValuationStatuses.Reversed;
        }

        // 5. Write replacement count valuation & layer/consumptions (postingLineNo = 3).
        if (newGeneration.AdjustmentHistoryId is int newCountHistoryId)
        {
            var countHistory = await db.IvTrxHistories.SingleAsync(h => h.Id == newCountHistoryId, cancellationToken);
            var countSourceLine = countHistory.TrxLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (plan.NewSignedAdjustmentQty > 0m)
            {
                var unitCost = StockLedgerPrecision.Money(plan.NewCountValue / plan.NewSignedAdjustmentQty);
                var valSource = plan.NewCountUsedFallback
                    ? basis!.CostBasisKind == StockCountCostBasisKinds.LedgerFifo
                        ? StockValuationSources.CountFrozenV2
                        : StockValuationSources.ManualApproved
                    : StockValuationSources.Fifo;
                var fact = BuildCountAdjustmentFact(context, anchor, oldGeneration.EffectiveAt, countHistory,
                    countSourceLine, 3, 0, plan.NewSignedAdjustmentQty, plan.NewCountValue, unitCost, 1,
                    valSource, baseCurrency);
                db.StockValuationFacts.Add(fact);
                var projectedNewCount = plan.FinalLayers.Single(l => l.LayerId == NewCountLayerPlaceholderId);
                db.StockFifoLayers.Add(new StockFifoLayer
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    ItemCode = plan.ItemCode,
                    BaseUom = fact.BaseUom,
                    OriginValuationFact = fact,
                    OriginStockPosting = context.Posting,
                    ReceiptEffectiveAt = oldGeneration.EffectiveAt,
                    OriginalQty = plan.NewSignedAdjustmentQty,
                    RemainingQty = projectedNewCount.RemainingQuantity,
                    OriginalValue = plan.NewCountValue,
                    AccumulatedAdjustment = 0m,
                    RemainingValue = projectedNewCount.RemainingValue,
                    CurrentUnitCost = projectedNewCount.CurrentUnitCost,
                    SourceDocumentType = fact.SourceDocumentType,
                    SourceDocumentNo = fact.SourceDocumentNo,
                    SourceDocumentLine = fact.SourceDocumentLine,
                    WarehouseCode = fact.WarehouseCode,
                    LotId = fact.LotId,
                    LotNo = fact.LotNo,
                    Status = projectedNewCount.Status
                });
                writtenFacts.Add(fact);
            }
            else
            {
                for (var ordinal = 0; ordinal < plan.NewCountAllocations.Count; ordinal++)
                {
                    var alloc = plan.NewCountAllocations[ordinal];
                    var resolvedLayerId = alloc.LayerId == CandidateLayerPlaceholderId
                        ? insertedCandidateLayer!.Id
                        : alloc.LayerId;
                    var fact = BuildCountAdjustmentFact(context, anchor, oldGeneration.EffectiveAt, countHistory,
                        countSourceLine, 3, ordinal, alloc.Quantity, alloc.Value, alloc.UnitCost, -1,
                        StockValuationSources.Fifo, baseCurrency);
                    db.StockValuationFacts.Add(fact);
                    db.StockFifoLayerConsumptions.Add(new StockFifoLayerConsumption
                    {
                        CompanyCode = context.CompanyCode,
                        BranchCode = context.BranchCode,
                        IssueValuationFact = fact,
                        FifoLayerId = resolvedLayerId,
                        ConsumedQty = alloc.Quantity,
                        ConsumedValue = alloc.Value,
                        SplitOrdinal = ordinal,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy = context.UserId
                    });
                    writtenFacts.Add(fact);
                }
            }
            countHistory.ValuationStatus = StockValuationStatuses.Valued;
        }

        // 6. Apply exact final positions to all pre-existing non-archived FIFO layers and update StockCostState.
        var beforeById = plan.Snapshot.Layers.ToDictionary(l => l.LayerId);
        var finalByExistingId = plan.FinalLayers
            .Where(l => l.LayerId != CandidateLayerPlaceholderId && l.LayerId != NewCountLayerPlaceholderId)
            .ToDictionary(l => l.LayerId);
        foreach (var layer in layers)
        {
            if (layer.Id == closedOldCountLayerId || layer.Id == plan.RemovedLayerId)
                continue;
            if (!beforeById.ContainsKey(layer.Id))
            {
                if (layer.Status != StockFifoLayerStatuses.Closed
                    || layer.RemainingQty != 0m
                    || layer.RemainingValue != 0m
                    || layer.CurrentUnitCost != 0m)
                    throw Invalid($"Archived FIFO layer {layer.Id} is not cleanly closed.");
                continue;
            }
            if (!finalByExistingId.TryGetValue(layer.Id, out var final))
                throw Invalid($"FIFO layer {layer.Id} missing from single-count projection.");
            layer.RemainingQty = final.RemainingQuantity;
            layer.RemainingValue = final.RemainingValue;
            layer.CurrentUnitCost = final.CurrentUnitCost;
            layer.Status = final.Status;
        }

        await db.SaveChangesAsync(cancellationToken);

        state.OnHandBaseQty = plan.FinalItemQuantity;
        state.InventoryValue = plan.FinalItemValue;
        state.AverageUnitCost = state.OnHandBaseQty == 0m
            ? 0m
            : StockLedgerPrecision.Money(state.InventoryValue / state.OnHandBaseQty);
        state.CurrentUnitCost = state.AverageUnitCost;
        state.LastPostingSequence = context.Posting.PostingSequence;
        if (writtenFacts.Count > 0)
            state.LastValuationFactId = writtenFacts[^1].Id;
        await db.SaveChangesAsync(cancellationToken);

        // 7. Reconcile persisted layers and root facts fail-closed.
        var verifiedLayers = await db.StockFifoLayers.AsNoTracking()
            .Where(l => l.CompanyCode == context.CompanyCode
                && l.BranchCode == context.BranchCode
                && l.ItemCode == plan.ItemCode)
            .ToArrayAsync(cancellationToken);
        if (StockLedgerPrecision.Quantity(verifiedLayers.Sum(l => l.RemainingQty)) != plan.FinalItemQuantity
            || StockLedgerPrecision.Money(verifiedLayers.Sum(l => l.RemainingValue)) != plan.FinalItemValue
            || StockLedgerPrecision.Quantity(writtenFacts.Sum(f => f.SignedQty)) != 0m
            || StockLedgerPrecision.Money(writtenFacts.Sum(f => f.SignedValue))
                != StockLedgerPrecision.Money(plan.FinalItemValue - plan.Snapshot.CurrentValue))
            throw Invalid("FIFO single-count replay failed layer or value reconciliation.");

        var required = new List<int>(2);
        if (!plan.IsRemoval)
            required.Add(candidateHistory.Id);
        if (newGeneration.AdjustmentHistoryId is int activeCountHistory)
            required.Add(activeCountHistory);
        return required;
    }

    private static StockValuationFact BuildCountAdjustmentFact(
        StockPostingContext context, StockPosting anchor, DateTime effectiveAt,
        IvTrxHistory history, string sourceLine, int postingLineNo, int splitOrdinal,
        decimal qty, decimal value, decimal unitCost, int direction,
        string source, string baseCurrency) =>
        new()
        {
            CompanyCode = context.CompanyCode,
            BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id,
            PostingLineNo = postingLineNo,
            SplitOrdinal = splitOrdinal,
            SourceLineId = $"{anchor.SourceDocumentType}:{anchor.SourceDocumentId}:{sourceLine}:{postingLineNo}:{splitOrdinal}",
            SourceDocumentType = anchor.SourceDocumentType,
            SourceDocumentId = anchor.SourceDocumentId,
            SourceDocumentNo = anchor.SourceDocumentNo,
            SourceDocumentLine = sourceLine,
            EffectiveAt = effectiveAt,
            BusinessDate = anchor.BusinessDate,
            PeriodKey = anchor.PeriodKey,
            ItemCode = history.ICode,
            WarehouseCode = direction > 0 ? BlankToNull(history.ToWarehouse) : BlankToNull(history.FrWarehouse),
            LocationCode = direction > 0 ? BlankToNull(history.ToLocation) : BlankToNull(history.FrLocation),
            LotId = direction > 0 ? history.ToLotId : history.FromLotId,
            LotNo = direction > 0 ? history.ToLotNo : history.FrLotNo,
            ItemStatus = BlankToNull(history.IStatus),
            BaseUom = direction > 0 ? history.ToStdUom! : history.FrStdUom!,
            MovementCode = direction > 0 ? "ADJUST_IN" : "ADJUST_OUT",
            Direction = direction,
            BaseQty = qty,
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

    private static async Task<IvTrxHistory> ApplyReceiptPhysicalInsertAsync(
        StockPostingContext context, StockReplayFifoSingleCountPlan plan,
        int batchNo, bool goodsReceipt, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var physical = plan.PhysicalInsert!;
        var authority = goodsReceipt
            ? await StockReplayGrReceiptAuthorityReader.ReadAsync(
                db, context.CompanyCode, context.BranchCode, batchNo, plan.BalLocId, cancellationToken)
            : await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
                db, context.CompanyCode, context.BranchCode, batchNo, plan.BalLocId, cancellationToken, allowExistingLot: true);
        if (authority.Quantity != plan.Quantity || authority.Value != plan.ReceiptValue
            || authority.EffectiveAt != plan.CandidateKey.EffectiveAt)
            throw Invalid("The FIFO receipt authority changed after single-count planning.");
        var batch = await db.IvTrxBatches.SingleAsync(b => b.Id == authority.BatchId, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d => d.Id == authority.DetailId, cancellationToken);
        var balance = await db.IvBalLocs.SingleAsync(b =>
            b.Id == plan.BalLocId && b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode,
            cancellationToken);
        if (batch.BatchStatus != IvBatchStatuses.New || detail.DocumentRevision != context.Posting.DocumentRevision
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The FIFO receipt pile changed after single-count planning.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId;
        if (balance.StdQty == 0m)
        {
            balance.UnitPrice = authority.UnitCost;
            balance.Cost = authority.UnitCost;
            balance.PriceEvidence = goodsReceipt ? detail.PriceEvidence : "EXPLICIT_COMPANY_BASE_PRICE";
        }
        else if (balance.UnitPrice != authority.UnitCost)
            balance.PriceEvidence = null;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.ToBalLocId = balance.Id;
        detail.ToLotId = authority.LotId;
        var history = new IvTrxHistory
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, BatchNo = batchNo,
            TrxLineNo = detail.TrxLineNo,
            TrxType = goodsReceipt ? IvTrxTypes.GoodsReceive : IvTrxTypes.MiscellaneousReceipt,
            BatchStatus = IvBatchStatuses.Posted, RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode, ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = authority.ItemCode, IDesc = detail.IDesc,
            ToWarehouse = detail.ToWarehouse, ToLocation = detail.ToLocation, ToLotNo = detail.ToLotNo,
            ToStdQty = detail.ToStdQty, ToStdUom = detail.ToStdUom,
            ToPurQty = detail.ToPurQty, ToPurUom = detail.ToPurUom, IStatus = detail.IStatus,
            DoNo = detail.DoNo, PoNo = detail.PoNo, PoRelNo = detail.PoRelNo, PoLineNo = detail.PoLineNo,
            Remarks = detail.Remarks, PriceEvidence = detail.PriceEvidence,
            CostEvidenceType = detail.CostEvidenceType, CostOverrideReason = detail.CostOverrideReason,
            CostApprovedBy = detail.CostApprovedBy, CostApprovedAtUtc = detail.CostApprovedAtUtc,
            UnitPrice = detail.UnitPrice, Cost = detail.Cost, CostPrice = detail.CostPrice,
            BaseUnitPrices = detail.BaseUnitPrices, LocationCode = detail.LocationCode,
            ToBalLocId = balance.Id, ToLotId = authority.LotId, CreatedDate = now, CreatedBy = user
        };
        new IvInventoryHistoryWriter().StampGeneration(context, [history], context.Posting.DocumentRevision);
        db.IvTrxHistories.Add(history);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now; batch.PostedBy = user; batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid(); batch.ModifiedDate = now; batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return history;
    }

    private static async Task<IvTrxHistory> ApplyReceiptPhysicalRemovalAsync(
        StockPostingContext context, StockReplayFifoSingleCountPlan plan,
        int batchNo, bool goodsReceipt, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var physical = plan.PhysicalRemoval!;
        var removed = physical.RemovedActiveEvent;
        var batch = await db.IvTrxBatches.SingleAsync(b =>
            b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode && b.BatchNo == batchNo,
            cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d => d.BatchId == batch.Id, cancellationToken);
        var history = await db.IvTrxHistories.SingleAsync(h => h.Id == (int)removed.EventId, cancellationToken);
        var balance = await db.IvBalLocs.SingleAsync(b =>
            b.Id == plan.BalLocId && b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode,
            cancellationToken);
        if (batch.BatchStatus != IvBatchStatuses.Posted || history.StockPostingId != context.Posting.ReversesPostingId
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The removed FIFO receipt changed after single-count planning.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        var reversal = new IvInventoryHistoryWriter().AppendReversal(
            context, [history], context.Posting.DocumentRevision).Single();
        detail.ToBalLocId = null; detail.ToLotId = null;
        detail.DocumentRevision = checked(detail.DocumentRevision + 1);
        batch.BatchStatus = IvBatchStatuses.New; batch.RollbackDate = now; batch.RollbackBy = user;
        batch.RollbackCount += 1; batch.RollbackOperationId = Guid.NewGuid();
        batch.ModifiedDate = now; batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return reversal;
    }

    private static async Task<IvTrxHistory> ApplyIssuePhysicalInsertAsync(
        StockPostingContext context, StockReplayFifoSingleCountPlan plan,
        int batchNo, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var physical = plan.PhysicalInsert!;
        var batch = await db.IvTrxBatches.SingleAsync(b =>
            b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode && b.BatchNo == batchNo,
            cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d => d.BatchId == batch.Id, cancellationToken);
        var master = await db.IvStockMasters.AsNoTracking().SingleAsync(
            m => m.CompanyCode == context.CompanyCode && m.ICode == plan.ItemCode, cancellationToken);
        var balance = await db.IvBalLocs.SingleAsync(b =>
            b.Id == plan.BalLocId && b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode,
            cancellationToken);
        if (batch.BatchStatus != IvBatchStatuses.New
            || !await StockReplayInventoryIssuePileAuthority.MatchesAsync(db, master, balance, detail, cancellationToken)
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The FIFO issue pile changed after single-count planning.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.FromLotId = balance.LotId; detail.PriceEvidence = balance.PriceEvidence;
        var history = new IvTrxHistory
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, BatchNo = batchNo,
            TrxLineNo = detail.TrxLineNo, TrxType = detail.TrxType, BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo, ProdCode = detail.ProdCode ?? detail.ICode, ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = plan.ItemCode, IDesc = detail.IDesc,
            FrWarehouse = detail.FrWarehouse, FrLocation = detail.FrLocation, FrLotNo = detail.FrLotNo,
            FrStdQty = detail.FrStdQty, FrStdUom = detail.FrStdUom, FrPurQty = detail.FrPurQty, FrPurUom = detail.FrPurUom,
            PoNo = detail.TrxType == IvTrxTypes.VendorReturn ? detail.PoNo : null,
            PoRelNo = detail.TrxType == IvTrxTypes.VendorReturn ? detail.PoRelNo : null,
            PoLineNo = detail.TrxType == IvTrxTypes.VendorReturn ? detail.PoLineNo : null,
            IStatus = detail.IStatus, Remarks = detail.Remarks, PriceEvidence = detail.PriceEvidence,
            CostEvidenceType = detail.CostEvidenceType, CostOverrideReason = detail.CostOverrideReason,
            CostApprovedBy = detail.CostApprovedBy, CostApprovedAtUtc = detail.CostApprovedAtUtc,
            UnitPrice = detail.UnitPrice, Cost = detail.Cost, CostPrice = detail.CostPrice,
            BaseUnitPrices = detail.BaseUnitPrices, LocationCode = detail.LocationCode,
            FromBalLocId = balance.Id, FromLotId = balance.LotId, CreatedDate = now, CreatedBy = user
        };
        new IvInventoryHistoryWriter().StampGeneration(context, [history], context.Posting.DocumentRevision);
        db.IvTrxHistories.Add(history);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now; batch.PostedBy = user; batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid(); batch.ModifiedDate = now; batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return history;
    }

    private static async Task<IvTrxHistory> ApplyIssuePhysicalRemovalAsync(
        StockPostingContext context, StockReplayFifoSingleCountPlan plan,
        int batchNo, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var physical = plan.PhysicalRemoval!;
        var removed = physical.RemovedActiveEvent;
        var batch = await db.IvTrxBatches.SingleAsync(b =>
            b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode && b.BatchNo == batchNo,
            cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d => d.BatchId == batch.Id, cancellationToken);
        var history = await db.IvTrxHistories.SingleAsync(h => h.Id == (int)removed.EventId, cancellationToken);
        var master = await db.IvStockMasters.AsNoTracking().SingleAsync(
            m => m.CompanyCode == context.CompanyCode && m.ICode == plan.ItemCode, cancellationToken);
        var balance = await db.IvBalLocs.SingleAsync(b =>
            b.Id == plan.BalLocId && b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode,
            cancellationToken);
        if (batch.BatchStatus != IvBatchStatuses.Posted || history.StockPostingId != context.Posting.ReversesPostingId
            || !await StockReplayInventoryIssuePileAuthority.MatchesAsync(
                db, master, balance, detail, cancellationToken, requireActive: false)
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The removed FIFO issue changed after single-count planning.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        var reversal = new IvInventoryHistoryWriter().AppendReversal(
            context, [history], context.Posting.DocumentRevision).Single();
        detail.FromLotId = null; detail.DocumentRevision = checked(detail.DocumentRevision + 1);
        batch.BatchStatus = IvBatchStatuses.New; batch.RollbackDate = now; batch.RollbackBy = user;
        batch.RollbackCount += 1; batch.RollbackOperationId = Guid.NewGuid();
        batch.ModifiedDate = now; batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        return reversal;
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
