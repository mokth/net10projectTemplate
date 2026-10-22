using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Resolves the value of one fully transferred WIP pool from its exact active
/// source receipts. This is a bounded authority for callers that have already
/// propagated corrected costs into those receipts.
/// </summary>
internal static class ProductionWorkOrderTransferSourceCostAuthority
{
    public static async Task<decimal> ReadWithinRootAsync(
        StockPostingContext context, long transferId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null || transferId <= 0)
            throw Invalid("Transfer source cost authority requires a replay transaction and transfer ID.");

        var transfer = await db.ProductionWorkOrderTransfers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == transferId && x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode, cancellationToken);
        if (transfer is null || transfer.Status != ProductionWorkOrderTransferStatuses.Posted
            || transfer.ReversalPostingId is not null
            || transfer.StockPostingId is not long transferOwnerId
            || transfer.TransferOutMovementId is not long outboundId)
            throw Invalid("Transfer source cost authority needs an active posted transfer.");

        var outbound = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == outboundId, cancellationToken);
        var source = await db.ProductionBalLots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == transfer.SourceProductionBalLotId, cancellationToken);
        var valuation = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == transfer.SourceProductionBalLotId,
                cancellationToken);
        var owner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == transferOwnerId, cancellationToken);
        if (outbound is null || source is null || valuation is null || owner is null
            || outbound.ProductionBalLotId != source.Uid
            || outbound.MovementType != ProductionBalLotMovementTypes.TransferOut
            || outbound.StockPostingId != owner.Id
            || outbound.LedgerVersion != 2 || outbound.LedgerEpochId != context.Epoch.Id
            || outbound.ValuationStatus != ProductionPoolValuationService.Verified
            || outbound.OriginalMovementId is not null
            || owner.CompanyCode != context.CompanyCode || owner.BranchCode != context.BranchCode
            || owner.LedgerEpochId != context.Epoch.Id || owner.SealedAtUtc is null
            || owner.PostingRole != "PRIMARY"
            || owner.PostingSequence <= 0 || outbound.MovementDate != owner.EffectiveAt
            || source.CompanyCode != context.CompanyCode || source.BranchCode != context.BranchCode
            || source.Kind != ProductionBalLotKinds.Wip
            || source.Qty != 0m || source.BaseQty != 0m
            || source.TotalCost != 0m || source.AverageUnitCost != 0m
            || valuation.Status != ProductionPoolValuationService.Verified
            || valuation.TrackedBaseQty != 0m || valuation.TrackedValue != 0m)
            throw Invalid("Transfer source pool or posting is not a verified full-balance transfer.");

        var poolMovements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => x.ProductionBalLotId == source.Uid).ToArrayAsync(cancellationToken);
        var receipts = poolMovements.Where(x => x.Uid != outboundId).ToArray();
        var receiptIds = receipts.Select(x => x.Uid).ToArray();
        var receiptOwnerIds = receipts.Select(x => x.StockPostingId ?? 0).Distinct().ToArray();
        var receiptOwners = await db.StockPostings.AsNoTracking()
            .Where(x => receiptOwnerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(x => x.ProductionBalLotId == source.Uid && receiptIds.Contains(x.MovementId))
            .ToArrayAsync(cancellationToken);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(x => x.OutboundMovementId == outboundId).ToArrayAsync(cancellationToken);
        var dependencies = await ProductionActiveEdgeResolver.Dependencies(db).AsNoTracking()
            .Where(x => x.ConsumerMovementId == outboundId).ToArrayAsync(cancellationToken);
        var reversedOwner = await db.StockPostings.AsNoTracking().AnyAsync(x =>
            x.SealedAtUtc != null && x.ReversesPostingId != null
            && (x.ReversesPostingId == owner.Id
                || receiptOwnerIds.Contains(x.ReversesPostingId.Value)), cancellationToken);
        var reversedReceipt = await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(x =>
            x.OriginalMovementId != null && receiptIds.Contains(x.OriginalMovementId.Value),
            cancellationToken);
        var registry = new StockMovementRegistry();
        if (receipts.Length == 0 || poolMovements.Length != receipts.Length + 1
            || reversedOwner || reversedReceipt
            || receiptOwnerIds.Contains(0) || receiptOwners.Count != receiptOwnerIds.Length
            || evidence.Length != receipts.Length
            || allocations.Length != receipts.Length || dependencies.Length != receipts.Length
            || receipts.Sum(x => x.BaseQty) != outbound.BaseQty
            || receipts.Sum(x => x.Qty) != outbound.Qty
            || StockLedgerPrecision.Money(receipts.Sum(x => x.TotalCost)) != outbound.TotalCost
            || receipts.Any(x => x.OriginalMovementId is not null
                || x.LedgerVersion != 2 || x.LedgerEpochId != context.Epoch.Id
                || x.CompanyCode != context.CompanyCode || x.BranchCode != context.BranchCode
                || x.ValuationStatus != ProductionPoolValuationService.Verified
                || x.BaseQty <= 0m || x.Qty <= 0m
                || x.ItemCode != outbound.ItemCode || x.BaseUom != outbound.BaseUom
                || x.Uom != source.Uom || x.ConversionFactorToBase != source.ConversionFactorToBase
                || x.WorkOrderId != source.WorkOrderId
                || x.RouteStepId != source.ProducingRouteStepId
                || x.WorkOrderOperationId != source.WorkOrderOperationId
                || registry.GetRequired(x.MovementType).Direction <= 0
                || !receiptOwners.TryGetValue(x.StockPostingId!.Value, out var receiptOwner)
                || receiptOwner.CompanyCode != context.CompanyCode
                || receiptOwner.BranchCode != context.BranchCode
                || receiptOwner.LedgerEpochId != context.Epoch.Id
                || receiptOwner.SealedAtUtc is null
                || receiptOwner.PostingRole != "PRIMARY"
                || receiptOwner.PostingSequence <= 0
                || receiptOwner.PostingSequence >= owner.PostingSequence
                || x.MovementDate != receiptOwner.EffectiveAt
                || evidence.Count(e => e.MovementId == x.Uid
                    && e.Generation == valuation.Generation
                    && e.Status == ProductionPoolValuationService.Verified) != 1
                || allocations.Count(a => a.ReceiptMovementId == x.Uid
                    && a.StockPostingId == owner.Id
                    && a.CompanyCode == context.CompanyCode
                    && a.BranchCode == context.BranchCode
                    && a.BaseQty == x.BaseQty) != 1
                || dependencies.Count(d => d.ContributorMovementId == x.Uid
                    && d.StockPostingId == owner.Id) != 1))
            throw Invalid("Transfer source receipt cost or full-balance allocation is incomplete.");

        var costs = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, receiptIds, cancellationToken);
        return StockLedgerPrecision.Money(receiptIds.Sum(id => costs[id].TotalCost));
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
