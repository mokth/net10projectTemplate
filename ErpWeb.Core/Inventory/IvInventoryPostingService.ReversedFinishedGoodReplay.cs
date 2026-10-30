using System.Globalization;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // A reversed FG receipt is inert only when its complete Production and Inventory
    // evidence forms one exact, sealed pair. Active or ambiguous descendants stay blocked.
    private sealed record ReversedFinishedGoodReplayPlan(string Hash, CorrectedReversedFinishedGoodPlan? Corrected);

    private static async Task<ReversedFinishedGoodReplayPlan> VerifyReversedFinishedGoodAsync(
        AppDbContext db, string company, string branch, DateTime candidateDate,
        ProductionBalLotMovement produce, ProductionBalLotMovement[] movements,
        long? removingReceiptOwnerId, CancellationToken ct)
    {
        if (movements.Length != 2
            || movements.Count(m => m.MovementType == ProductionBalLotMovementTypes.FgReceiptOut) != 1
            || movements.Count(m => m.MovementType == ProductionBalLotMovementTypes.FgReceiptReversal) != 1)
            throw ProductionReplayError("Production output has active or unsupported finished-good history.");
        var outbound = movements.Single(m => m.MovementType == ProductionBalLotMovementTypes.FgReceiptOut);
        var reversal = movements.Single(m => m.MovementType == ProductionBalLotMovementTypes.FgReceiptReversal);
        var corrected = await PrepareCorrectedReversedFinishedGoodAsync(db, company, branch,
            produce, outbound, reversal, removingReceiptOwnerId, ct);
        if (outbound.OriginalMovementId is not null || reversal.OriginalMovementId != outbound.Uid
            || outbound.StockPostingId is not long postId || reversal.StockPostingId is not long reverseId
            || postId == reverseId || outbound.CompanyCode != company || outbound.BranchCode != branch
            || reversal.CompanyCode != company || reversal.BranchCode != branch
            || outbound.LedgerVersion != 2 || reversal.LedgerVersion != 2
            || outbound.LedgerEpochId != produce.LedgerEpochId || reversal.LedgerEpochId != produce.LedgerEpochId
            || outbound.ProductionBalLotId != produce.ProductionBalLotId
            || reversal.ProductionBalLotId != produce.ProductionBalLotId
            || outbound.WorkOrderId != produce.WorkOrderId || reversal.WorkOrderId != produce.WorkOrderId
            || outbound.WorkOrderOperationId != produce.WorkOrderOperationId
            || reversal.WorkOrderOperationId != produce.WorkOrderOperationId
            || outbound.RouteStepId != produce.RouteStepId || reversal.RouteStepId != produce.RouteStepId
            || outbound.ItemCode != produce.ItemCode || reversal.ItemCode != produce.ItemCode
            || outbound.ConversionFactorToBase != produce.ConversionFactorToBase
            || reversal.ConversionFactorToBase != outbound.ConversionFactorToBase
            || outbound.ValuationStatus != ProductionPoolValuationService.Verified
            || reversal.ValuationStatus != ProductionPoolValuationService.Verified
            || outbound.BaseQty <= 0m || outbound.BaseQty > produce.BaseQty
            || reversal.BaseQty != outbound.BaseQty || reversal.Qty != outbound.Qty
            || reversal.Uom != outbound.Uom || reversal.BaseUom != outbound.BaseUom
            || reversal.TotalCost != (corrected?.CurrentValue ?? outbound.TotalCost)
            || reversal.UnitCost != (corrected?.CurrentUnitCost ?? outbound.UnitCost)
            || await db.ProductionBalLotMovements.AsNoTracking().CountAsync(m =>
                m.OriginalMovementId == outbound.Uid, ct) != 1
            || corrected is null && await db.ProductionCostRestatementFacts.AsNoTracking().AnyAsync(f =>
                f.ProductionBalLotMovementId == outbound.Uid || f.ProductionBalLotMovementId == reversal.Uid, ct))
            throw ProductionReplayError("Finished-good production movements are not an exact reversal pair.");

        var posts = await db.StockPostings.AsNoTracking().Where(p => p.Id == postId || p.Id == reverseId)
            .ToDictionaryAsync(p => p.Id, ct);
        var producerPost = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == produce.StockPostingId, ct);
        if (!posts.TryGetValue(postId, out var post) || !posts.TryGetValue(reverseId, out var reverse)
            || producerPost is null || producerPost.CompanyCode != company || producerPost.BranchCode != branch
            || producerPost.SealedAtUtc is null || producerPost.LedgerEpochId != produce.LedgerEpochId
            || post.CompanyCode != company || post.BranchCode != branch || post.SealedAtUtc is null
            || reverse.CompanyCode != company || reverse.BranchCode != branch || reverse.SealedAtUtc is null
            || post.LedgerEpochId != produce.LedgerEpochId || reverse.LedgerEpochId != produce.LedgerEpochId
            || post.SourceModule != "PRODUCTION" || reverse.SourceModule != "PRODUCTION"
            || post.SourceDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || reverse.SourceDocumentType != post.SourceDocumentType
            || post.SourceDocumentId != reverse.SourceDocumentId || post.SourceDocumentNo != reverse.SourceDocumentNo
            || outbound.DocumentNo != post.SourceDocumentNo || reversal.DocumentNo != post.SourceDocumentNo
            || post.DocumentRevision != reverse.DocumentRevision || post.PostingRole != "PRIMARY"
            || post.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptPost
            || reverse.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptRollback
            || reverse.PostingRole != "REVERSAL" || reverse.ReversesPostingId != postId
            || post.PostingSequence <= producerPost.PostingSequence || reverse.PostingSequence <= post.PostingSequence
            || outbound.MovementDate != post.EffectiveAt || reversal.MovementDate != reverse.EffectiveAt
            || post.EffectiveAt < produce.MovementDate || reverse.EffectiveAt.Date < post.EffectiveAt.Date
            || post.EffectiveAt.Year != candidateDate.Year || post.EffectiveAt.Month != candidateDate.Month
            || reverse.EffectiveAt.Year != candidateDate.Year || reverse.EffectiveAt.Month != candidateDate.Month
            || post.ProductionPostingLinkId != outbound.PostingLinkId
            || reverse.ProductionPostingLinkId != reversal.PostingLinkId
            || await db.StockPostings.AsNoTracking().CountAsync(p => p.ReversesPostingId == postId, ct) != 1)
            throw ProductionReplayError("Finished-good postings are not an exact sealed reversal pair.");

        var links = await db.ProductionPostingLinks.AsNoTracking()
            .Where(l => l.Uid == outbound.PostingLinkId || l.Uid == reversal.PostingLinkId)
            .ToDictionaryAsync(l => l.Uid, ct);
        if (!links.TryGetValue(outbound.PostingLinkId, out var originalLink)
            || !links.TryGetValue(reversal.PostingLinkId, out var reversalLink)
            || originalLink.CompanyCode != company || originalLink.BranchCode != branch
            || reversalLink.CompanyCode != company || reversalLink.BranchCode != branch
            || originalLink.Status != ProductionPostingLinkStatuses.Succeeded
            || reversalLink.Status != ProductionPostingLinkStatuses.Succeeded
            || originalLink.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptPost
            || reversalLink.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptRollback
            || reversalLink.OriginalPostingLinkId != originalLink.Uid
            || originalLink.WorkOrderId != produce.WorkOrderId || reversalLink.WorkOrderId != produce.WorkOrderId
            || originalLink.ProductionDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || reversalLink.ProductionDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || originalLink.ProductionDocumentNo != post.SourceDocumentNo
            || reversalLink.ProductionDocumentNo != post.SourceDocumentNo
            || originalLink.DocumentRevision != post.DocumentRevision || reversalLink.DocumentRevision != post.DocumentRevision)
            throw ProductionReplayError("Finished-good reversal has inconsistent posting links.");

        var facts = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .Where(f => f.ProductionMovementId == outbound.Uid || f.ProductionMovementId == reversal.Uid)
            .ToArrayAsync(ct);
        if (facts.Length != 2 || facts.Count(f => f.ProductionMovementId == outbound.Uid) != 1
            || facts.Count(f => f.ProductionMovementId == reversal.Uid) != 1)
            throw ProductionReplayError("Finished-good reversal has incomplete source facts.");
        var fact = facts.Single(f => f.ProductionMovementId == outbound.Uid);
        var reverseFact = facts.Single(f => f.ProductionMovementId == reversal.Uid);
        if (fact.ReversesFactId is not null || reverseFact.ReversesFactId != fact.Id
            || fact.StockPostingId != postId || reverseFact.StockPostingId != reverseId
            || fact.BatchId != reverseFact.BatchId || fact.SourceId != reverseFact.SourceId
            || fact.DestinationBalanceId != reverseFact.DestinationBalanceId
            || fact.BaseQty != outbound.BaseQty || reverseFact.BaseQty != outbound.BaseQty
            || fact.TotalValue != outbound.TotalCost || reverseFact.TotalValue != (corrected?.CurrentValue ?? outbound.TotalCost)
            || fact.InventoryHistoryId != outbound.InventoryHistoryId
            || reverseFact.InventoryHistoryId != reversal.InventoryHistoryId
            || post.SourceDocumentId != fact.BatchId.ToString(CultureInfo.InvariantCulture)
            || outbound.SourceLineId != fact.SourceId.ToString(CultureInfo.InvariantCulture)
            || reversal.SourceLineId != fact.SourceId.ToString(CultureInfo.InvariantCulture)
            || await db.ProductionFinishedGoodFactRows.AsNoTracking().CountAsync(f => f.ReversesFactId == fact.Id, ct) != 1)
            throw ProductionReplayError("Finished-good source facts do not form an exact reversal pair.");

        var source = await db.ProductionFinishedGoodSourceRows.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == fact.SourceId, ct);
        if (source is null || source.BatchId != fact.BatchId || source.DocumentRevision != post.DocumentRevision
            || source.ProductionBalLotId != produce.ProductionBalLotId || source.BaseQty != fact.BaseQty
            || source.RequestedQty != outbound.Qty || source.SourceUom != outbound.Uom
            || source.BaseUom != outbound.BaseUom || source.SourceFactor != outbound.ConversionFactorToBase)
            throw ProductionReplayError("Finished-good source does not match its original production movement.");

        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.Id == fact.InventoryHistoryId || h.Id == reverseFact.InventoryHistoryId)
            .ToDictionaryAsync(h => h.Id, ct);
        if (!histories.TryGetValue(fact.InventoryHistoryId, out var history)
            || !histories.TryGetValue(reverseFact.InventoryHistoryId, out var reversedHistory)
            || history.CompanyCode != company || history.BranchCode != branch
            || reversedHistory.CompanyCode != company || reversedHistory.BranchCode != branch
            || history.LedgerVersion != 2 || reversedHistory.LedgerVersion != 2
            || history.LedgerEpochId != produce.LedgerEpochId || reversedHistory.LedgerEpochId != produce.LedgerEpochId
            || history.StockPostingId != postId || reversedHistory.StockPostingId != reverseId
            || history.ReversesHistoryId is not null || reversedHistory.ReversesHistoryId != history.Id
            || history.EntryRole != "ORIGINAL" || reversedHistory.EntryRole != "REVERSAL"
            || history.TrxDtTime != post.EffectiveAt || reversedHistory.TrxDtTime != reverse.EffectiveAt
            || originalLink.InventoryBatchNo != history.BatchNo || reversalLink.InventoryBatchNo != history.BatchNo
            || reversedHistory.BatchNo != history.BatchNo || history.DocumentRevision != post.DocumentRevision
            || reversedHistory.DocumentRevision != post.DocumentRevision
            || history.TrxType != IvTrxTypes.FinishedGoods || reversedHistory.TrxType != history.TrxType
            || history.ToBalLocId != fact.DestinationBalanceId || reversedHistory.FromBalLocId != fact.DestinationBalanceId
            || history.ICode != outbound.ItemCode || reversedHistory.ICode != outbound.ItemCode
            || history.EvidenceBaseQty != fact.BaseQty || reversedHistory.EvidenceBaseQty != fact.BaseQty
            || history.ExactTransferredValue != fact.TotalValue || reversedHistory.ExactTransferredValue != fact.TotalValue
            || await db.IvTrxHistories.AsNoTracking().CountAsync(h => h.ReversesHistoryId == history.Id, ct) != 1)
            throw ProductionReplayError("Finished-good inventory histories do not form an exact reversal pair.");

        var valuations = await db.StockValuationFacts.AsNoTracking()
            .Where(v => v.InventoryHistoryId == history.Id || v.InventoryHistoryId == reversedHistory.Id)
            .ToArrayAsync(ct);
        if (valuations.Length != 2
            || valuations.Count(v => v.InventoryHistoryId == history.Id && v.StockPostingId == postId
                && v.ReversesValuationFactId is null && v.OriginalValuationFactId is null
                && v.CompanyCode == company && v.BranchCode == branch
                && v.LedgerEpochId == produce.LedgerEpochId && v.ItemCode == outbound.ItemCode
                && v.Direction == 1 && v.BaseQty == fact.BaseQty && v.CostAmount == fact.TotalValue
                && v.BaseCostAmount == fact.TotalValue
                && v.ValuationStatus == StockValuationStatuses.Valued) != 1)
            throw ProductionReplayError("Finished-good inventory valuation is incomplete.");
        var valuation = valuations.Single(v => v.InventoryHistoryId == history.Id);
        if (valuations.Count(v => v.InventoryHistoryId == reversedHistory.Id && v.StockPostingId == reverseId
                && v.ReversesValuationFactId == valuation.Id && v.CompanyCode == company && v.BranchCode == branch
                && v.LedgerEpochId == produce.LedgerEpochId && v.ItemCode == outbound.ItemCode
                && v.Direction == -1 && v.BaseQty == valuation.BaseQty && v.CostAmount == valuation.CostAmount
                && v.BaseCostAmount == valuation.BaseCostAmount
                && v.CostMethod == valuation.CostMethod && v.ValuationStatus == StockValuationStatuses.Reversed) != 1
            || await db.StockValuationFacts.AsNoTracking().CountAsync(v => v.ReversesValuationFactId == valuation.Id, ct) != 1
            || corrected is null && await db.StockValuationFacts.AsNoTracking().AnyAsync(v =>
                v.OriginalValuationFactId == valuation.Id && v.ReversesValuationFactId == null, ct))
            throw ProductionReplayError("Finished-good inventory valuation reversal is inconsistent.");

        long? closedFifoLayerId = null;
        if (valuation.CostMethod == StockCostMethods.Fifo)
        {
            var layers = await db.StockFifoLayers.AsNoTracking()
                .Where(l => l.OriginValuationFactId == valuation.Id).ToArrayAsync(ct);
            if (layers.Length != 1 || layers[0].CompanyCode != company || layers[0].BranchCode != branch
                || layers[0].ItemCode != outbound.ItemCode || layers[0].OriginStockPostingId != postId
                || layers[0].OriginalQty != valuation.BaseQty || layers[0].OriginalValue != valuation.CostAmount
                || layers[0].RemainingQty != 0m || layers[0].RemainingValue != 0m
                || layers[0].AccumulatedAdjustment != 0m || layers[0].CurrentUnitCost != 0m
                || layers[0].Status != StockFifoLayerStatuses.Closed
                || await db.StockFifoLayerConsumptions.AsNoTracking().AnyAsync(c => c.FifoLayerId == layers[0].Id, ct))
                throw ProductionReplayError("Reversed finished goods have an inconsistent FIFO layer.");
            closedFifoLayerId = layers[0].Id;
        }

        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(e => e.MovementId == outbound.Uid || e.MovementId == reversal.Uid).ToArrayAsync(ct);
        var poolGeneration = await db.ProductionPoolValuationRows.AsNoTracking()
            .Where(p => p.ProductionBalLotId == produce.ProductionBalLotId)
            .Select(p => (int?)p.Generation).SingleOrDefaultAsync(ct);
        if (evidence.Length != 2 || movements.Any(m => evidence.Count(e =>
                e.MovementId == m.Uid && e.ProductionBalLotId == produce.ProductionBalLotId
                && e.Generation > 0 && e.Status == ProductionPoolValuationService.Verified
                && e.Currency == "COMPANY_BASE" && e.Price == m.UnitCost
                && e.PriceUom == m.BaseUom && e.ConversionFactor > 0m
                && e.ConversionFactor == m.ConversionFactorToBase
                && e.Basis == (m.Uid == outbound.Uid ? "POOLED_AVERAGE" : "REVERSAL")
                && e.InventoryHistoryId == m.InventoryHistoryId
                && e.OriginalMovementId == m.OriginalMovementId) != 1)
            || poolGeneration is null || evidence[0].Generation != poolGeneration
            || evidence[0].Generation != evidence[1].Generation)
            throw ProductionReplayError("Finished-good production valuation evidence is inconsistent.");

        var allocations = await db.ProductionMovementAllocations.AsNoTracking()
            .Where(a => a.OutboundMovementId == outbound.Uid || a.OutboundMovementId == reversal.Uid)
            .ToArrayAsync(ct);
        var edges = await db.ProductionPoolDependencyRows.AsNoTracking()
            .Where(e => e.ConsumerMovementId == outbound.Uid || e.ConsumerMovementId == reversal.Uid)
            .ToArrayAsync(ct);
        if (allocations.Length != 2 || edges.Length != 2
            || allocations.Count(a => a.OutboundMovementId == outbound.Uid && a.ReversesAllocationId is null
                && a.ReceiptMovementId == produce.Uid && a.StockPostingId == postId
                && a.CompanyCode == company && a.BranchCode == branch && a.BaseQty == outbound.BaseQty) != 1
            || edges.Count(e => e.ConsumerMovementId == outbound.Uid && e.ReversesDependencyId is null
                && e.ContributorMovementId == produce.Uid && e.StockPostingId == postId) != 1)
            throw ProductionReplayError("Finished-good allocation or dependency is incomplete.");
        var allocation = allocations.Single(a => a.OutboundMovementId == outbound.Uid);
        var edge = edges.Single(e => e.ConsumerMovementId == outbound.Uid);
        if (allocations.Count(a => a.OutboundMovementId == reversal.Uid && a.ReversesAllocationId == allocation.Id
                && a.ReceiptMovementId == produce.Uid && a.StockPostingId == reverseId
                && a.CompanyCode == company && a.BranchCode == branch && a.BaseQty == allocation.BaseQty) != 1
            || edges.Count(e => e.ConsumerMovementId == reversal.Uid && e.ReversesDependencyId == edge.Id
                && e.ContributorMovementId == produce.Uid && e.StockPostingId == reverseId) != 1
            || await db.ProductionMovementAllocations.AsNoTracking().CountAsync(a => a.ReversesAllocationId == allocation.Id, ct) != 1
            || await db.ProductionPoolDependencyRows.AsNoTracking().CountAsync(e => e.ReversesDependencyId == edge.Id, ct) != 1)
            throw ProductionReplayError("Finished-good allocation or dependency reversal is inconsistent.");

        var hash = StockPostingFingerprint.Create(new
        {
            outbound.Uid, ReversalId = reversal.Uid, postId, reverseId, post.PostingSequence,
            ReverseSequence = reverse.PostingSequence, post.EffectiveAt, ReverseAt = reverse.EffectiveAt,
            outbound.BaseQty, outbound.Qty, outbound.TotalCost, outbound.UnitCost,
            FactId = fact.Id, ReversalFactId = reverseFact.Id, fact.InventoryHistoryId,
            ReverseHistoryId = reverseFact.InventoryHistoryId, fact.SourceId, fact.BatchId,
            fact.DestinationBalanceId, AllocationIds = allocations.Select(a => a.Id).Order().ToArray(),
            EdgeIds = edges.Select(e => e.Id).Order().ToArray(),
            ValuationIds = valuations.Select(v => v.Id).Order().ToArray(), closedFifoLayerId, evidence[0].Generation,
            CorrectedHash = corrected?.EvidenceHash
        }, new { }).RequestFingerprint;
        return new(hash, corrected);
    }
}
