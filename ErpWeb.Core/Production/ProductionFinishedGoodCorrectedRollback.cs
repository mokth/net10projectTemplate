using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionFinishedGoodCorrectedRollbackPlan(
    int BatchId, long OwnerId, long MovementId, int HistoryId, long ReceiptFactId,
    long DeltaFactId, long CorrectionRootId, string ItemCode, int PileId, decimal Quantity,
    decimal OriginalValue, decimal CorrectedValue, DateTime ReceiptAt,
    long PoolId, decimal RestoredPoolQuantity, decimal RestoredPoolValue,
    long Watermark, string Hash);

/// <summary>
/// Plans the narrow case where one corrected WAC FG receipt is the terminal
/// event of an otherwise empty item and its WIP pool has one contribution.
/// More complex histories need general downstream replay and stay blocked.
/// </summary>
internal static class ProductionFinishedGoodCorrectedRollback
{
    public static async Task<ProductionFinishedGoodCorrectedRollbackPlan?> TryPrepareAsync(
        AppDbContext db, string company, string branch, int batchId,
        DateTime rollbackAt, CancellationToken ct)
    {
        var document = await db.ProductionFinishedGoodReceiptRows.AsNoTracking()
            .Include(r => r.Batch).SingleOrDefaultAsync(r => r.BatchId == batchId
                && r.CompanyCode == company && r.BranchCode == branch, ct);
        if (document?.PostingId is not long ownerId || document.Batch.BatchStatus != IvBatchStatuses.Posted)
            return null;
        var facts = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .Where(f => f.BatchId == batchId && f.StockPostingId == ownerId
                && f.ReversesFactId == null).ToArrayAsync(ct);
        if (facts.Length != 1) return null;
        var fact = facts[0];
        var restatements = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => f.ProductionBalLotMovementId == fact.ProductionMovementId).ToArrayAsync(ct);
        if (restatements.Length == 0) return null;
        var receiptFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.InventoryHistoryId == fact.InventoryHistoryId && f.StockPostingId == ownerId
            && f.ReversesValuationFactId == null && f.OriginalValuationFactId == null).ToArrayAsync(ct);
        if (receiptFacts.Length != 1) return null;
        var receipt = receiptFacts[0];
        if (fact.BaseQty <= 0m || receipt.BaseQty <= 0m)
            throw Invalid("The finished-good receipt has invalid quantity evidence.");
        var deltas = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.OriginalValuationFactId == receipt.Id && f.InventoryHistoryId == null)
            .ToArrayAsync(ct);
        if (deltas.Length == 0) return null; // Existing reversal guard handles mismatched corrections.
        var effective = (await ProductionReplayEffectiveCostReader.ReadAsync(db,
            company, branch, [fact.ProductionMovementId], ct))[fact.ProductionMovementId];
        if (effective.RestatementFactId is not long activeRestatementId)
        {
            if (await HasClearedCorrectionAsync(db, company, branch, fact.ProductionMovementId, ct))
                return null;
            throw Invalid("Finished-good correction history requires full downstream replay.");
        }
        var activeDeltas = deltas.Where(d => d.ReversesValuationFactId is null
            && !deltas.Any(r => r.ReversesValuationFactId == d.Id)).ToArray();
        if (activeDeltas.Length != 1)
            throw Invalid("Finished-good correction history has no unique active Inventory value.");
        var delta = activeDeltas[0];
        var activeRestatement = restatements.Single(r => r.Id == activeRestatementId);
        var cleared = await ProductionFinishedGoodCorrectionHistory.ReadAsync(db, company, branch,
            fact.ProductionMovementId, activeRestatementId, delta.Id, ct);
        if (cleared is null)
            throw Invalid("Finished-good correction history requires verified earlier reversals.");
        var movement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(m => m.Uid == fact.ProductionMovementId, ct);
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleAsync(h => h.Id == fact.InventoryHistoryId, ct);
        var source = await db.ProductionFinishedGoodSourceRows.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == fact.SourceId, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == ownerId, ct);
        var link = await db.ProductionPostingLinks.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Uid == movement.PostingLinkId, ct);
        var root = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == delta.StockPostingId, ct);
        var run = root is null ? null : await db.StockReplayRuns.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RootStockPostingId == root.Id, ct);
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
            && e.AffectedEntityId == delta.Id).ToArrayAsync(ct);
        if (root is null || run is null || effects.Length != 1
            || root.CompanyCode != company || root.BranchCode != branch || root.SealedAtUtc is null
            || root.LedgerEpochId != owner.LedgerEpochId || root.PostingSequence <= owner.PostingSequence
            || root.SourceModule != "INVENTORY"
            || root.SourceDocumentType is not (IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)
            || root.PostingRole is not ("PRIMARY" or "REVERSAL")
            || root.EffectiveAt > owner.EffectiveAt || root.EffectiveAt.Year != owner.EffectiveAt.Year
            || root.EffectiveAt.Month != owner.EffectiveAt.Month
            || run.CompanyCode != company || run.BranchCode != branch
            || run.RootRequestId != root.RequestId || run.RootDocumentType != root.SourceDocumentType
            || run.RootDocumentId != root.SourceDocumentId || run.EarliestEffectiveAt > owner.EffectiveAt
            || run.LatestEffectiveAt < owner.EffectiveAt || string.IsNullOrWhiteSpace(run.PlanHash)
            || effects[0].CompanyCode != company || effects[0].BranchCode != branch
            || effects[0].ReplayRunId != run.Id || effects[0].RootStockPostingId != root.Id
            || effects[0].BusinessOwnerPostingId != ownerId || effects[0].OriginalEntityId != receipt.Id
            || effects[0].EffectKind != StockReplayEffectTypes.FinishedGoodReceiptValue
            || activeRestatement.ReplayStockPostingId != root.Id
            || effective.RestatementFactId != activeRestatement.Id
            || effective.TotalCost != StockLedgerPrecision.Money(receipt.CostAmount + delta.SignedValue)
            || effective.TotalCost == fact.TotalValue || receipt.CostAmount != fact.TotalValue
            || receipt.BaseQty != fact.BaseQty || receipt.BaseCostAmount != receipt.CostAmount
            || receipt.CostMethod != StockCostMethods.MovingAverage
            || delta.ValuationSource != StockValuationSources.ProductionActual
            || delta.BaseCostAmount != delta.CostAmount || delta.ValuationVersion != receipt.ValuationVersion + 1
            || delta.UnitCost != StockLedgerPrecision.Money(delta.CostAmount / receipt.BaseQty)
            || delta.MovementCode != receipt.MovementCode || delta.BaseUom != receipt.BaseUom
            || delta.FromBalLocId != receipt.FromBalLocId || delta.ToBalLocId != receipt.ToBalLocId
            || delta.WarehouseCode != receipt.WarehouseCode || delta.LocationCode != receipt.LocationCode
            || delta.LotId != receipt.LotId || delta.LotNo != receipt.LotNo || delta.ItemStatus != receipt.ItemStatus
            || delta.TransactionCurrency is not null || delta.TransactionCostAmount is not null || delta.ExchangeRate is not null
            || movement.StockPostingId != ownerId || movement.MovementType != ProductionBalLotMovementTypes.FgReceiptOut
            || movement.OriginalMovementId is not null || movement.ValuationStatus != ProductionPoolValuationService.Verified
            || movement.LedgerVersion != 2 || movement.LedgerEpochId != owner.LedgerEpochId
            || movement.BaseQty != fact.BaseQty || movement.TotalCost != fact.TotalValue
            || movement.InventoryHistoryId != history.Id || history.ToBalLocId != fact.DestinationBalanceId
            || history.EvidenceBaseQty != fact.BaseQty || history.ExactTransferredValue != fact.TotalValue
            || owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
            || owner.SourceModule != "PRODUCTION" || owner.SourceDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || owner.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptPost
            || owner.SourceDocumentId != batchId.ToString(CultureInfo.InvariantCulture)
            || owner.ProductionPostingLinkId != movement.PostingLinkId
            || link is null || link.CompanyCode != company || link.BranchCode != branch
            || link.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptPost
            || link.Status != ProductionPostingLinkStatuses.Succeeded
            || link.WorkOrderId != document.WorkOrderId || link.DocumentRevision != owner.DocumentRevision
            || link.ProductionDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || link.ProductionDocumentNo != owner.SourceDocumentNo || link.InventoryBatchNo != document.Batch.BatchNo
            || movement.SourceLineId != fact.SourceId.ToString(CultureInfo.InvariantCulture)
            || movement.ItemCode != history.ICode || movement.WorkOrderId != document.WorkOrderId
            || history.BatchNo != document.Batch.BatchNo || history.DocumentRevision != owner.DocumentRevision
            || history.LedgerEpochId != owner.LedgerEpochId
            || history.TrxType != IvTrxTypes.FinishedGoods || document.DocumentRevision != owner.DocumentRevision
            || source is null || source.BatchId != batchId || source.DocumentRevision != owner.DocumentRevision
            || source.ProductionBalLotId != movement.ProductionBalLotId || source.BaseQty != fact.BaseQty
            || source.RequestedQty != movement.Qty || source.SourceUom != movement.Uom
            || source.BaseUom != movement.BaseUom || source.SourceFactor != movement.ConversionFactorToBase
            || owner.PostingRole != "PRIMARY" || owner.EffectiveAt != movement.MovementDate
            || rollbackAt < owner.EffectiveAt
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == ownerId, ct)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.ReversesHistoryId == history.Id, ct))
            throw Invalid("The finished-good Production and Inventory corrections do not share verified ownership.");
        StockReplayWacValueEventFactory.CreateReceiptRestatement(delta, receipt, history, owner, root);
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(db, company, branch, [history], ct);
        if (active[history.Id].Count != 1 || active[history.Id][0].Id != receipt.Id
            || await new StockCostMethodResolver().ResolveAsync(db, company, branch, rollbackAt, ct)
                != StockCostMethods.MovingAverage)
            throw Invalid("The finished-good receipt no longer has one active WAC valuation.");

        var poolMovements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotId == movement.ProductionBalLotId).ToArrayAsync(ct);
        var inbounds = poolMovements.Where(m => m.MovementType == ProductionBalLotMovementTypes.Produce
            && m.OriginalMovementId is null).ToArray();
        if (poolMovements.Length != 2 || inbounds.Length != 1)
            throw Invalid("The corrected finished-good source requires full Production replay.");
        var inbound = inbounds[0];
        var inboundEffective = (await ProductionReplayEffectiveCostReader.ReadAsync(db,
            company, branch, [inbound.Uid], ct))[inbound.Uid];
        var inboundRestatements = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => f.ProductionBalLotMovementId == inbound.Uid).ToArrayAsync(ct);
        var lot = await db.ProductionBalLots.AsNoTracking().SingleAsync(l => l.Uid == movement.ProductionBalLotId, ct);
        var pool = await db.ProductionPoolValuationRows.AsNoTracking().SingleAsync(p => p.ProductionBalLotId == lot.Uid, ct);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .SingleOrDefaultAsync(e => e.MovementId == movement.Uid, ct);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(a => a.OutboundMovementId == movement.Uid).ToArrayAsync(ct);
        var dependencies = await ProductionActiveEdgeResolver.Dependencies(db).AsNoTracking()
            .Where(e => e.ConsumerMovementId == movement.Uid).ToArrayAsync(ct);
        var expectedValue = FinishedGoodReceiptMath.AllocateValue(inbound.BaseQty,
            inboundEffective.TotalCost, [(fact.SourceId, fact.BaseQty)])[fact.SourceId];
        var inboundMatchesCorrection = inboundEffective.RestatementFactId is long inboundActiveId
            && inboundRestatements.Any(r => r.Id == inboundActiveId && r.ReplayStockPostingId == root.Id)
            && (cleared.Count == 0 ? inboundRestatements.Length == 1
                : await ProductionFinishedGoodCorrectionHistory.MatchesOutputAsync(db, movement.Uid, inbound.Uid, ct));
        var sourceInbound = root.ReversesPostingId is long sourceId
            ? inboundRestatements.SingleOrDefault(r => r.ReplayStockPostingId == sourceId
                && r.ReversesRestatementFactId is null) : null;
        var inboundRestoredByRemoval = root.PostingRole == "REVERSAL"
            && root.ReversesPostingId is long originalRootId
            && inboundEffective.RestatementFactId is null && sourceInbound is not null
            && sourceInbound.ReplayStockPostingId == originalRootId
            && inboundRestatements.Count(r => r.ReplayStockPostingId == root.Id
                && r.ReversesRestatementFactId == sourceInbound.Id) == 1
            && (cleared.Count == 0 ? inboundRestatements.Length == 2
                : await ProductionFinishedGoodCorrectionHistory.MatchesOutputAsync(db, movement.Uid, inbound.Uid, ct));
        if ((!inboundMatchesCorrection && !inboundRestoredByRemoval)
            || inbound.CompanyCode != company || inbound.BranchCode != branch
            || inbound.LedgerEpochId != owner.LedgerEpochId || inbound.BaseQty < movement.BaseQty
            || inbound.ProductionBalLotId != lot.Uid || inbound.ItemCode != movement.ItemCode
            || inbound.WorkOrderId != movement.WorkOrderId || inbound.WorkOrderOperationId != movement.WorkOrderOperationId
            || inbound.RouteStepId != movement.RouteStepId
            || lot.CompanyCode != company || lot.BranchCode != branch
            || lot.WorkOrderId != movement.WorkOrderId || lot.WorkOrderOperationId != movement.WorkOrderOperationId
            || lot.ProducingRouteStepId != movement.RouteStepId
            || lot.BaseQty != inbound.BaseQty - fact.BaseQty
            || lot.TotalCost != StockLedgerPrecision.Money(inboundEffective.TotalCost - effective.TotalCost)
            || lot.AverageUnitCost != (lot.BaseQty == 0m ? 0m : StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty))
            || pool.Generation <= 0 || pool.Status != ProductionPoolValuationService.Verified || pool.TrackedBaseQty != lot.BaseQty
            || pool.TrackedValue != lot.TotalCost || expectedValue != effective.TotalCost
            || evidence is null || evidence.ProductionBalLotId != lot.Uid || evidence.Generation != pool.Generation
            || evidence.Status != ProductionPoolValuationService.Verified || evidence.Basis != "POOLED_AVERAGE"
            || evidence.Currency != "COMPANY_BASE" || evidence.Price != movement.UnitCost
            || evidence.PriceUom != movement.BaseUom || evidence.ConversionFactor != movement.ConversionFactorToBase
            || evidence.InventoryHistoryId != history.Id || evidence.OriginalMovementId is not null
            || allocations.Length != 1 || allocations[0].ReceiptMovementId != inbound.Uid
            || allocations[0].StockPostingId != ownerId || allocations[0].BaseQty != fact.BaseQty
            || dependencies.Length != 1 || dependencies[0].ContributorMovementId != inbound.Uid
            || dependencies[0].StockPostingId != ownerId)
            throw Invalid("The corrected finished-good source pool is inconsistent.");

        var state = await db.StockCostStates.AsNoTracking().SingleOrDefaultAsync(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.ItemCode == receipt.ItemCode, ct);
        var piles = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == receipt.ItemCode).ToArrayAsync(ct);
        if (state is null || state.CostMethod != StockCostMethods.MovingAverage
            || state.LastValuationFactId != delta.Id || state.LastPostingSequence != root.PostingSequence
            || state.OnHandBaseQty != fact.BaseQty || state.InventoryValue != effective.TotalCost
            || state.AverageUnitCost != StockLedgerPrecision.Money(state.InventoryValue / state.OnHandBaseQty)
            || state.CurrentUnitCost != state.AverageUnitCost || piles.Length != 1
            || piles[0].Id != fact.DestinationBalanceId || piles[0].StdQty != fact.BaseQty)
            throw Invalid("Finished goods have later stock activity requiring full replay.");
        var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            owner.PostingSequence, $"H{history.Id:D10}", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch,
            receipt.ItemCode, key, ct);
        if (tail.LaterEvents.Count != 1 || tail.LaterEvents[0].EventId != long.MaxValue - receipt.Id
            || tail.LaterEvents[0].OldSignedValue != delta.SignedValue
            || tail.CurrentQuantity != state.OnHandBaseQty || tail.CurrentValue != state.InventoryValue)
            throw Invalid("Finished goods have a later WAC event requiring full replay.");
        var watermark = await db.StockPostingBranchSequences.AsNoTracking()
            .Where(s => s.CompanyCode == company && s.BranchCode == branch)
            .Select(s => s.LastSequence).SingleAsync(ct);
        var hash = StockPostingFingerprint.Create(new { batchId, ownerId, rollbackAt }, new
        {
            Watermark = watermark, DocumentVersion = Convert.ToHexString(document.RowVersion ?? []),
            FactId = fact.Id, MovementId = movement.Uid, InboundId = inbound.Uid, InboundValue = inboundEffective.TotalCost,
            RestatementId = activeRestatement.Id, DeltaId = delta.Id, RootId = root.Id, cleared.Count, cleared.LastFactId,
            ReceiptId = receipt.Id, receipt.CostAmount, CorrectedValue = effective.TotalCost,
            StateId = state.Id, state.OnHandBaseQty, state.InventoryValue, state.LastValuationFactId,
            state.LastPostingSequence, StateVersion = Convert.ToHexString(state.RowVersion ?? []),
            lot.BaseQty, lot.TotalCost, LotVersion = Convert.ToHexString(lot.RowVersion ?? []),
            PoolGeneration = pool.Generation, PileId = piles[0].Id, piles[0].StdQty,
            AllocationId = allocations[0].Id, DependencyId = dependencies[0].Id
        }).SourceSnapshotHash;
        return new(batchId, ownerId, movement.Uid, history.Id, receipt.Id, delta.Id, root.Id,
            receipt.ItemCode, piles[0].Id, fact.BaseQty, receipt.CostAmount,
            effective.TotalCost, owner.EffectiveAt, lot.Uid,
            IvQty.Round(lot.BaseQty + fact.BaseQty),
            StockLedgerPrecision.Money(lot.TotalCost + effective.TotalCost), watermark, hash);
    }

    public static async Task ApplyWithinRootAsync(StockPostingContext context,
        ProductionFinishedGoodCorrectedRollbackPlan plan, CancellationToken ct)
    {
        context.EnsureUnsealed();
        if (context.Posting.ReversesPostingId != plan.OwnerId
            || context.Posting.PostingSequence != plan.Watermark + 1)
            throw Invalid("The finished-good rollback changed after planning.");
        context.CostMethod = await new StockCostMethodResolver().ResolveAsync(context.Db,
            context.CompanyCode, context.BranchCode, context.Posting.EffectiveAt, ct);
        if (context.CostMethod != StockCostMethods.MovingAverage)
            throw Invalid("The corrected finished-good rollback requires WAC valuation.");
        var db = context.Db;
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = plan.ReceiptAt, LatestEffectiveAt = context.Posting.EffectiveAt,
            PlanWatermark = plan.Watermark, PlanHash = plan.Hash, AffectedHistoryCount = 1,
            AffectedItemCount = 1, AffectedProductionMovementCount = 1,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        });
        await db.SaveChangesAsync(ct);
        var result = await ProductionFinishedGoodWacReceiptCorrection.AppendWithinRootAsync(context,
            plan.HistoryId, plan.ReceiptFactId, plan.OriginalValue, 1000001, 1000002, ct);
        if (result?.Reversal is null || result.Reversal.ReversesValuationFactId != plan.DeltaFactId
            || result.ActiveDelta is not null)
            throw Invalid("The finished-good Inventory correction changed after planning.");
        var permit = new StockReplayWacWritePermit(context, plan.ItemCode, plan.Hash,
            plan.Quantity, plan.CorrectedValue, plan.Quantity, plan.OriginalValue,
            plan.PileId, plan.Quantity);
        await StockReplayWacCostStateWriter.ApplyAsync(context, permit, ct);
    }

    // If FG was posted at a corrected source value, removing that source after FG
    // would need a separate replay of the physical FG reversal. Keep that order
    // blocked until that wider reversal path is available.
    public static async Task<bool> MustRemoveActiveSourceBeforeRollbackAsync(AppDbContext db,
        string company, string branch, int batchId, CancellationToken ct)
    {
        var document = await db.ProductionFinishedGoodReceiptRows.AsNoTracking().SingleOrDefaultAsync(r =>
            r.BatchId == batchId && r.CompanyCode == company && r.BranchCode == branch, ct);
        if (document?.PostingId is not long ownerId) return false;
        var facts = await db.ProductionFinishedGoodFactRows.AsNoTracking().Where(f =>
            f.BatchId == batchId && f.StockPostingId == ownerId && f.ReversesFactId == null).ToArrayAsync(ct);
        if (facts.Length != 1) return false;
        var movement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(m => m.Uid == facts[0].ProductionMovementId, ct);
        if (!await db.ProductionCostRestatementFacts.AsNoTracking().AnyAsync(r =>
            r.ProductionBalLotMovementId == movement.Uid, ct)) return false;
        var outputs = await db.ProductionBalLotMovements.AsNoTracking().Where(m =>
            m.ProductionBalLotId == movement.ProductionBalLotId
            && m.MovementType == ProductionBalLotMovementTypes.Produce && m.OriginalMovementId == null).ToArrayAsync(ct);
        if (outputs.Length != 1 || outputs[0].BaseQty <= 0m || movement.BaseQty <= 0m
            || movement.BaseQty > outputs[0].BaseQty) return false;
        var output = outputs[0];
        var originalShare = movement.BaseQty == output.BaseQty ? output.TotalCost
            : StockLedgerPrecision.Money(output.TotalCost * movement.BaseQty / output.BaseQty);
        if (originalShare == movement.TotalCost) return false;
        var effective = (await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, [output.Uid], ct))[output.Uid];
        if (effective.RestatementFactId is not long activeId) return false;
        var rootId = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(r => r.Id == activeId).Select(r => r.ReplayStockPostingId).SingleAsync(ct);
        return await db.StockPostings.AsNoTracking().AnyAsync(p => p.Id == rootId
            && p.CompanyCode == company && p.BranchCode == branch && p.SealedAtUtc != null
            && p.SourceModule == "INVENTORY" && p.PostingRole == "PRIMARY"
            && (p.SourceDocumentType == IvTrxTypes.MiscellaneousReceipt || p.SourceDocumentType == IvTrxTypes.GoodsReceive)
            && !db.StockPostings.Any(r => r.ReversesPostingId == p.Id && r.SealedAtUtc != null), ct);
    }

    // Receipt-first reversal restores the original value; every earlier pair must still verify.
    public static async Task<bool> HasClearedCorrectionAsync(AppDbContext db,
        string company, string branch, long movementId, CancellationToken ct)
    {
        var cleared = await ProductionFinishedGoodCorrectionHistory.ReadAsync(
            db, company, branch, movementId, null, null, ct);
        if (cleared is null || cleared.Count == 0) return false;
        var fact = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .SingleAsync(f => f.ProductionMovementId == movementId && f.ReversesFactId == null, ct);
        var movement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == movementId, ct);
        var outputs = await db.ProductionBalLotMovements.AsNoTracking().Where(m =>
            m.ProductionBalLotId == movement.ProductionBalLotId
            && m.MovementType == ProductionBalLotMovementTypes.Produce
            && m.OriginalMovementId == null).Select(m => m.Uid).ToArrayAsync(ct);
        if (outputs.Length != 1) return false;
        await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, outputs, ct);
        if (!await ProductionFinishedGoodCorrectionHistory.MatchesOutputAsync(db, movementId, outputs[0], ct))
            return false;
        var history = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == fact.InventoryHistoryId, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleAsync(p => p.Id == fact.StockPostingId, ct);
        var receipt = await db.StockValuationFacts.AsNoTracking().SingleAsync(v =>
            v.InventoryHistoryId == history.Id && v.StockPostingId == owner.Id
            && v.OriginalValuationFactId == null && v.ReversesValuationFactId == null, ct);
        var effective = (await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, [movementId], ct))[movementId];
        if (effective.RestatementFactId is not null || effective.TotalCost != fact.TotalValue) return false;
        var state = await db.StockCostStates.AsNoTracking().SingleOrDefaultAsync(s =>
            s.CompanyCode == company && s.BranchCode == branch && s.ItemCode == receipt.ItemCode, ct);
        var piles = await db.IvBalLocs.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && b.ICode == receipt.ItemCode).ToArrayAsync(ct);
        if (state is null || state.CostMethod != StockCostMethods.MovingAverage
            || state.OnHandBaseQty != fact.BaseQty || state.InventoryValue != fact.TotalValue
            || state.LastValuationFactId != cleared.LastFactId || state.LastPostingSequence != cleared.LastSequence
            || piles.Length != 1 || piles[0].Id != fact.DestinationBalanceId || piles[0].StdQty != fact.BaseQty)
            return false;
        var key = new StockReplayEventKey(owner.EffectiveAt, StockReplayEventKey.OrdinaryClass,
            owner.PostingSequence, $"H{history.Id:D10}", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db, company, branch, receipt.ItemCode, key, ct);
        return tail.LaterEvents.Count == 0 && tail.CurrentQuantity == state.OnHandBaseQty
            && tail.CurrentValue == state.InventoryValue;
    }

    public static async Task VerifyWithinRootAsync(StockPostingContext context,
        ProductionFinishedGoodCorrectedRollbackPlan plan, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var db = context.Db;
        var reversalHistory = await db.IvTrxHistories.AsNoTracking().SingleAsync(h =>
            h.StockPostingId == context.Posting.Id && h.ReversesHistoryId == plan.HistoryId, ct);
        var facts = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.StockPostingId == context.Posting.Id && f.ItemCode == plan.ItemCode).ToArrayAsync(ct);
        var valueReversals = facts.Where(f => f.ReversesValuationFactId == plan.DeltaFactId).ToArray();
        var receiptReversals = facts.Where(f => f.InventoryHistoryId == reversalHistory.Id
            && f.ReversesValuationFactId == plan.ReceiptFactId).ToArray();
        var state = await db.StockCostStates.AsNoTracking().SingleAsync(s =>
            s.CompanyCode == context.CompanyCode && s.BranchCode == context.BranchCode
            && s.ItemCode == plan.ItemCode, ct);
        var pile = await db.IvBalLocs.AsNoTracking().SingleAsync(b => b.Id == plan.PileId, ct);
        var lot = await db.ProductionBalLots.AsNoTracking().SingleAsync(l => l.Uid == plan.PoolId, ct);
        var pool = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleAsync(p => p.ProductionBalLotId == plan.PoolId, ct);
        var reverseFacts = await db.ProductionFinishedGoodFactRows.AsNoTracking().Where(f =>
            f.StockPostingId == context.Posting.Id && f.BatchId == plan.BatchId
            && f.ReversesFactId != null).ToArrayAsync(ct);
        var reverseMovement = reverseFacts.Length == 1
            ? await db.ProductionBalLotMovements.AsNoTracking().SingleOrDefaultAsync(m =>
                m.Uid == reverseFacts[0].ProductionMovementId, ct) : null;
        if (facts.Length != 2 || valueReversals.Length != 1 || receiptReversals.Length != 1
            || valueReversals[0].OriginalValuationFactId != plan.ReceiptFactId
            || valueReversals[0].BaseQty != 0m || valueReversals[0].SignedValue != plan.OriginalValue - plan.CorrectedValue
            || receiptReversals[0].Direction != -1 || receiptReversals[0].BaseQty != plan.Quantity
            || receiptReversals[0].CostAmount != plan.OriginalValue
            || facts.Any(f => f.CompanyCode != context.CompanyCode || f.BranchCode != context.BranchCode
                || f.LedgerEpochId != context.Epoch.Id || f.ValuationStatus != StockValuationStatuses.Reversed)
            || !await db.StockReplayEffects.AsNoTracking().AnyAsync(e =>
                e.RootStockPostingId == context.Posting.Id && e.BusinessOwnerPostingId == plan.OwnerId
                && e.EffectKind == StockReplayEffectTypes.FinishedGoodReceiptValue
                && e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                && e.AffectedEntityId == valueReversals[0].Id && e.OriginalEntityId == plan.ReceiptFactId, ct)
            || state.OnHandBaseQty != 0m || state.InventoryValue != 0m
            || state.AverageUnitCost != 0m || state.CurrentUnitCost != 0m
            || state.LastValuationFactId != receiptReversals[0].Id
            || state.LastPostingSequence != context.Posting.PostingSequence
            || pile.StdQty != 0m || reverseFacts.Length != 1
            || reverseFacts[0].TotalValue != plan.CorrectedValue || reverseFacts[0].BaseQty != plan.Quantity
            || reverseMovement is null || reverseMovement.OriginalMovementId != plan.MovementId
            || reverseMovement.TotalCost != plan.CorrectedValue || reverseMovement.BaseQty != plan.Quantity
            || lot.BaseQty != plan.RestoredPoolQuantity || lot.TotalCost != plan.RestoredPoolValue
            || pool.Status != ProductionPoolValuationService.Verified
            || pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost)
            throw Invalid("The corrected finished-good rollback did not reconcile Production and Inventory.");
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
