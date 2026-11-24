using System.Globalization;
using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    internal sealed record InvoiceGoodsReceiptPurchaseProof(
        int HistoryId, long FactId, int BatchId, string PoNo, short PoRelNo, short PoLineNo,
        byte[] OrderVersion, string OrderStatus, string? ItemCode, string? PurchaseUom, string? StdUom,
        decimal Received, decimal Returned, decimal ReturnedOnCredit, decimal Invoiced);

    // A later GR retains its original receipt value. Its own settlement and any
    // other invoice or credit dependency must be absent; the invoice being
    // replayed may already account for an earlier GR on the same order line.
    private static async Task<IReadOnlyList<InvoiceGoodsReceiptPurchaseProof>?> ProveInvoiceLaterGoodsReceiptsAsync(
        AppDbContext db, string company, string branch, string item,
        string invoiceDocNo, string invoicePoNo, short invoicePoRelNo, short invoicePoLineNo,
        decimal invoiceQty, bool invoicePosted,
        IReadOnlyList<IvTrxHistory> histories, IReadOnlyDictionary<long, StockPosting> owners,
        IReadOnlyDictionary<int, IReadOnlyList<StockValuationFact>> active, CancellationToken ct)
    {
        var goods = histories.Where(h => h.TrxType == IvTrxTypes.GoodsReceive).OrderBy(h => h.Id).ToArray();
        if (goods.Length == 0)
            return Array.Empty<InvoiceGoodsReceiptPurchaseProof>();
        if (invoiceQty <= 0m || goods.Any(h => string.IsNullOrWhiteSpace(h.PoNo)
                || h.PoRelNo is null || h.PoLineNo is null))
            return null;

        var historyIds = goods.Select(h => h.Id).ToArray();
        var factIds = goods.Select(h => active[h.Id][0].Id).ToArray();
        if (await db.PurchaseReceiptCostSettlements.AsNoTracking().AnyAsync(s =>
            s.CompanyCode == company && s.BranchCode == branch
            && (s.ReceiptInventoryHistoryId != null && historyIds.Contains(s.ReceiptInventoryHistoryId.Value)
                || factIds.Contains(s.ReceiptValuationFactId)), ct))
            return null;

        var poNos = goods.Select(h => h.PoNo!.Trim()).Distinct().ToArray();
        var orders = await db.PoOrders.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && poNos.Contains(p.PoNo)).ToArrayAsync(ct);
        var lines = await db.PoOrderDetails.AsNoTracking().Where(p => p.CompanyCode == company
            && p.BranchCode == branch && poNos.Contains(p.PoNo)).ToArrayAsync(ct);
        var postedInvoices = await db.PoInvoiceDetails.AsNoTracking().Where(d => d.CompanyCode == company
            && d.BranchCode == branch && d.PoNo != null && poNos.Contains(d.PoNo)
            && d.Invoice.Status == "POSTED")
            .Select(d => new { d.DocNo, d.PoNo, d.PoRelNo, d.PoLineNo }).ToArrayAsync(ct);
        var postedNotes = await db.PoCdnDetails.AsNoTracking().Where(d => d.CompanyCode == company
            && d.BranchCode == branch && d.PoNo != null && poNos.Contains(d.PoNo)
            && d.Cdn.Status == "POSTED")
            .Select(d => new { d.PoNo, d.PoRelNo, d.PoLineNo }).ToArrayAsync(ct);
        var batchNumbers = goods.Select(h => h.BatchNo).Distinct().ToArray();
        var batches = await db.IvTrxBatches.AsNoTracking().Where(b => b.CompanyCode == company
            && b.BranchCode == branch && batchNumbers.Contains(b.BatchNo)).ToArrayAsync(ct);
        var proof = new List<InvoiceGoodsReceiptPurchaseProof>(goods.Length);
        foreach (var h in goods)
        {
            bool SameOrder(string? no, short? rel, short? line) =>
                string.Equals(no?.Trim(), h.PoNo!.Trim(), StringComparison.OrdinalIgnoreCase)
                && rel == h.PoRelNo && line == h.PoLineNo;
            var matchingOrders = orders.Where(p => string.Equals(p.PoNo.Trim(), h.PoNo!.Trim(),
                StringComparison.OrdinalIgnoreCase) && p.PoRelNo == h.PoRelNo).ToArray();
            var matchingLines = lines.Where(p => SameOrder(p.PoNo, p.PoRelNo, p.Line)).ToArray();
            var matchingBatches = batches.Where(b => b.BatchNo == h.BatchNo).ToArray();
            var owner = owners[h.StockPostingId!.Value];
            var fact = active[h.Id][0];
            var isInvoiceLine = SameOrder(invoicePoNo, invoicePoRelNo, invoicePoLineNo);
            var expectedInvoiced = isInvoiceLine && invoicePosted ? invoiceQty : 0m;
            if (matchingOrders.Length != 1 || matchingLines.Length != 1 || matchingBatches.Length != 1
                || matchingBatches[0].TrxType != IvTrxTypes.GoodsReceive
                || matchingBatches[0].BatchStatus != IvBatchStatuses.Posted
                || owner.SourceDocumentId != matchingBatches[0].Id.ToString(CultureInfo.InvariantCulture)
                || owner.SourceDocumentNo != h.BatchNo.ToString(CultureInfo.InvariantCulture)
                || matchingLines[0].ICode != item || matchingLines[0].StdUom != h.ToStdUom
                || matchingLines[0].InvoicedQty != expectedInvoiced || matchingLines[0].ReturnQty != 0m
                || matchingLines[0].ReturnQtyCn != 0m || matchingLines[0].RecvQty <= 0m
                || h.BaseUnitPrices is not decimal receiptUnit || receiptUnit < 0m
                || StockLedgerPrecision.Money(receiptUnit) != fact.UnitCost
                || postedInvoices.Any(d => SameOrder(d.PoNo, d.PoRelNo, d.PoLineNo)
                    && (!isInvoiceLine || !invoicePosted || d.DocNo != invoiceDocNo))
                || postedNotes.Any(d => SameOrder(d.PoNo, d.PoRelNo, d.PoLineNo)))
                return null;
            var order = matchingOrders[0];
            var line = matchingLines[0];
            proof.Add(new(h.Id, fact.Id, matchingBatches[0].Id, line.PoNo, line.PoRelNo, line.Line,
                order.RowVersion, order.Status, line.ICode, line.PurchaseUom, line.StdUom,
                line.RecvQty, line.ReturnQty, line.ReturnQtyCn, line.InvoicedQty));
        }
        return proof;
    }

    internal static async Task<bool> LockInvoiceLaterGoodsReceiptOrdersAsync(AppDbContext db,
        string company, string branch, IReadOnlyList<InvoiceGoodsReceiptPurchaseProof>? receipts, CancellationToken ct)
    {
        if (receipts is null || receipts.Count == 0)
            return true;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Purchase evidence requires the replay transaction.");
        var headers = receipts.Select(r => new { r.PoNo, r.PoRelNo }).Distinct().ToArray();
        var lines = receipts.Select(r => new { r.PoNo, r.PoRelNo, r.PoLineNo }).Distinct().ToArray();
        var headersJson = JsonSerializer.Serialize(headers);
        var linesJson = JsonSerializer.Serialize(lines);
        // Normal invoice posting locks PO rows before the branch. NOWAIT avoids
        // waiting on that inverted order while the replay holds the branch lock.
        var lockedHeaders = await db.PoOrders.FromSqlInterpolated($@"
            SELECT p.* FROM dbo.POOrder AS p WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            INNER JOIN OPENJSON({headersJson}) WITH (PONo nvarchar(30) '$.PoNo', PORelNo smallint '$.PoRelNo') AS k
                ON p.PONo = k.PONo AND p.PORelNo = k.PORelNo
            WHERE p.CompanyCode = {company} AND p.BranchCode = {branch}")
            .AsNoTracking().ToArrayAsync(ct);
        var lockedLines = await db.PoOrderDetails.FromSqlInterpolated($@"
            SELECT p.* FROM dbo.PODetail AS p WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            INNER JOIN OPENJSON({linesJson}) WITH (PONo nvarchar(30) '$.PoNo', PORelNo smallint '$.PoRelNo',
                POLineNo smallint '$.PoLineNo') AS k
                ON p.PONo = k.PONo AND p.PORelNo = k.PORelNo AND p.Line = k.POLineNo
            WHERE p.CompanyCode = {company} AND p.BranchCode = {branch}")
            .AsNoTracking().ToArrayAsync(ct);
        return lockedHeaders.Length == headers.Length && lockedLines.Length == lines.Length
            && headers.All(k => lockedHeaders.Any(p => string.Equals(p.PoNo, k.PoNo,
                StringComparison.OrdinalIgnoreCase) && p.PoRelNo == k.PoRelNo))
            && lines.All(k => lockedLines.Any(p => string.Equals(p.PoNo, k.PoNo,
                StringComparison.OrdinalIgnoreCase) && p.PoRelNo == k.PoRelNo && p.Line == k.PoLineNo));
    }
}
