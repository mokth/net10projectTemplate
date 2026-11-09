using System.Globalization;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private static async Task<ActiveFinishedGoodReplayPlan> VerifyActiveFinishedGoodAsync(
        AppDbContext db, string company, string branch, DateTime candidateDate,
        ProductionBalLotMovement produce, ProductionEffectiveCost produceCost,
        ProductionBalLotMovement outbound, string costMethod, CancellationToken ct)
    {
        var effective = (await ProductionReplayEffectiveCostReader.ReadAsync(
            db, company, branch, [outbound.Uid], ct))[outbound.Uid];
        var hasRestatements = await db.ProductionCostRestatementFacts.AsNoTracking().AnyAsync(f =>
            f.ProductionBalLotMovementId == outbound.Uid, ct);
        if (outbound.MovementType != ProductionBalLotMovementTypes.FgReceiptOut
            || outbound.OriginalMovementId is not null || outbound.StockPostingId is not long postId
            || outbound.CompanyCode != company || outbound.BranchCode != branch
            || outbound.LedgerVersion != 2 || outbound.LedgerEpochId != produce.LedgerEpochId
            || outbound.ProductionBalLotId != produce.ProductionBalLotId
            || outbound.WorkOrderId != produce.WorkOrderId
            || outbound.WorkOrderOperationId != produce.WorkOrderOperationId
            || outbound.RouteStepId != produce.RouteStepId || outbound.ItemCode != produce.ItemCode
            || outbound.ConversionFactorToBase != produce.ConversionFactorToBase
            || outbound.ValuationStatus != ProductionPoolValuationService.Verified
            || outbound.BaseQty <= 0m || outbound.BaseQty > produce.BaseQty
            || outbound.Qty <= 0m || outbound.Qty > produce.Qty
            || effective.TotalCost != (outbound.BaseQty == produce.BaseQty ? produceCost.TotalCost
                : StockLedgerPrecision.Money(produceCost.TotalCost * outbound.BaseQty / produce.BaseQty))
            || outbound.UnitCost != StockLedgerPrecision.Money(outbound.TotalCost / outbound.BaseQty)
            || await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(m => m.OriginalMovementId == outbound.Uid, ct)
            || hasRestatements && costMethod != StockCostMethods.MovingAverage)
            throw ProductionReplayError("Finished-good receipt does not match the active Production output.");

        var post = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == postId, ct);
        var producerPost = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == produce.StockPostingId, ct);
        var link = await db.ProductionPostingLinks.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Uid == outbound.PostingLinkId, ct);
        if (post is null || producerPost is null || link is null
            || post.CompanyCode != company || post.BranchCode != branch || post.SealedAtUtc is null
            || producerPost.CompanyCode != company || producerPost.BranchCode != branch || producerPost.SealedAtUtc is null
            || post.LedgerEpochId != produce.LedgerEpochId || producerPost.LedgerEpochId != produce.LedgerEpochId
            || post.SourceModule != "PRODUCTION" || post.SourceDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || post.PostingRole != "PRIMARY" || post.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptPost
            || post.PostingSequence <= producerPost.PostingSequence || post.EffectiveAt != outbound.MovementDate
            || post.EffectiveAt < produce.MovementDate || post.EffectiveAt.Year != candidateDate.Year
            || post.EffectiveAt.Month != candidateDate.Month || post.SourceDocumentNo != outbound.DocumentNo
            || post.ProductionPostingLinkId != link.Uid
            || link.CompanyCode != company || link.BranchCode != branch
            || link.CommandType != ProductionPostingCommandTypes.FinishedGoodReceiptPost
            || link.Status != ProductionPostingLinkStatuses.Succeeded || link.WorkOrderId != produce.WorkOrderId
            || link.DocumentRevision != post.DocumentRevision
            || link.ProductionDocumentType != ProductionDocumentTypes.FinishedGoodReceipt
            || link.ProductionDocumentNo != post.SourceDocumentNo
            || await db.StockPostings.AsNoTracking().AnyAsync(p => p.ReversesPostingId == postId, ct))
            throw ProductionReplayError("Finished-good receipt has no active sealed posting owner.");

        var facts = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .Where(f => f.ProductionMovementId == outbound.Uid).ToArrayAsync(ct);
        if (facts.Length != 1 || facts[0].ReversesFactId is not null)
            throw ProductionReplayError("Finished-good receipt has incomplete original cost evidence.");
        var fact = facts[0];
        var cleared = hasRestatements && effective.RestatementFactId is null
            && await ProductionFinishedGoodCorrectedRollback.HasClearedCorrectionAsync(
                db, company, branch, outbound.Uid, ct);
        if (hasRestatements && effective.RestatementFactId is null && !cleared)
            throw ProductionReplayError("Finished-good correction history has not been fully reversed.");
        var corrected = effective.RestatementFactId is null ? null
            : await ProductionFinishedGoodCorrectedRollback.TryPrepareAsync(
                db, company, branch, fact.BatchId, outbound.MovementDate, ct);
        if (effective.RestatementFactId is not null
            && (corrected is null || corrected.MovementId != outbound.Uid
                || corrected.CorrectedValue != effective.TotalCost))
            throw ProductionReplayError("Finished-good corrected value lacks matching replay evidence.");
        var document = await db.ProductionFinishedGoodReceiptRows.AsNoTracking()
            .Include(r => r.Batch).Include(r => r.Sources)
            .SingleOrDefaultAsync(r => r.BatchId == fact.BatchId, ct);
        var source = document?.Sources.SingleOrDefault(s => s.Id == fact.SourceId);
        var outputLot = await db.ProductionBalLots.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Uid == produce.ProductionBalLotId, ct);
        if (document is null || source is null || document.CompanyCode != company || document.BranchCode != branch
            || outputLot is null || outputLot.CompanyCode != company || outputLot.BranchCode != branch
            || outputLot.Kind != ProductionBalLotKinds.Wip || outputLot.ItemCode != produce.ItemCode
            || outputLot.WorkOrderId != produce.WorkOrderId || outputLot.WorkOrderOperationId != produce.WorkOrderOperationId
            || outputLot.ProducingRouteStepId != produce.RouteStepId || outputLot.Uom != produce.Uom
            || outputLot.BaseUom != produce.BaseUom || outputLot.ConversionFactorToBase != produce.ConversionFactorToBase
            || outputLot.OutputType != PrRouteOutputTypes.FinishedGoods || outputLot.BalanceStage != "FG_STAGING"
            || document.Batch.BatchStatus != IvBatchStatuses.Posted || document.PostingId != postId
            || document.ReversalPostingId is not null || document.DocumentRevision != post.DocumentRevision
            || document.WorkOrderId != produce.WorkOrderId
            || document.Sources.Count(s => s.DocumentRevision == document.DocumentRevision) != 1
            || fact.StockPostingId != postId || fact.BaseQty != outbound.BaseQty
            || fact.TotalValue != outbound.TotalCost || fact.InventoryHistoryId != outbound.InventoryHistoryId
            || source.DocumentRevision != post.DocumentRevision || source.ProductionBalLotId != produce.ProductionBalLotId
            || source.BaseQty != outbound.BaseQty || source.RequestedQty != outbound.Qty
            || source.SourceUom != outbound.Uom || source.BaseUom != outbound.BaseUom
            || source.SourceFactor != outbound.ConversionFactorToBase
            || outbound.SourceLineId != source.Id.ToString(CultureInfo.InvariantCulture)
            || post.SourceDocumentId != fact.BatchId.ToString(CultureInfo.InvariantCulture)
            || link.InventoryBatchNo != document.Batch.BatchNo
            || await db.ProductionFinishedGoodFactRows.AsNoTracking().CountAsync(f =>
                f.BatchId == fact.BatchId && f.StockPostingId == postId && f.ReversesFactId == null, ct) != 1
            || await db.ProductionFinishedGoodFactRows.AsNoTracking().AnyAsync(f => f.ReversesFactId == fact.Id, ct))
            throw ProductionReplayError("Finished-good receipt source or document is inconsistent.");

        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == fact.InventoryHistoryId, ct);
        if (history is null || history.CompanyCode != company || history.BranchCode != branch
            || history.StockPostingId != postId || history.LedgerVersion != 2
            || history.LedgerEpochId != produce.LedgerEpochId || history.EntryRole != "ORIGINAL"
            || history.DocumentRevision != post.DocumentRevision || history.TrxDtTime != post.EffectiveAt
            || history.TrxType != IvTrxTypes.FinishedGoods || history.BatchNo != document.Batch.BatchNo
            || history.ICode != outbound.ItemCode || history.ToBalLocId != fact.DestinationBalanceId
            || history.ToStdQty is not > 0m || history.FrStdQty is > 0m
            || history.EvidenceBaseQty != fact.BaseQty || history.ExactTransferredValue != fact.TotalValue
            || history.ValuationStatus != StockValuationStatuses.Valued
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.ReversesHistoryId == history.Id, ct))
            throw ProductionReplayError("Finished-good receipt inventory history is inconsistent.");

        var valuations = await db.StockValuationFacts.AsNoTracking()
            .Where(v => v.InventoryHistoryId == history.Id).ToArrayAsync(ct);
        if (valuations.Length != 1 || valuations[0].StockPostingId != postId
            || valuations[0].CompanyCode != company || valuations[0].BranchCode != branch
            || valuations[0].LedgerEpochId != produce.LedgerEpochId || valuations[0].ItemCode != outbound.ItemCode
            || valuations[0].Direction != 1 || valuations[0].BaseQty != history.ToStdQty
            || valuations[0].CostAmount != fact.TotalValue || valuations[0].BaseCostAmount != fact.TotalValue
            || valuations[0].CostMethod != costMethod
            || valuations[0].ValuationStatus != StockValuationStatuses.Valued
            || valuations[0].ReversesValuationFactId is not null || valuations[0].OriginalValuationFactId is not null
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(v =>
                v.ReversesValuationFactId == valuations[0].Id
                || v.OriginalValuationFactId == valuations[0].Id
                    && !hasRestatements, ct))
            throw ProductionReplayError("Finished-good receipt inventory value is not an unchanged original.");

        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .SingleOrDefaultAsync(e => e.MovementId == outbound.Uid, ct);
        var poolGeneration = await db.ProductionPoolValuationRows.AsNoTracking()
            .Where(p => p.ProductionBalLotId == produce.ProductionBalLotId)
            .Select(p => (int?)p.Generation).SingleOrDefaultAsync(ct);
        if (evidence is null || poolGeneration is null || evidence.ProductionBalLotId != produce.ProductionBalLotId
            || evidence.Generation != poolGeneration || evidence.Status != ProductionPoolValuationService.Verified
            || evidence.Basis != "POOLED_AVERAGE" || evidence.Currency != "COMPANY_BASE"
            || evidence.PriceUom != outbound.BaseUom || evidence.Price != outbound.UnitCost
            || evidence.ConversionFactor != outbound.ConversionFactorToBase
            || evidence.InventoryHistoryId != history.Id || evidence.OriginalMovementId is not null)
            throw ProductionReplayError("Finished-good receipt production valuation evidence is inconsistent.");

        var allocations = await db.ProductionMovementAllocations.AsNoTracking()
            .Where(a => a.OutboundMovementId == outbound.Uid).ToArrayAsync(ct);
        var edges = await db.ProductionPoolDependencyRows.AsNoTracking()
            .Where(e => e.ConsumerMovementId == outbound.Uid).ToArrayAsync(ct);
        if (allocations.Length != 1 || allocations[0].CompanyCode != company || allocations[0].BranchCode != branch
            || allocations[0].StockPostingId != postId || allocations[0].ReceiptMovementId != produce.Uid
            || allocations[0].BaseQty != outbound.BaseQty || allocations[0].ReversesAllocationId is not null
            || edges.Length != 1 || edges[0].StockPostingId != postId
            || edges[0].ContributorMovementId != produce.Uid || edges[0].ReversesDependencyId is not null
            || await db.ProductionMovementAllocations.AsNoTracking().AnyAsync(a =>
                a.ReversesAllocationId == allocations[0].Id, ct)
            || await db.ProductionPoolDependencyRows.AsNoTracking().AnyAsync(e =>
                e.ReversesDependencyId == edges[0].Id, ct))
            throw ProductionReplayError("Finished-good receipt production dependencies are inconsistent.");

        var hash = StockPostingFingerprint.Create(new
        {
            outbound.Uid, postId, post.PostingSequence, post.EffectiveAt, post.DocumentRevision,
            outbound.Qty, outbound.BaseQty, outbound.TotalCost, outbound.UnitCost, Effective = effective,
            fact.Id, fact.BatchId, fact.SourceId, fact.InventoryHistoryId, fact.DestinationBalanceId,
            ValuationId = valuations[0].Id, AllocationId = allocations[0].Id, EdgeId = edges[0].Id,
            evidence.Generation
        }, new { }).RequestFingerprint;
        return new(fact.BatchId, outbound.Uid, outbound.Qty, outbound.BaseQty, effective.TotalCost,
            effective, post.EffectiveAt, hash);
    }
}
