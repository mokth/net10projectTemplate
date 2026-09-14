using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Purchase;

public sealed record PoInvoiceSearchArgs(
    string? Type,
    string? SearchText,
    string? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public interface IPoInvoiceRepository
{
    Task<PoInvoice?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default);

    Task<PoInvoice?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<PoInvoice> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoInvoiceSearchArgs args,
        CancellationToken cancellationToken = default);

    /// <summary>Sum of posted CN qty on a PO line against a specific INV DocNo.</summary>
    Task<decimal> SumPostedCnQtyOnInvLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invDocNo,
        string poNo,
        short poRelNo,
        short poLineNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    Task<bool> HasPostedCnReferencingInvAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invDocNo,
        CancellationToken cancellationToken = default);
}

public sealed class PoInvoiceRepository : IPoInvoiceRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(PoInvoice.DocNo),
        nameof(PoInvoice.DocDate),
        nameof(PoInvoice.Status),
        nameof(PoInvoice.VendorCode),
        nameof(PoInvoice.TotAmnt),
        nameof(PoInvoice.CreatedDate),
        nameof(PoInvoice.Type)
    };

    public async Task<PoInvoice?> LockForUpdateAsync(
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
        if (company.Length == 0 || branch.Length == 0 || no.Length == 0)
        {
            return null;
        }

        if (db.Database.IsSqlServer())
        {
            return await db.PoInvoices
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.POInvoice WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND DocNo = {no}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.PoInvoices
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == no,
                cancellationToken);
    }

    public Task<PoInvoice?> GetWithDetailsAsync(
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
        return db.PoInvoices
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == no,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<PoInvoice> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoInvoiceSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        if (!string.IsNullOrWhiteSpace(args.Type))
        {
            var type = args.Type.Trim().ToUpperInvariant();
            query = query.Where(x => x.Type == type);
        }

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
                || (x.InvNo != null && x.InvNo.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var sortField = AllowedSortFields.Contains(args.SortField ?? string.Empty)
            ? args.SortField!
            : nameof(PoInvoice.DocDate);

        IOrderedQueryable<PoInvoice> ordered = sortField.ToUpperInvariant() switch
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
            "TYPE" => args.SortDescending
                ? query.OrderByDescending(x => x.Type)
                : query.OrderBy(x => x.Type),
            _ => args.SortDescending
                ? query.OrderByDescending(x => x.DocDate).ThenByDescending(x => x.DocNo)
                : query.OrderBy(x => x.DocDate).ThenBy(x => x.DocNo)
        };

        var rows = await ordered.Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (rows, total);
    }

    public async Task<decimal> SumPostedCnQtyOnInvLineAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invDocNo,
        string poNo,
        short poRelNo,
        short poLineNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var inv = (invDocNo ?? string.Empty).Trim();
        var po = (poNo ?? string.Empty).Trim();
        var exclude = (excludeDocNo ?? string.Empty).Trim();

        var query = db.PoInvoiceDetails.AsNoTracking()
            .Where(d =>
                d.CompanyCode == company
                && d.BranchCode == branch
                && d.PoNo == po
                && d.PoRelNo == poRelNo
                && d.PoLineNo == poLineNo
                && d.Invoice.Type == "CN"
                && d.Invoice.Status == "POSTED"
                && d.Invoice.InvNo == inv);

        if (exclude.Length > 0)
        {
            query = query.Where(d => d.DocNo != exclude);
        }

        var sum = await query.SumAsync(d => (decimal?)d.Qty, cancellationToken);
        return sum ?? 0m;
    }

    public Task<bool> HasPostedCnReferencingInvAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invDocNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var inv = (invDocNo ?? string.Empty).Trim();
        return db.PoInvoices.AsNoTracking()
            .AnyAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.Type == "CN"
                    && x.Status == "POSTED"
                    && x.InvNo == inv,
                cancellationToken);
    }
}
