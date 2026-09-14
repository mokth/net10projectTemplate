using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Purchase;

public sealed record PoCdnSearchArgs(
    string Type,
    string? SearchText,
    string? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public interface IPoCdnRepository
{
    Task<PoCdn?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default);

    Task<PoCdn?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<PoCdn> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoCdnSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<decimal>> ListOtherCnTotAmntsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The other NEW/POSTED credit notes holding part of the invoice's remaining balance, with
    /// their document numbers and statuses, so the operator can be told <i>which</i> drafts
    /// reserved the balance rather than merely that the total was exceeded.
    /// </summary>
    Task<IReadOnlyList<PoCdnReservationRow>> ListOtherCreditNotesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// C2 pre-check: the document already holding this supplier number, if any. The unique index
    /// <c>UX_PoCdn_SupplierDoc</c> is the real control; this exists to report which document
    /// collided instead of surfacing a raw duplicate-key error.
    /// </summary>
    Task<PoCdn?> FindBySupplierDocAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string vendorCode,
        string type,
        string supplierDocNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// C34/C41: per-line consumption against a referenced invoice, for other NEW/POSTED credit
    /// notes. Only lines carrying <c>InvLineNo</c> are returned. Callers must already hold the
    /// invoice lock.
    /// </summary>
    Task<IReadOnlyList<PoCdnSourceLineUsageRow>> ListSourceLineUsageAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// C25: does any NEW/POSTED PoCdn reference this invoice? Used by <c>PoInvoiceService</c> to
    /// block rollback/delete of a referenced invoice.
    /// </summary>
    Task<bool> ExistsForInvoiceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        CancellationToken cancellationToken = default);
}

/// <summary>A credit note reserving part of a posted invoice's remaining balance.</summary>
public sealed class PoCdnReservationRow
{
    public string DocNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotAmnt { get; init; }

