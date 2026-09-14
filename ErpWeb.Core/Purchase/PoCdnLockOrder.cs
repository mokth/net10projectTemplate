using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Canonical lock order: <c>PoInvoice (if InvNo) → PoCdn</c>. The caller then locks the owned
/// VR batch → balance rows. Inventory posting must never acquire PoInvoice / PoCdn locks.
/// </summary>
/// <remarks>
/// This is the lock that makes C34 a real ceiling rather than advice: source-line consumption is
/// read here, after the invoice lock is held, so two concurrent saves cannot each read the same
/// remaining quantity.
/// </remarks>
public static class PoCdnLockOrder
{
    public sealed record Result(PoInvoice? Invoice, PoCdn? Cdn);

    public static async Task<Result> AcquireAsync(
        AppDbContext db,
        IPoInvoiceRepository invoices,
        IPoCdnRepository cdns,
        string companyCode,
        string branchCode,
        string? invNo,
        string? docNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(cdns);

        PoInvoice? invoice = null;
        var invoiceNo = (invNo ?? string.Empty).Trim();
        if (invoiceNo.Length > 0)
        {
            invoice = await invoices.LockForUpdateAsync(db, companyCode, branchCode, invoiceNo, cancellationToken);
        }

        PoCdn? cdn = null;
        var no = (docNo ?? string.Empty).Trim();
        if (no.Length > 0)
        {
            cdn = await cdns.LockForUpdateAsync(db, companyCode, branchCode, no, cancellationToken);
        }

        return new Result(invoice, cdn);
    }
}

/// <summary>Locks the single VR batch owned by a PoCdn, addressed by its <c>PCN/</c> reference.</summary>
public static class PoCdnVrLock
{
    public static Task<IvTrxBatch?> LockByVrRefAsync(
        AppDbContext db,
        IIvStockPostingRepository posting,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posting);
        var refNo = PoCdnSpRefs.ToVrRefNo(docNo);
        return posting.LockBatchByTrxTypeAndRefAsync(
            db,
            companyCode,
            branchCode,
            IvTrxTypes.VendorReturn,
            refNo,
            cancellationToken);
    }
}
