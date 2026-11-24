using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    internal sealed record InvoiceDeliveryBillingLinkProof(
        string InvoiceNo, byte[] InvoiceVersion, DateTime InvoiceDate, string InvoiceStatus,
        int InvoiceLineId, int InvoiceLine, decimal InvoiceQty, decimal InvoiceStdQty,
        string? InvoiceStdUom, decimal InvoiceNetAmount, long ApplicationId, short? ApplicationSourceRevision,
        short? ApplicationSourceLine, string ApplicationTargetType, string ApplicationTargetDocument,
        short? ApplicationTargetRevision, short? ApplicationTargetLine, string? ApplicationRelatedOrder,
        short? ApplicationRelatedRevision, short? ApplicationRelatedLine, decimal ApplicationQty, decimal? ApplicationAmount);

    internal sealed record InvoiceDeliverySaleProof(
        int HistoryId, int BatchId, int BatchNo, short HistoryLine, int? HistoryRevision, short? SaleLine,
        long OwnerId, int OwnerRevision, string OwnerFingerprint, string OwnerSnapshotHash,
        string DeliveryNo, byte[] DeliveryVersion, DateTime DeliveryDate, string DeliveryStatus, string BillingStatus,
        string Customer, string? Currency, short DeliveryLine, string? DeliveryInvoice, string SalesOrderNo,
        short? SalesOrderLine, short? CustomerRevision, string? ItemCode, decimal Qty, decimal StdQty,
        decimal StdPackSize, string? StdUom, decimal NetAmount,
        IReadOnlyList<InvoiceDeliveryBillingLinkProof> BillingLinks);

    private static async Task<IReadOnlyList<InvoiceDeliverySaleProof>?> ProveInvoiceDeliverySalesAsync(
        AppDbContext db, string company, string branch, IReadOnlyList<IvTrxHistory> histories, CancellationToken ct)
    {
        var sales = histories.Where(h => h.TrxType == IvTrxTypes.SalesOut).ToArray();
        if (sales.Length == 0)
            return Array.Empty<InvoiceDeliverySaleProof>();
        if (sales.Length != 1 || histories.Any(h =>
                h.TrxType is not (IvTrxTypes.SalesOut or IvTrxTypes.MiscellaneousIssue
                    or IvTrxTypes.MiscellaneousReceipt or IvTrxTypes.GoodsReceive)))
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
        if (delivery is null || batch is null || owner is null
            || batch.TrxType != IvTrxTypes.SalesOut || batch.BatchStatus != IvBatchStatuses.Posted
            || owner.SourceDocumentId != batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || owner.SourceDocumentNo != batch.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return null;
        var line = delivery.Details.SingleOrDefault(d => d.Line == history.SoLineNo);
        if (line is null)
            return null;
        var applications = await db.SaDocApplications.AsNoTracking().Where(a => a.CompanyCode == company
            && a.BranchCode == branch && a.SourceDocType == SaDocTypes.Do
            && a.SourceDocId == deliveryNo && a.SourceLineId == line.Line)
            .OrderBy(a => a.Id)
            .ToArrayAsync(ct);
        var billingLinks = new List<InvoiceDeliveryBillingLinkProof>(applications.Length);
        if (delivery.BillingStatus is SaDualStatuses.Partial or SaDualStatuses.Full)
        {
            if (applications.Length == 0)
                return null;
            var invoiceNos = applications.Select(a => a.TargetDocId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var invoices = await db.SaInvoices.AsNoTracking().Include(i => i.Details)
                .Where(i => i.CompanyCode == company && i.BranchCode == branch && invoiceNos.Contains(i.InvNo))
                .ToDictionaryAsync(i => i.InvNo, StringComparer.OrdinalIgnoreCase, ct);
            if (invoices.Count != invoiceNos.Length)
                return null;
            foreach (var app in applications)
            {
                if (!invoices.TryGetValue(app.TargetDocId, out var invoice)
                    || invoice.Status != SaInvoiceStatuses.Posted)
                    return null;
                var invoiceLine = invoice.Details.SingleOrDefault(d =>
                    d.Line == app.TargetLineId && d.LinkDo
                    && string.Equals(d.DoNo?.Trim(), deliveryNo, StringComparison.OrdinalIgnoreCase)
                    && d.DoLine == line.Line);
                if (invoiceLine is null)
                    return null;
                billingLinks.Add(new(
                    invoice.InvNo, invoice.RowVersion, invoice.InvDate, invoice.Status,
                    invoiceLine.Id, invoiceLine.Line, invoiceLine.Qty, invoiceLine.StdQty,
                    invoiceLine.StdUom, invoiceLine.NetAmount, app.Id, app.SourceCustRel,
                    app.SourceLineId, app.TargetDocType, app.TargetDocId, app.TargetCustRel,
                    app.TargetLineId, app.RelatedSoNo, app.RelatedCustRel, app.RelatedSoLine,
                    app.AppliedQty, app.AppliedAmount));
            }
        }
        else if (delivery.BillingStatus != SaDualStatuses.None || applications.Length != 0)
            return null;
        return [new(history.Id, batch.Id, batch.BatchNo, history.TrxLineNo, history.DocumentRevision,
            history.SoLineNo, owner.Id, owner.DocumentRevision, owner.RequestFingerprint, owner.SourceSnapshotHash,
            delivery.DoNo, delivery.RowVersion, delivery.DoDate, delivery.Status, delivery.BillingStatus,
            delivery.CustCode, delivery.Currency, line.Line, line.InvNo, line.SoNo, line.SoLine, line.CustRel,
            line.ICode, line.Qty, line.StdQty, line.StdPsize, line.StdUom,
            line.NetAmount, billingLinks)];
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
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND DONo = {proof.DeliveryNo} AND Line = {proof.DeliveryLine}")
            .AsNoTracking().ToArrayAsync(ct);
        var applications = await db.SaDocApplications.FromSqlInterpolated($@"SELECT * FROM dbo.SaDocApplication WITH (UPDLOCK, HOLDLOCK, NOWAIT)
            WHERE CompanyCode = {company} AND BranchCode = {branch} AND SourceDocType = {SaDocTypes.Do}
                AND SourceDocId = {proof.DeliveryNo} AND SourceLineId = {proof.DeliveryLine}")
            .AsNoTracking().ToArrayAsync(ct);
        var expectedAppIds = proof.BillingLinks.Select(b => b.ApplicationId).OrderBy(x => x).ToArray();
        var actualAppIds = applications.Select(a => a.Id).OrderBy(x => x).ToArray();
        if (headers.Length != 1 || lines.Length != 1 || lines[0].Line != proof.DeliveryLine
            || !expectedAppIds.SequenceEqual(actualAppIds))
            return false;
        if (proof.BillingLinks.Count == 0)
            return true;
        foreach (var group in proof.BillingLinks.GroupBy(b => b.InvoiceNo, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var invNo = group.Key;
            var invoices = await db.SaInvoices.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoice WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {invNo}")
                .AsNoTracking().ToArrayAsync(ct);
            var invoiceLines = await db.SaInvoiceDetails.FromSqlInterpolated($@"SELECT * FROM dbo.SaInvoiceDetail WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {invNo}")
                .AsNoTracking().ToArrayAsync(ct);
            var notes = await db.SaCdns.FromSqlInterpolated($@"SELECT * FROM dbo.SaCDN WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                WHERE CompanyCode = {company} AND BranchCode = {branch} AND InvNo = {invNo}")
                .AsNoTracking().ToArrayAsync(ct);
            if (invoices.Length != 1 || notes.Length != 0
                || group.Any(b => !invoiceLines.Any(l => l.Id == b.InvoiceLineId && l.Line == b.InvoiceLine)))
                return false;
        }
        return true;
    }
}
