using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Sales;

public sealed record SaCdnSearchArgs(
    string Type,
    string? SearchText,
    string? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public interface ISaCdnRepository
{
    Task<SaCdn?> LockForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default);

    Task<SaCdn?> GetWithDetailsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SaCdn> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaCdnSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<decimal>> ListOtherCnTotAmntsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R9: the other credit notes (NEW or POSTED) holding part of the invoice's remaining balance,
    /// with their document numbers and statuses so the operator can be told <i>which</i> drafts
    /// reserved the balance — not merely that the total was exceeded.
    /// </summary>
    Task<IReadOnlyList<SaCdnReservationRow>> ListOtherCreditNotesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default);
}

/// <summary>A credit note reserving part of a posted invoice's remaining balance (R9).</summary>
public sealed class SaCdnReservationRow
{
    public string DocNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotAmnt { get; init; }

    public bool IsDraft => string.Equals(Status, SaCdnStatuses.New, StringComparison.OrdinalIgnoreCase);
}

public sealed class SaCdnRepository : ISaCdnRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(SaCdn.DocNo),
        nameof(SaCdn.DocDate),
        nameof(SaCdn.Status),
        nameof(SaCdn.CustCode),
        nameof(SaCdn.TotAmnt),
        nameof(SaCdn.CreatedDate),
        nameof(SaCdn.Type)
    };

    public async Task<SaCdn?> LockForUpdateAsync(
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
            return await db.SaCdns
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.SaCDN WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND DocNo = {no}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.SaCdns
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == no,
                cancellationToken);
    }

    public Task<SaCdn?> GetWithDetailsAsync(
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
        return db.SaCdns
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.DocNo == no,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<SaCdn> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        SaCdnSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var type = (args.Type ?? string.Empty).Trim().ToUpperInvariant();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.SaCdns.AsNoTracking()
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
                || (x.CustCode != null && x.CustCode.Contains(term))
                || (x.CustName != null && x.CustName.Contains(term))
                || (x.InvNo != null && x.InvNo.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);

        var sortField = AllowedSortFields.Contains(args.SortField ?? string.Empty)
            ? args.SortField!
            : nameof(SaCdn.DocDate);

        IOrderedQueryable<SaCdn> ordered = sortField.ToUpperInvariant() switch
        {
            "DOCNO" => args.SortDescending
                ? query.OrderByDescending(x => x.DocNo)
                : query.OrderBy(x => x.DocNo),
            "STATUS" => args.SortDescending
                ? query.OrderByDescending(x => x.Status)
                : query.OrderBy(x => x.Status),
            "CUSTCODE" => args.SortDescending
                ? query.OrderByDescending(x => x.CustCode)
                : query.OrderBy(x => x.CustCode),
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

        var query = db.SaCdns.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.Type == SaCdnTypes.CreditNote
                && x.InvNo == invoice
                && (x.Status == SaCdnStatuses.New || x.Status == SaCdnStatuses.Posted));

        if (exclude.Length > 0)
        {
            query = query.Where(x => x.DocNo != exclude);
        }

        return await query.Select(x => x.TotAmnt).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SaCdnReservationRow>> ListOtherCreditNotesAsync(
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

        var query = db.SaCdns.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.Type == SaCdnTypes.CreditNote
                && x.InvNo == invoice
                && (x.Status == SaCdnStatuses.New || x.Status == SaCdnStatuses.Posted));

        if (exclude.Length > 0)
        {
            query = query.Where(x => x.DocNo != exclude);
        }

        return await query
            .OrderBy(x => x.Status)
            .ThenBy(x => x.DocNo)
            .Select(x => new SaCdnReservationRow
            {
                DocNo = x.DocNo,
                Status = x.Status,
                TotAmnt = x.TotAmnt
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Status constants shared with Core (duplicated string literals for Model layer).</summary>
public static class SaCdnStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
}

public static class SaCdnTypes
{
    public const string CreditNote = "CN";
    public const string DebitNote = "DN";
}
