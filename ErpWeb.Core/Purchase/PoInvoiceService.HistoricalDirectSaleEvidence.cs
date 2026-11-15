using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    private sealed record InvoiceDirectSaleProof(int HistoryId, int BatchId, int BatchNo, short HistoryLine,
        int? HistoryRevision, short? SaleLine, long OwnerId, int OwnerRevision,
        string OwnerFingerprint, string OwnerSnapshotHash,
        string InvoiceNo, byte[] InvoiceVersion,
        DateTime InvoiceDate, string InvoiceStatus, string Customer, int LineId, int Line,
        string? ItemCode, decimal Qty, decimal StdQty, string? StdUom, decimal NetAmount);

    private static async Task<IReadOnlyList<InvoiceDirectSaleProof>?> ProveInvoiceDirectSalesAsync(
        AppDbContext db, string company, string branch, IReadOnlyList<IvTrxHistory> histories, CancellationToken ct)
    {
        var sales = histories.Where(h => h.TrxType == IvTrxTypes.SalesOut).ToArray();
        if (sales.Length == 0)
            return Array.Empty<InvoiceDirectSaleProof>();
        if (sales.Length != 1 || histories.Any(h =>
                h.TrxType is not (IvTrxTypes.SalesOut or IvTrxTypes.MiscellaneousIssue
                    or IvTrxTypes.MiscellaneousReceipt)))
            return null;
        if (!string.IsNullOrWhiteSpace(sales[0].DoNo))
            return Array.Empty<InvoiceDirectSaleProof>();
        // Customer returns and multiple sales remain on the existing guard.
        if (string.IsNullOrWhiteSpace(sales[0].InvNo)
            || await IvInventoryPostingService.CheckDirectSalesInvoiceForPurchaseReplayAsync(db, sales[0], ct) is not null)
            return null;
        var invoice = await db.SaInvoices.AsNoTracking().Include(i => i.Details).SingleOrDefaultAsync(i =>
            i.CompanyCode == company && i.BranchCode == branch && i.InvNo == sales[0].InvNo, ct);
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyCode == company
            && b.BranchCode == branch && b.BatchNo == sales[0].BatchNo, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.CompanyCode == company
            && p.BranchCode == branch && p.Id == sales[0].StockPostingId, ct);
        if (invoice is null || invoice.Details.Count != 1 || batch is null || owner is null
            || batch.TrxType != IvTrxTypes.SalesOut || batch.BatchStatus != IvBatchStatuses.Posted
            || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batch.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return null;
        var line = invoice.Details.Single();
        return [new(sales[0].Id, batch.Id, batch.BatchNo, sales[0].TrxLineNo, sales[0].DocumentRevision,
            sales[0].SoLineNo, owner.Id, owner.DocumentRevision, owner.RequestFingerprint, owner.SourceSnapshotHash,
            invoice.InvNo, invoice.RowVersion, invoice.InvDate, invoice.Status,
            invoice.CustCode, line.Id, line.Line, line.ICode, line.Qty, line.StdQty, line.StdUom, line.NetAmount)];
    }

    private static async Task<bool> LockInvoiceDirectSalesAsync(AppDbContext db, string company, string branch,
        IReadOnlyList<InvoiceDirectSaleProof>? sales, CancellationToken ct)
    {
        if (sales is null || sales.Count == 0)
            return true;
        if (sales.Count != 1 || db.Database.CurrentTransaction is null)
            return false;
        var invoiceNo = sales[0].InvoiceNo;
        // Sales posting and returns may lock the invoice before the stock
        // branch. Refuse a busy document while retaining the branch lock.
        var invoices = await db.SaInvoices.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {invoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var lines = await db.SaInvoiceDetails.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {invoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var notes = await db.SaCdns.FromSqlInterpolated($@"SELECT * FROM dbo.SaCDN WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {invoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        return invoices.Length == 1 && lines.Length == 1 && notes.Length == 0
            && lines[0].Id == sales[0].LineId && lines[0].Line == sales[0].Line;
    }
}
