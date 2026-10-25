using ErpWeb.Model.Data;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private async Task<StockReplayInventoryRouteSelector.Decision>
        ReadInventoryReplayRouteAsync(string companyCode, string branchCode,
            int batchNo, string trxType, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await StockReplayInventoryRouteSelector.ReadAsync(read,
            companyCode, branchCode, batchNo, trxType, cancellationToken);
    }

    private static IvInventoryPostingBatchResult UnsupportedHistoricalInventoryPost(
        int batchNo, string trxType) =>
        IvInventoryPostingBatchResult.Fail(batchNo,
            $"Backdated {trxType} has a historical stock or cost dependency, "
            + "but this document shape is not supported by a replay route. "
            + "Posting was not attempted through legacy chronology checks.");

    private async Task<StockReplayInventoryRouteSelector.Decision>
        ReadInventoryRollbackRouteAsync(string companyCode, string branchCode,
            int batchNo, string trxType, CancellationToken cancellationToken)
    {
        await using var read = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await StockReplayInventoryRouteSelector.ReadRollbackAsync(read,
            companyCode, branchCode, batchNo, trxType, cancellationToken);
    }

    private static IvInventoryPostingBatchResult UnsupportedHistoricalInventoryRollback(
        int batchNo, string trxType) =>
        IvInventoryPostingBatchResult.Fail(batchNo,
            $"Rollback of {trxType} has a later stock or cost dependency "
            + "that is not supported by a replay route. "
            + "The legacy rollback path was not used.");

    private async Task<IvInventoryPostingBatchResult?> RejectFastPathStalenessAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, string trxType, CancellationToken cancellationToken)
    {
        if (!_openPeriodBackdatedReplayEnabled)
            return null;
        var current = await StockReplayInventoryRouteSelector.ReadAsync(db,
            companyCode, branchCode, batchNo, trxType, cancellationToken);
        return current.RequiresReplay
            ? IvInventoryPostingBatchResult.Fail(batchNo,
                "Stock chronology changed during routing. Retry the posting request.")
            : null;
    }

    private async Task<IvInventoryPostingBatchResult?> RejectFastRollbackStalenessAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, string trxType, CancellationToken cancellationToken)
    {
        if (!_openPeriodBackdatedReplayEnabled)
            return null;
        var current = await StockReplayInventoryRouteSelector.ReadRollbackAsync(db,
            companyCode, branchCode, batchNo, trxType, cancellationToken);
        return current.RequiresReplay
            ? IvInventoryPostingBatchResult.Fail(batchNo,
                "Stock chronology changed during rollback routing. Retry the request.")
            : null;
    }
}
