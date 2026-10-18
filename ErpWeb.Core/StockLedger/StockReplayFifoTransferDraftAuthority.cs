using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoTransferDraftAuthority(
    int BatchId, int BatchNo, int DetailId, int DocumentRevision,
    string ItemCode, int SourceBalLocId, int DestinationBalLocId,
    DateTime EffectiveAt, decimal Quantity, string BaseUom);

/// <summary>
/// Identifies the one-line, non-lot draft transfer that a paired physical
/// replay plan may eventually post. The FIFO pool, not the draft display
/// price, supplies its authoritative cost.
/// </summary>
public static class StockReplayFifoTransferDraftAuthorityReader
{
    public static async Task<StockReplayFifoTransferDraftAuthority> ReadAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, int sourceBalLocId, int destinationBalLocId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (batchNo <= 0 || sourceBalLocId <= 0
            || destinationBalLocId <= 0
            || sourceBalLocId == destinationBalLocId)
            throw Invalid("FIFO transfer needs a batch and two distinct piles.");
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.BatchNo == batchNo)
            .ToArrayAsync(cancellationToken);
        if (batches.Length != 1)
            throw Invalid("FIFO transfer has no unique draft batch.");
        var batch = batches[0];
        if (batch.TrxType != IvTrxTypes.StockTransfer
            || batch.BatchStatus != IvBatchStatuses.New
            || batch.DeletedAtUtc is not null
            || batch.ForceCloseDate is not null)
            throw Invalid("FIFO transfer needs an active NEW batch.");
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id)
            .ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            throw Invalid("FIFO transfer replay currently needs one draft line.");
        var detail = details[0];
        if (detail.CompanyCode != companyCode
            || detail.BranchCode != branchCode
            || detail.BatchNo != batchNo
            || detail.TrxType != IvTrxTypes.StockTransfer
            || detail.DocumentRevision < 0
            || detail.TrxLineNo <= 0
            || string.IsNullOrWhiteSpace(detail.ICode)
            || detail.FromBalLocId != sourceBalLocId
            || detail.ToBalLocId is not null
            || detail.FrStdQty is not decimal quantity
            || quantity <= 0m || IvQty.Round(quantity) != quantity
            || detail.ToStdQty != quantity
            || string.IsNullOrWhiteSpace(detail.FrStdUom)
            || detail.FrStdUom != detail.ToStdUom
            || detail.FromLotId is not null
            || detail.ToLotId is not null
            || !string.IsNullOrWhiteSpace(detail.FrLotNo)
            || !string.IsNullOrWhiteSpace(detail.ToLotNo))
            throw Invalid("The draft transfer is not one supported paired non-lot movement.");
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && (b.Id == sourceBalLocId || b.Id == destinationBalLocId))
            .ToArrayAsync(cancellationToken);
        var source = piles.SingleOrDefault(b => b.Id == sourceBalLocId);
        var destination = piles.SingleOrDefault(b => b.Id == destinationBalLocId);
        if (piles.Length != 2 || source is null || destination is null
            || source.ICode != detail.ICode
            || destination.ICode != detail.ICode
            || source.LotId is not null || destination.LotId is not null
            || !string.IsNullOrWhiteSpace(source.LotNo)
            || !string.IsNullOrWhiteSpace(destination.LotNo)
            || source.WhCode != detail.FrWarehouse
            || source.LocCode != (detail.FrLocation ?? string.Empty)
            || destination.WhCode != detail.ToWarehouse
            || destination.LocCode != (detail.ToLocation ?? string.Empty)
            || source.IStatus != detail.IStatus
            || destination.IStatus != detail.IStatus
            || source.StdUom != detail.FrStdUom
            || destination.StdUom != detail.ToStdUom)
            throw Invalid("The draft transfer does not match its two existing stock piles.");
        var masters = await db.IvStockMasters.AsNoTracking()
            .Where(m => m.CompanyCode == companyCode
                && m.ICode == detail.ICode)
            .ToArrayAsync(cancellationToken);
        if (masters.Length != 1 || !masters[0].IsActive
            || !masters[0].StockControl || masters[0].LotControl)
            throw Invalid("FIFO transfer needs an active non-lot stock item.");
        return new(batch.Id, batchNo, detail.Id, detail.DocumentRevision,
            detail.ICode!, sourceBalLocId, destinationBalLocId,
            batch.TrxDtTime, quantity, detail.FrStdUom!);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