    public bool IsDraft => string.Equals(Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Consumption of one source invoice line by another credit note (C34).</summary>
public sealed class PoCdnSourceLineUsageRow
{
    public string DocNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public short InvLineNo { get; init; }
    public decimal StdQty { get; init; }
    public decimal NetAmount { get; init; }
    public decimal Amount { get; init; }
}

public sealed class PoCdnRepository : IPoCdnRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PoCdn.DocNo),
        nameof(PoCdn.DocDate),
        nameof(PoCdn.Status),
        nameof(PoCdn.VendorCode),
        nameof(PoCdn.TotAmnt),
        nameof(PoCdn.CreatedDate),
        nameof(PoCdn.SupplierDocNo)
    };

    public async Task<PoCdn?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (docNo ?? string.Empty).Trim();

        if (db.Database.IsSqlServer())
        {
            return await db.PoCdns
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.PoCdn WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND DocNo = {no}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.PoCdns
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == no,
                cancellationToken);
    }

    public Task<PoCdn?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var no = (docNo ?? string.Empty).Trim();
        return db.PoCdns
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == no,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<PoCdn> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoCdnSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var type = (args.Type ?? string.Empty).Trim().ToUpperInvariant();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Type == type);

        if (!string.IsNullOrWhiteSpace(args.Status))
        {
            var status = args.Status.Trim();
            query = query.Where(x => x.Status == status);
        }

        if (args.DateFrom is not null)
        {
            var from = args.DateFrom.Value.Date;
            query = query.Where(x => x.DocDate >= from);
        }

        if (args.DateTo is not null)
        {
            var toExclusive = args.DateTo.Value.Date.AddDays(1);
            query = query.Where(x => x.DocDate < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            query = query.Where(x =>
                x.DocNo.Contains(term)
                || (x.VendorCode != null && x.VendorCode.Contains(term))
                || (x.VendorName != null && x.VendorName.Contains(term))
                || (x.InvNo != null && x.InvNo.Contains(term))
                || (x.SupplierDocNo != null && x.SupplierDocNo.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);

        var sortField = AllowedSortFields.Contains(args.SortField ?? string.Empty)
            ? args.SortField!
            : nameof(PoCdn.DocDate);

        IOrderedQueryable<PoCdn> ordered = sortField.ToUpperInvariant() switch
        {
            "DOCNO" => args.SortDescending
                ? query.OrderByDescending(x => x.DocNo)
                : query.OrderBy(x => x.DocNo),
            "STATUS" => args.SortDescending
                ? query.OrderByDescending(x => x.Status)
                : query.OrderBy(x => x.Status),
            "VENDORCODE" => args.SortDescending
                ? query.OrderByDescending(x => x.VendorCode)
                : query.OrderBy(x => x.VendorCode),
            "TOTAMNT" => args.SortDescending
                ? query.OrderByDescending(x => x.TotAmnt)
                : query.OrderBy(x => x.TotAmnt),
            "CREATEDDATE" => args.SortDescending
                ? query.OrderByDescending(x => x.CreatedDate)
                : query.OrderBy(x => x.CreatedDate),
            "SUPPLIERDOCNO" => args.SortDescending
                ? query.OrderByDescending(x => x.SupplierDocNo)
                : query.OrderBy(x => x.SupplierDocNo),
            _ => args.SortDescending
                ? query.OrderByDescending(x => x.DocDate).ThenByDescending(x => x.DocNo)
                : query.OrderBy(x => x.DocDate).ThenBy(x => x.DocNo)
        };

        var rows = await ordered.Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (rows, total);
    }

    public async Task<IReadOnlyList<decimal>> ListOtherCnTotAmntsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var invoice = (invNo ?? string.Empty).Trim();
        var exclude = (excludeDocNo ?? string.Empty).Trim();

        var query = OtherCreditNoteQuery(db, company, branch, invoice, exclude);
        return await query.Select(x => x.TotAmnt).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PoCdnReservationRow>> ListOtherCreditNotesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var invoice = (invNo ?? string.Empty).Trim();
        var exclude = (excludeDocNo ?? string.Empty).Trim();

        return await OtherCreditNoteQuery(db, company, branch, invoice, exclude)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.DocNo)
            .Select(x => new PoCdnReservationRow
            {
                DocNo = x.DocNo,
                Status = x.Status,
                TotAmnt = x.TotAmnt
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<PoCdn?> FindBySupplierDocAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string vendorCode,
        string type,
        string supplierDocNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var vendor = (vendorCode ?? string.Empty).Trim();
        var docType = (type ?? string.Empty).Trim().ToUpperInvariant();
        var supplierDoc = (supplierDocNo ?? string.Empty).Trim();
        var exclude = (excludeDocNo ?? string.Empty).Trim();

        if (supplierDoc.Length == 0)
        {
            return null;
        }

        var query = db.PoCdns.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.VendorCode == vendor
                && x.Type == docType
                && x.SupplierDocNo == supplierDoc);

        if (exclude.Length > 0)
        {
            query = query.Where(x => x.DocNo != exclude);
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PoCdnSourceLineUsageRow>> ListSourceLineUsageAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var invoice = (invNo ?? string.Empty).Trim();
        var exclude = (excludeDocNo ?? string.Empty).Trim();

        if (invoice.Length == 0)
        {
            return [];
        }

        // Only CNs consume a source line: a DN never reduces the invoice (C2/C3).
        var rows = await (
                from d in db.PoCdnDetails.AsNoTracking()
                join h in db.PoCdns.AsNoTracking()
                    on new { d.CompanyCode, d.BranchCode, d.DocNo }
                    equals new { h.CompanyCode, h.BranchCode, h.DocNo }
                where h.CompanyCode == company
                      && h.BranchCode == branch
                      && h.Type == PoCdnTypes.CreditNote
                      && h.InvNo == invoice
                      && (h.Status == PoCdnStatuses.New || h.Status == PoCdnStatuses.Posted)
                      && d.InvLineNo != null
                select new
                {
                    h.DocNo,
                    h.Status,
                    InvLineNo = d.InvLineNo!.Value,
                    d.StdQty,
                    d.NetAmount,
                    d.Amount
                })
            .ToListAsync(cancellationToken);

        // Filter and group in memory: the exclude filter combines with the grouping, and a
        // deferred GroupBy over the join is not worth the translation risk.
        return rows
            .Where(x => exclude.Length == 0
                || !string.Equals(x.DocNo, exclude, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => new { x.DocNo, x.Status, x.InvLineNo })
            .Select(g => new PoCdnSourceLineUsageRow
            {
                DocNo = g.Key.DocNo,
                Status = g.Key.Status,
                InvLineNo = g.Key.InvLineNo,
                StdQty = g.Sum(x => x.StdQty),
                NetAmount = g.Sum(x => x.NetAmount),
                Amount = g.Sum(x => x.Amount)
            })
            .OrderBy(x => x.InvLineNo)
            .ThenBy(x => x.DocNo)
            .ToList();
    }

    public Task<bool> ExistsForInvoiceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var invoice = (invNo ?? string.Empty).Trim();

        if (invoice.Length == 0)
        {
            return Task.FromResult(false);
        }

        return db.PoCdns.AsNoTracking().AnyAsync(
            x => x.CompanyCode == company
                 && x.BranchCode == branch
                 && x.InvNo == invoice
                 && (x.Status == PoCdnStatuses.New || x.Status == PoCdnStatuses.Posted),
            cancellationToken);
    }

    private static IQueryable<PoCdn> OtherCreditNoteQuery(
        AppDbContext db,
        string company,
        string branch,
        string invoice,
        string exclude)
    {
        var query = db.PoCdns.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.Type == PoCdnTypes.CreditNote
                && x.InvNo == invoice
                && (x.Status == PoCdnStatuses.New || x.Status == PoCdnStatuses.Posted));

        if (exclude.Length > 0)
        {
            query = query.Where(x => x.DocNo != exclude);
        }

        return query;
    }
}

/// <summary>Status constants shared with Core (duplicated string literals for the Model layer).</summary>
public static class PoCdnStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
}

public static class PoCdnTypes
{
    public const string CreditNote = "CN";
    public const string DebitNote = "DN";
}
