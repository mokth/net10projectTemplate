using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Builds the same provisional physical event for an ordinary Inventory root,
/// independent of its document type and costing method. The returned plans
/// remain read-only and must be revalidated under the posting root lock.
/// </summary>
internal static class StockReplayInventoryCandidatePlanner
{
    internal static async Task<StockReplayPhysicalPlan> BuildSingleAsync(
        AppDbContext db, string companyCode, string branchCode,
        int pileId, DateTime effectiveAt, decimal signedQuantity,
        CancellationToken cancellationToken)
    {
        var key = await ReadKeyAsync(db, companyCode, branchCode,
            effectiveAt, cancellationToken);
        return await BuildAsync(db, companyCode, branchCode, pileId,
            key, signedQuantity, cancellationToken);
    }

    internal static async Task<(StockReplayPhysicalPlan Source,
        StockReplayPhysicalPlan Destination)> BuildTransferAsync(
        AppDbContext db, string companyCode, string branchCode,
        int sourcePileId, int destinationPileId, DateTime effectiveAt,
        decimal quantity, CancellationToken cancellationToken)
    {
        if (sourcePileId <= 0 || destinationPileId <= 0
            || sourcePileId == destinationPileId || quantity <= 0m
            || IvQty.Round(quantity) != quantity)
            throw new ArgumentException("A transfer needs distinct piles and a positive stock quantity.");

        var key = await ReadKeyAsync(db, companyCode, branchCode,
            effectiveAt, cancellationToken);
        var source = await BuildAsync(db, companyCode, branchCode,
            sourcePileId, key, -quantity, cancellationToken);
        var destination = await BuildAsync(db, companyCode, branchCode,
            destinationPileId, key, quantity, cancellationToken);
        if (source.LedgerEpochId != destination.LedgerEpochId
            || source.BranchWatermark != destination.BranchWatermark)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "The transfer piles were planned under different ledger states."));
        return (source, destination);
    }

    private static async Task<StockReplayEventKey> ReadKeyAsync(
        AppDbContext db, string companyCode, string branchCode,
        DateTime effectiveAt, CancellationToken cancellationToken)
    {
        var watermark = await db.StockPostingBranchSequences.AsNoTracking()
            .Where(s => s.CompanyCode == companyCode
                && s.BranchCode == branchCode)
            .Select(s => (long?)s.LastSequence)
            .SingleOrDefaultAsync(cancellationToken) ?? 0L;
        return new StockReplayEventKey(effectiveAt,
            StockReplayEventKey.OrdinaryClass, checked(watermark + 1),
            "H9999999999", 0);
    }

    private static Task<StockReplayPhysicalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode,
        int pileId, StockReplayEventKey key, decimal signedQuantity,
        CancellationToken cancellationToken) =>
        StockReplayPhysicalPlanner.BuildInsertionAsync(db,
            companyCode, branchCode, pileId,
            new StockReplayPhysicalEvent(long.MaxValue - 1,
                key, signedQuantity), cancellationToken);
}
