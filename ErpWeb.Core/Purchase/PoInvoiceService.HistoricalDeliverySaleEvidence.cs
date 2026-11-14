using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    private sealed record InvoiceDeliverySaleProof(
        int HistoryId, int BatchId, int BatchNo, short HistoryLine, int? HistoryRevision, short? SaleLine,
        long OwnerId, int OwnerRevision, string OwnerFingerprint, string OwnerSnapshotHash,
        string DeliveryNo, byte[] DeliveryVersion, DateTime DeliveryDate, string DeliveryStatus, string BillingStatus,
        string Customer, string? Currency, short DeliveryLine, string? DeliveryInvoice, string SalesOrderNo,
        short? SalesOrderLine, short? CustomerRevision, string? ItemCode, decimal Qty, decimal StdQty,
        decimal StdPackSize, string? StdUom, decimal NetAmount, string? InvoiceNo, byte[]? InvoiceVersion, DateTime? InvoiceDate,
        string? InvoiceStatus, int? InvoiceLineId, int? InvoiceLine, decimal? InvoiceQty, decimal? InvoiceStdQty,
        string? InvoiceStdUom, decimal? InvoiceNetAmount, long? ApplicationId, short? ApplicationSourceRevision,
        short? ApplicationSourceLine, string? ApplicationTargetType, string? ApplicationTargetDocument,
        short? ApplicationTargetRevision, short? ApplicationTargetLine, string? ApplicationRelatedOrder,
        short? ApplicationRelatedRevision, short? ApplicationRelatedLine, decimal? ApplicationQty, decimal? ApplicationAmount);

    private static async Task<IReadOnlyList<InvoiceDeliverySaleProof>?> ProveInvoiceDeliverySalesAsync(
        AppDbContext db, string company, string branch, IReadOnlyList<IvTrxHistory> histories, CancellationToken ct)
    {
        var sales = histories.Where(h => h.TrxType == IvTrxTypes.SalesOut).ToArray();
        if (sales.Length == 0)
            return Array.Empty<InvoiceDeliverySaleProof>();
        if (sales.Length != 1 || histories.Count != 1)
            return null;
        var history = sales[0];
        if (string.IsNullOrWhiteSpace(history.DoNo))
            return Array.Empty<InvoiceDeliverySaleProof>();
        if (await IvInventoryPostingService.CheckDeliverySaleForPurchaseReplayAsync(db, history, ct) is not null)
            return null;
        var deliveryNo = history.DoNo.Trim();
        var delivery = await db.SaDos.AsNoTracking().Include(d => d.Details).SingleOrDefaultAsync(d =>
            d.CompanyCode == company && d.BranchCode == branch && d.DoNo == deliveryNo, ct);
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyCode == company
            && b.BranchCode == branch && b.BatchNo == history.BatchNo, ct);
        var owner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.CompanyCode == company
            && p.BranchCode == branch && p.Id == history.StockPostingId, ct);
        if (delivery is null || delivery.Details.Count != 1 || batch is null || owner is null
            || batch.TrxType != IvTrxTypes.SalesOut || batch.BatchStatus != IvBatchStatuses.Posted
            || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batch.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return null;
        var line = delivery.Details.Single();
        var applications = await db.SaDocApplications.AsNoTracking().Where(a => a.CompanyCode == company
            && a.BranchCode == branch && a.SourceDocType == SaDocTypes.Do && a.SourceDocId == deliveryNo).ToArrayAsync(ct);
        ErpWeb.Model.Entities.Sales.SaInvoice? invoice = null;
        ErpWeb.Model.Entities.Sales.SaInvoiceDetail? invoiceLine = null;
        ErpWeb.Model.Entities.Sales.SaDocApplication? application = null;
        if (delivery.BillingStatus == SaDualStatuses.Full)
        {
            if (applications.Length != 1)
                return null;
            application = applications[0];
            invoice = await db.SaInvoices.AsNoTracking().Include(i => i.Details).SingleOrDefaultAsync(i =>
                i.CompanyCode == company && i.BranchCode == branch && i.InvNo == application.TargetDocId, ct);
            if (invoice is null || invoice.Details.Count != 1)
                return null;
            invoiceLine = invoice.Details.Single();
        }
        else if (delivery.BillingStatus != SaDualStatuses.None || applications.Length != 0)
            return null;
        return [new(history.Id, batch.Id, batch.BatchNo, history.TrxLineNo, history.DocumentRevision,
            history.SoLineNo, owner.Id, owner.DocumentRevision, owner.RequestFingerprint, owner.SourceSnapshotHash,
            delivery.DoNo, delivery.RowVersion, delivery.DoDate, delivery.Status, delivery.BillingStatus,
            delivery.CustCode, delivery.Currency, line.Line, line.InvNo, line.SoNo, line.SoLine, line.CustRel,
            line.ICode, line.Qty, line.StdQty, line.StdPsize, line.StdUom,
            line.NetAmount, invoice?.InvNo, invoice?.RowVersion, invoice?.InvDate, invoice?.Status,
            invoiceLine?.Id, invoiceLine?.Line, invoiceLine?.Qty, invoiceLine?.StdQty, invoiceLine?.StdUom,
            invoiceLine?.NetAmount, application?.Id, application?.SourceCustRel, application?.SourceLineId,
            application?.TargetDocType, application?.TargetDocId, application?.TargetCustRel, application?.TargetLineId,
            application?.RelatedSoNo, application?.RelatedCustRel, application?.RelatedSoLine,
            application?.AppliedQty, application?.AppliedAmount)];
    }

    private static async Task<bool> LockInvoiceDeliverySalesAsync(AppDbContext db, string company, string branch,
        IReadOnlyList<InvoiceDeliverySaleProof>? deliveries, CancellationToken ct)
    {
        if (deliveries is null || deliveries.Count == 0)
            return true;
        if (deliveries.Count != 1 || db.Database.CurrentTransaction is null)
            return false;
        var proof = deliveries[0];
        // Billing and delivery rollback take the DO lock before changing its
        // application; NOWAIT avoids waiting while the branch lock is held.
        var headers = await db.SaDos.FromSqlInterpolated($@"SELECT * FROM dbo.SaDO WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND DONo = {proof.DeliveryNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var lines = await db.SaDoDetails.FromSqlInterpolated($@"SELECT * FROM dbo.SaDODetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND DONo = {proof.DeliveryNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var applications = await db.SaDocApplications.FromSqlInterpolated($@"SELECT * FROM dbo.SaDocApplication WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND SourceDocType = {SaDocTypes.Do}
                AND SourceDocId = {proof.DeliveryNo}")
            .AsNoTracking().ToArrayAsync(ct);
        if (headers.Length != 1 || lines.Length != 1 || lines[0].Line != proof.DeliveryLine
            || applications.Length != (proof.ApplicationId is null ? 0 : 1)
            || proof.ApplicationId is long applicationId && applications[0].Id != applicationId)
            return false;
        if (proof.InvoiceNo is null)
            return true;
        var invoices = await db.SaInvoices.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {proof.InvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var invoiceLines = await db.SaInvoiceDetails.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {proof.InvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        var notes = await db.SaCdns.FromSqlInterpolated($@"SELECT * FROM dbo.SaCDN WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {proof.InvoiceNo}")
            .AsNoTracking().ToArrayAsync(ct);
        return invoices.Length == 1 && invoiceLines.Length == 1 && notes.Length == 0
            && invoiceLines[0].Id == proof.InvoiceLineId && invoiceLines[0].Line == proof.InvoiceLine;
    }
}
