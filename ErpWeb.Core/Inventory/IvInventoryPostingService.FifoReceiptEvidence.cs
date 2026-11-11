using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    // Captures receipt and PO inputs that the coordinator's generic document
    // fingerprint does not include, especially approved price and received qty.
    private static async Task<string?> ReadFifoReceiptSourceHashAsync(
        AppDbContext db, string company, string branch, int batchNo, CancellationToken ct)
    {
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == batchNo, ct);
        if (batch is null) return null;
        var details = await db.IvTrxBatchDetails.AsNoTracking().Where(d => d.BatchId == batch.Id).ToArrayAsync(ct);
        if (details.Length != 1) return null;
        var detail = details[0];
        object? purchase = null;
        if (batch.TrxType == IvTrxTypes.GoodsReceive)
        {
            if (string.IsNullOrWhiteSpace(detail.PoNo) || detail.PoRelNo is null || detail.PoLineNo is null)
                return null;
            var orders = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
                && p.PoNo == detail.PoNo && p.PoRelNo == detail.PoRelNo).ToArrayAsync(ct);
            var lines = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == company && p.BranchCode == branch
                && p.PoNo == detail.PoNo && p.PoRelNo == detail.PoRelNo && p.Line == detail.PoLineNo).ToArrayAsync(ct);
            var sourceHistoryIds = await db.IvTrxHistories.AsNoTracking().Where(h => h.CompanyCode == company
                && h.BranchCode == branch && h.BatchNo == batchNo && h.TrxType == batch.TrxType)
                .Select(h => h.Id).ToArrayAsync(ct);
            if (orders.Length != 1 || lines.Length != 1 || await db.PurchaseReceiptCostSettlements.AsNoTracking()
                    .AnyAsync(s => s.CompanyCode == company && s.BranchCode == branch
                        && (s.ReceiptBatchNo == batchNo || s.ReceiptInventoryHistoryId.HasValue
                            && sourceHistoryIds.Contains(s.ReceiptInventoryHistoryId.Value)), ct))
                return null;
            var order = orders[0];
            var line = lines[0];
            purchase = new
            {
                order.PoNo, order.PoRelNo, order.PoDate, order.Status, order.VendCode,
                orderOneTime = order.OneTime, orderCurrency = order.CurCode, order.RowVersion,
                line.Line, line.ICode, lineOneTime = line.OneTime, line.PoUnitPrice,
                line.PoQty, line.PoPurQty, line.RecvQty, line.ReturnQty, line.OverRecvQty,
                line.InvoicedQty, line.PackSz, line.StdUom, line.PurchaseUom, lineCurrency = line.CurCode, line.NetAmount
            };
        }
        return StockPostingFingerprint.Create(new
        {
            company, branch, batch.Id, batch.BatchNo, batch.TrxType, batch.BatchStatus, batch.TrxDtTime,
            batch.RefNo, batch.PostedCount, batch.RollbackCount, batch.DeletedAtUtc, batch.ForceCloseDate,
            detail = new { detail.Id, detail.BatchId, detail.BatchNo, detail.CompanyCode, detail.BranchCode,
                detail.TrxType, detail.DocumentRevision, detail.TrxLineNo, detail.ICode, detail.IClassCode,
                detail.FromBalLocId, detail.ToBalLocId, detail.FromLotId, detail.ToLotId,
                detail.FrWarehouse, detail.FrLocation, detail.FrLotNo, detail.FrStdQty, detail.FrStdUom,
                detail.ToWarehouse, detail.ToLocation, detail.ToLotNo,
                detail.ToStdQty, detail.ToStdUom, detail.ToPurQty, detail.ToPurUom, detail.IStatus,
                detail.ExpiryDate, detail.Reason, detail.UnitPrice, detail.BaseUnitPrices, detail.Cost, detail.CostPrice,
                detail.Currency, detail.PoNo, detail.PoRelNo, detail.PoLineNo, detail.PriceEvidence,
                detail.CostEvidenceType, detail.CostOverrideReason, detail.CostApprovedBy, detail.CostApprovedAtUtc },
            purchase
        }, new { }).RequestFingerprint;
    }
}
