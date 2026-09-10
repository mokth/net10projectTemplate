using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Canonical lock order: Invoice (if InvNo) → CN. Caller then locks CR → BalLoc.
/// Inventory posting must never acquire Invoice/CN locks.
/// </summary>
public static class SaCdnLockOrder
{
    public sealed record Result(SaInvoice? Invoice, SaCdn? Cdn);

    public static async Task<Result> AcquireAsync(
        AppDbContext db,
        ISaInvoiceRepository invoices,
        ISaCdnRepository cdns,
        string companyCode,
        string branchCode,
        string? invNo,
        string? docNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(cdns);

        SaInvoice? invoice = null;
        var invoiceNo = (invNo ?? string.Empty).Trim();
        if (invoiceNo.Length > 0)
        {
            invoice = await invoices.LockForUpdateAsync(db, companyCode, branchCode, invoiceNo, cancellationToken);
        }

        SaCdn? cdn = null;
        var no = (docNo ?? string.Empty).Trim();
        if (no.Length > 0)
        {
            cdn = await cdns.LockForUpdateAsync(db, companyCode, branchCode, no, cancellationToken);
        }

        return new Result(invoice, cdn);
    }
}

public static class SaCdnCrLock
{
    public static Task<IvTrxBatch?> LockByCnRefAsync(
        AppDbContext db,
        IIvStockPostingRepository posting,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posting);
        var refNo = SaCdnSpRefs.ToRefNo(docNo);
        return posting.LockBatchByTrxTypeAndRefAsync(
            db,
            companyCode,
            branchCode,
            IvTrxTypes.CustomerReturn,
            refNo,
            cancellationToken);
    }
}
