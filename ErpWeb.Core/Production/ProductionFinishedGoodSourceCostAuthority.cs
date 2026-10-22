using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionFinishedGoodSourceCostProjection(
    IReadOnlyList<long> SourceMovementIds,
    long FinishedGoodMovementId,
    long FinishedGoodFactId,
    int InventoryHistoryId,
    long OriginalInventoryValuationFactId,
    decimal CorrectedSourceValue,
    decimal CorrectedFinishedGoodValue,
    decimal ProjectedRemainingPoolValue);

/// <summary>
/// Reads the corrected value of one FG receipt from verified WIP Produce
/// contributions, preserving independent later Produce receipts. Other
/// source-pool movements and multi-line receipts are
/// excluded until the full pool chronology can be replayed.
/// </summary>
internal static class ProductionFinishedGoodSourceCostAuthority
{
    public static async Task<ProductionFinishedGoodSourceCostProjection> ReadWithinRootAsync(
        StockPostingContext context, int receiptBatchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null || receiptBatchId <= 0)
            throw Invalid("FG source cost authority requires a replay transaction and receipt ID.");
        var document = await db.ProductionFinishedGoodReceiptRows.AsNoTracking()
            .Include(x => x.Batch).Include(x => x.Sources)
            .SingleOrDefaultAsync(x => x.BatchId == receiptBatchId
                && x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode, cancellationToken);
        if (document is null || document.Batch.BatchStatus != IvBatchStatuses.Posted
            || document.ReversalPostingId is not null
            || document.PostingId is not long receiptPostingId)
            throw Invalid("FG source cost authority needs an active posted receipt.");
        var sources = document.Sources.Where(x =>
            x.DocumentRevision == document.DocumentRevision).ToArray();
        var facts = await db.ProductionFinishedGoodFactRows.AsNoTracking()
            .Where(x => x.BatchId == receiptBatchId
                && x.StockPostingId == receiptPostingId && x.ReversesFactId == null)
            .ToArrayAsync(cancellationToken);
        if (sources.Length != 1 || facts.Length != 1
            || facts[0].SourceId != sources[0].Id)
            throw Invalid("FG source cost authority supports one active receipt line.");
        var source = sources[0];
        var fact = facts[0];
        var outbound = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == fact.ProductionMovementId,
                cancellationToken);
        var pool = await db.ProductionBalLots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == source.ProductionBalLotId,
                cancellationToken);
        var poolValuation = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == source.ProductionBalLotId,
                cancellationToken);
        var receiptOwner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == receiptPostingId, cancellationToken);
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == fact.InventoryHistoryId,
                cancellationToken);
        if (outbound is null || pool is null || poolValuation is null
            || receiptOwner is null || history is null
            || pool.CompanyCode != context.CompanyCode
            || pool.BranchCode != context.BranchCode
            || pool.Kind != ProductionBalLotKinds.Wip
            || pool.WorkOrderId != document.WorkOrderId
            || poolValuation.Status != ProductionPoolValuationService.Verified
            || poolValuation.Generation <= 0
            || poolValuation.TrackedBaseQty != pool.BaseQty
            || poolValuation.TrackedValue != pool.TotalCost
            || outbound.ProductionBalLotId != pool.Uid
            || outbound.MovementType != ProductionBalLotMovementTypes.FgReceiptOut
            || outbound.OriginalMovementId is not null
            || outbound.StockPostingId != receiptOwner.Id
            || outbound.LedgerVersion != 2
            || outbound.LedgerEpochId != context.Epoch.Id
            || outbound.ValuationStatus != ProductionPoolValuationService.Verified
            || outbound.WorkOrderId != document.WorkOrderId
            || outbound.ItemCode != pool.ItemCode
            || outbound.BaseUom != pool.BaseUom
            || outbound.MovementDate != receiptOwner.EffectiveAt
            || outbound.BaseQty != source.BaseQty
            || outbound.BaseQty != fact.BaseQty
            || outbound.TotalCost != fact.TotalValue
            || outbound.InventoryHistoryId != fact.InventoryHistoryId
            || receiptOwner.CompanyCode != context.CompanyCode
            || receiptOwner.BranchCode != context.BranchCode
            || receiptOwner.LedgerEpochId != context.Epoch.Id
            || receiptOwner.PostingRole != "PRIMARY"
            || receiptOwner.SealedAtUtc is null
            || history.CompanyCode != context.CompanyCode
            || history.BranchCode != context.BranchCode
            || history.ICode != pool.ItemCode
            || history.StockPostingId != receiptOwner.Id
            || history.LedgerVersion != 2
            || history.EntryRole != "ORIGINAL"
            || history.TrxType != IvTrxTypes.FinishedGoods
            || history.ValuationStatus != StockValuationStatuses.Valued
            || history.ExactTransferredValue != fact.TotalValue
            || history.EvidenceBaseQty != fact.BaseQty
            || history.ToBalLocId != fact.DestinationBalanceId
            || history.ToStdQty is not > 0m
            || history.FrStdQty is > 0m
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(x =>
                x.ReversesHistoryId == fact.InventoryHistoryId, cancellationToken)
            || await db.ProductionFinishedGoodFactRows.AsNoTracking().AnyAsync(x =>
                x.ReversesFactId == fact.Id, cancellationToken)
            || await db.StockPostings.AsNoTracking().AnyAsync(x =>
                x.ReversesPostingId == receiptOwner.Id
                && x.SealedAtUtc != null, cancellationToken))
            throw Invalid("FG receipt has incomplete Production or Inventory identity.");

        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => x.ProductionBalLotId == pool.Uid)
            .ToArrayAsync(cancellationToken);
        var allInbounds = movements.Where(x => x.Uid != outbound.Uid
            && x.MovementType == ProductionBalLotMovementTypes.Produce).ToArray();
        var inboundPostingIds = allInbounds.Where(x => x.StockPostingId.HasValue)
            .Select(x => x.StockPostingId!.Value).Distinct().ToArray();
        var inboundOwners = (await db.StockPostings.AsNoTracking()
            .Where(x => inboundPostingIds.Contains(x.Id))
            .ToArrayAsync(cancellationToken)).ToDictionary(x => x.Id);
        var inbounds = allInbounds.Where(x => x.StockPostingId is long id
            && inboundOwners.TryGetValue(id, out var owner)
            && owner.PostingSequence < receiptOwner.PostingSequence).ToArray();
        var laterInbounds = allInbounds.Where(x => x.StockPostingId is long id
            && inboundOwners.TryGetValue(id, out var owner)
            && owner.PostingSequence > receiptOwner.PostingSequence).ToArray();
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(x => x.ProductionBalLotId == pool.Uid)
            .ToArrayAsync(cancellationToken);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(x => x.OutboundMovementId == outbound.Uid)
            .ToArrayAsync(cancellationToken);
        var dependencies = await ProductionActiveEdgeResolver.Dependencies(db).AsNoTracking()
            .Where(x => x.ConsumerMovementId == outbound.Uid)
            .ToArrayAsync(cancellationToken);
        if (inbounds.Length == 0 || movements.Length != allInbounds.Length + 1
            || allInbounds.Length != inbounds.Length + laterInbounds.Length
            || allInbounds.Any(x => x.StockPostingId is not long postingId
                || !inboundOwners.TryGetValue(postingId, out var owner)
                || x.ProductionBalLotId != pool.Uid
                || x.OriginalMovementId is not null
                || x.LedgerVersion != 2
                || x.LedgerEpochId != context.Epoch.Id
                || x.ValuationStatus != ProductionPoolValuationService.Verified
                || x.WorkOrderId != document.WorkOrderId
                || x.ItemCode != pool.ItemCode || x.BaseUom != pool.BaseUom
                || x.BaseQty <= 0m || x.TotalCost < 0m
                || x.MovementDate != owner.EffectiveAt
                || owner.CompanyCode != context.CompanyCode
                || owner.BranchCode != context.BranchCode
                || owner.LedgerEpochId != context.Epoch.Id
                || owner.PostingRole != "PRIMARY"
                || owner.SealedAtUtc is null
                || owner.PostingSequence == receiptOwner.PostingSequence
                || owner.PostingSequence < receiptOwner.PostingSequence
                    && x.MovementDate > outbound.MovementDate
                || owner.PostingSequence > receiptOwner.PostingSequence
                    && x.MovementDate < outbound.MovementDate)
            || evidence.Length != movements.Length
            || evidence.Any(x => x.Generation != poolValuation.Generation
                || x.Status != ProductionPoolValuationService.Verified)
            || allInbounds.Any(x => evidence.Count(e => e.MovementId == x.Uid) != 1)
            || evidence.Count(x => x.MovementId == outbound.Uid) != 1
            || dependencies.Length != inbounds.Length
            || dependencies.Any(x => x.StockPostingId != receiptOwner.Id
                || !inbounds.Any(m => m.Uid == x.ContributorMovementId))
            || dependencies.Select(x => x.ContributorMovementId).Distinct().Count()
                != inbounds.Length
            || pool.BaseQty != allInbounds.Sum(x => x.BaseQty) - outbound.BaseQty)
            throw Invalid("FG receipt does not have isolated exact WIP contributions.");
        var expectedAllocations = new ProductionContributionAllocator().BuildPlan(
            inbounds.Select(x => new ProductionContribution(pool.Uid, x.Uid,
                x.MovementDate, inboundOwners[x.StockPostingId!.Value].PostingSequence,
                x.PostingLineNo ?? 0, x.BaseQty)).ToArray(),
            [new(outbound.Uid.ToString(), outbound.BaseQty)]);
        if (allocations.Length != expectedAllocations.Count
            || allocations.Select(x => x.ReceiptMovementId).Distinct().Count()
                != allocations.Length
            || allocations.Any(x => x.CompanyCode != context.CompanyCode
                || x.BranchCode != context.BranchCode
                || x.StockPostingId != receiptOwner.Id
                || expectedAllocations.Count(a => a.ReceiptMovementId == x.ReceiptMovementId
                    && a.BaseQty == x.BaseQty) != 1)
            || await db.StockPostings.AsNoTracking().AnyAsync(x =>
                inboundPostingIds.Contains(x.ReversesPostingId ?? 0)
                && x.SealedAtUtc != null, cancellationToken))
            throw Invalid("FG receipt has incomplete WIP allocation or reversed source.");

        var receiptFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode,
            [history], cancellationToken))[history.Id];
        var inventoryReceipt = receiptFacts.SingleOrDefault();
        if (inventoryReceipt is null
            || inventoryReceipt.StockPostingId != receiptOwner.Id
            || inventoryReceipt.Direction != 1
            || inventoryReceipt.CostMethod != StockCostMethods.MovingAverage
            || inventoryReceipt.ValuationStatus != StockValuationStatuses.Valued
            || inventoryReceipt.ReversesValuationFactId is not null
            || inventoryReceipt.BaseQty != history.ToStdQty
            || inventoryReceipt.CostAmount != fact.TotalValue
            || inventoryReceipt.BaseCostAmount != inventoryReceipt.CostAmount)
            throw Invalid("FG Inventory receipt lacks exact original WAC valuation.");

        var effective = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [..allInbounds.Select(x => x.Uid), outbound.Uid], cancellationToken);
        foreach (var inbound in inbounds)
        {
            if (effective[inbound.Uid].RestatementFactId is long inboundCorrectionId)
            {
                var correction = await db.ProductionCostRestatementFacts.AsNoTracking()
                    .SingleAsync(x => x.Id == inboundCorrectionId, cancellationToken);
                if (correction.ReplayStockPostingId != context.Posting.Id)
                    throw Invalid("FG source cost was corrected by a different replay root.");
            }
        }
        if (laterInbounds.Any(x => effective[x.Uid].RestatementFactId is not null))
            throw Invalid("FG has a corrected later WIP receipt outside this bounded sequence.");
        var originalSource = StockLedgerPrecision.Money(inbounds.Sum(x => x.TotalCost));
        var correctedSource = StockLedgerPrecision.Money(
            inbounds.Sum(x => effective[x.Uid].TotalCost));
        var sourceQty = inbounds.Sum(x => x.BaseQty);
        var correctedFg = FinishedGoodReceiptMath.AllocateValue(sourceQty,
            correctedSource, [(source.Id, source.BaseQty)])[source.Id];
        var laterValue = StockLedgerPrecision.Money(
            laterInbounds.Sum(x => x.TotalCost));
        var remaining = StockLedgerPrecision.Money(
            correctedSource - correctedFg + laterValue);
        if (StockLedgerPrecision.Money(FinishedGoodReceiptMath.AllocateValue(
                sourceQty, originalSource,
                [(source.Id, source.BaseQty)])[source.Id]) != fact.TotalValue
            || remaining < 0m)
            throw Invalid("FG original or corrected source allocation is inconsistent.");
        if (effective[outbound.Uid].RestatementFactId is long outboundCorrectionId)
        {
            var correction = await db.ProductionCostRestatementFacts.AsNoTracking()
                .SingleAsync(x => x.Id == outboundCorrectionId, cancellationToken);
            if (correction.ReplayStockPostingId != context.Posting.Id
                || effective[outbound.Uid].TotalCost != correctedFg
                || pool.TotalCost != remaining)
                throw Invalid("FG receipt has a different active correction or pool value.");
        }
        else if (effective[outbound.Uid].TotalCost != fact.TotalValue
            || pool.TotalCost != StockLedgerPrecision.Money(
                originalSource - fact.TotalValue + laterValue)
                && !(correctedFg == fact.TotalValue
                    && pool.TotalCost == remaining))
            throw Invalid("FG original cost or source pool value is inconsistent.");

        return new(inbounds.Select(x => x.Uid).ToArray(), outbound.Uid,
            fact.Id, fact.InventoryHistoryId,
            inventoryReceipt.Id, correctedSource, correctedFg, remaining);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
