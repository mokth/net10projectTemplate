using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Numbering;

public sealed record DocumentNumberResult(string DocumentNumber, string? PrefixUsed);

public sealed class DocumentNumberingService : IDocumentNumberingService
{
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";
    private const int MaxRetries = 3;
    private const int MaxDocLength = 30;

    private readonly IInventoryTenantContext _tenant;

    public DocumentNumberingService(IInventoryTenantContext tenant)
    {
        _tenant = tenant;
    }

    public Task<DocumentNumberResult> NextAsync(
        AppDbContext db,
        string module,
        string extraPrefix,
        DateTime documentDate,
        DocumentNumberRequestMode requestMode,
        string currentDocNo,
        CancellationToken ct) =>
        IssueCoreAsync(db, module, extraPrefix, documentDate, requestMode, currentDocNo, ct);

    private async Task<DocumentNumberResult> IssueCoreAsync(
        AppDbContext db,
        string module,
        string extraPrefix,
        DateTime documentDate,
        DocumentNumberRequestMode requestMode,
        string currentDocNo,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var current = (currentDocNo ?? string.Empty).Trim();
        if (requestMode == DocumentNumberRequestMode.Edit
            && !string.Equals(current, "AUTO", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(current))
        {
            return new DocumentNumberResult(current, null);
        }

        var scope = _tenant.TryWriteScope()
            ?? throw new InvalidOperationException("Write scope (company, branch, location) is required for numbering.");

        var numCd = NormalizeNumCd(module, extraPrefix);
        var company = scope.CompanyCode;
        var branch = scope.BranchCode!;
        var location = Truncate(scope.LocationCode, 10);
        var userId = Truncate(scope.UserId, 10);
        var docDate = documentDate.Date;
        var isSqlServer = string.Equals(db.Database.ProviderName, SqlServerProvider, StringComparison.Ordinal);

        var hasDateRows = await db.AdSmNumDates.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd, ct);
        if (hasDateRows)
        {
            return await AllocateFromDateAsync(db, company, branch, location, userId, numCd, docDate, isSqlServer, ct);
        }

        var cont = await LoadContinuousAsync(db, company, branch, numCd, isSqlServer, forUpdate: true, ct);
        if (cont is null)
        {
            throw new DocumentNumberingNotConfiguredException(
                $"Numbering is not configured for {company}/{branch}/{numCd}.");
        }

        return await IssueContinuousAsync(db, cont, location, userId, isSqlServer, ct);
    }

    private static string NormalizeNumCd(string module, string extraPrefix)
    {
        var numCd = ((module ?? string.Empty) + (extraPrefix ?? string.Empty)).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(numCd))
        {
            throw new DocumentNumberingConfigurationException("NumCd is required.");
        }

        if (numCd.Length > 10)
        {
            throw new DocumentNumberingConfigurationException("NumCd must be at most 10 characters.");
        }

        return numCd;
    }

    private async Task<DocumentNumberResult> IssueContinuousAsync(
        AppDbContext db,
        AdSmNum row,
        string? location,
        string? userId,
        bool isSqlServer,
        CancellationToken ct)
    {
        ValidateContinuous(row);
        var issuedSeq = row.Seq;
        var docNo = DocumentNumberFormatter.FormatContinuous(row.Prefix, issuedSeq, row.TotLength);
        DocumentNumberFormatter.EnsureFitsMaxLength(docNo, MaxDocLength);

        await PersistContinuousIncrementAsync(db, row, location, userId, isSqlServer, ct);
        return new DocumentNumberResult(docNo, row.Prefix);
    }

    private static void ValidateContinuous(AdSmNum row)
    {
        var prefix = row.Prefix ?? string.Empty;
        if (prefix.Length > 10)
        {
            throw new DocumentNumberingConfigurationException("Prefix is too long.");
        }

        if (row.TotLength <= prefix.Length)
        {
            throw new DocumentNumberingConfigurationException("TotLength must be greater than Prefix length.");
        }

        if (row.Seq < 1)
        {
            throw new DocumentNumberingConfigurationException("Sequence must be at least 1.");
        }
    }

    private async Task<DocumentNumberResult> AllocateFromDateAsync(
        AppDbContext db,
        string company,
        string branch,
        string? location,
        string? userId,
        string numCd,
        DateTime docDate,
        bool isSqlServer,
        CancellationToken ct)
    {
        var docYear = (short)docDate.Year;
        var docMonth = (short)docDate.Month;

        // 1. Year=0 Month=0 continuous
        var y0m0 = await LoadDateRowAsync(db, company, branch, numCd, 0, 0, isSqlServer, forUpdate: true, ct);
        if (y0m0 is not null)
        {
            return await IssueExistingDateRowAsync(
                db, y0m0, docDate, DocumentNumberFormatter.DateMode.Continuous, location, userId, isSqlServer, ct);
        }

        // 2. Any Month=0 → yearly path
        var anyMonth0 = await db.AdSmNumDates.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd && x.Month == 0, ct);
        if (anyMonth0)
        {
            var yearRow = await LoadDateRowAsync(db, company, branch, numCd, docYear, 0, isSqlServer, forUpdate: true, ct);
            if (yearRow is not null)
            {
                return await IssueExistingDateRowAsync(
                    db, yearRow, docDate, DocumentNumberFormatter.DateMode.Yearly, location, userId, isSqlServer, ct);
            }

            var template = await LoadLatestMonth0TemplateAsync(db, company, branch, numCd, ct)
                ?? throw new DocumentNumberingNotConfiguredException(
                    $"Numbering is not configured for {company}/{branch}/{numCd}.");
            return await InsertNewPeriodWithRetryAsync(
                db, template, company, branch, location, userId, numCd, docYear, 0,
                docDate, DocumentNumberFormatter.DateMode.Yearly, isSqlServer, ct);
        }

        // 3. Exact year+month
        var monthRow = await LoadDateRowAsync(
            db, company, branch, numCd, docYear, docMonth, isSqlServer, forUpdate: true, ct);
        if (monthRow is not null)
        {
            return await IssueExistingDateRowAsync(
                db, monthRow, docDate, DocumentNumberFormatter.DateMode.Monthly, location, userId, isSqlServer, ct);
        }

        // 4. Missing period — latest template
        var latest = await LoadLatestTemplateAsync(db, company, branch, numCd, ct)
            ?? throw new DocumentNumberingNotConfiguredException(
                $"Numbering is not configured for {company}/{branch}/{numCd}.");

        var mode = latest.Year is > 0 && latest.Month is > 0
            ? DocumentNumberFormatter.DateMode.Monthly
            : DocumentNumberFormatter.DateMode.Continuous;

        return await InsertNewPeriodWithRetryAsync(
            db, latest, company, branch, location, userId, numCd, docYear, docMonth,
            docDate, mode, isSqlServer, ct);
    }

    private async Task<DocumentNumberResult> IssueExistingDateRowAsync(
        AppDbContext db,
        AdSmNumDate row,
        DateTime docDate,
        DocumentNumberFormatter.DateMode mode,
        string? location,
        string? userId,
        bool isSqlServer,
        CancellationToken ct)
    {
        ValidateDateRow(row);
        var seq = row.Seq!.Value;
        var tot = row.TotLength!.Value;
        var docNo = FormatDateDoc(row, seq, tot, docDate, mode);
        DocumentNumberFormatter.EnsureFitsMaxLength(docNo, MaxDocLength);

        await PersistDateIncrementAsync(db, row, location, userId, isSqlServer, ct);
        return new DocumentNumberResult(docNo, row.Prefix);
    }

    private async Task<DocumentNumberResult> InsertNewPeriodWithRetryAsync(
        AppDbContext db,
        AdSmNumDate template,
        string company,
        string branch,
        string? location,
        string? userId,
        string numCd,
        short year,
        short month,
        DateTime docDate,
        DocumentNumberFormatter.DateMode mode,
        bool isSqlServer,
        CancellationToken ct)
    {
        ValidateDateTemplate(template);

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Issue 1, persist Seq=2
            var docNo = FormatDateDoc(template, 1, template.TotLength!.Value, docDate, mode);
            DocumentNumberFormatter.EnsureFitsMaxLength(docNo, MaxDocLength);

            try
            {
                await InsertDatePeriodAsync(
                    db, template, company, branch, location, userId, numCd, year, month, nextSeq: 2, isSqlServer, ct);
                return new DocumentNumberResult(docNo, template.Prefix);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                var existing = await LoadDateRowAsync(
                    db, company, branch, numCd, year, month, isSqlServer, forUpdate: true, ct);
                if (existing is null)
                {
                    continue;
                }

                return await IssueExistingDateRowAsync(
                    db, existing, docDate, mode, location, userId, isSqlServer, ct);
            }
        }

        throw new DocumentNumberingConcurrencyException(
            "Unable to allocate document number due to a concurrent period insert.");
    }

    private static string FormatDateDoc(
        AdSmNumDate row,
        long seq,
        short totLength,
        DateTime docDate,
        DocumentNumberFormatter.DateMode mode)
    {
        if (!string.IsNullOrWhiteSpace(row.NumberingFormat))
        {
            return DocumentNumberFormatter.FormatTemplate(
                row.NumberingFormat, row.Prefix, seq, totLength, docDate);
        }

        return DocumentNumberFormatter.FormatDateMode(
            row.Prefix, seq, totLength, row.NumberingDelimeter, docDate, mode);
    }

    private static void ValidateDateRow(AdSmNumDate row)
    {
        ValidateDateTemplate(row);
        if (row.Seq is null || row.Seq < 1)
        {
            throw new DocumentNumberingConfigurationException("Sequence must be at least 1.");
        }

        if (row.Year is null || row.Year < 0 || row.Year > 2099)
        {
            throw new DocumentNumberingConfigurationException("Year is invalid.");
        }

        if (row.Month is null || row.Month < 0 || row.Month > 12)
        {
            throw new DocumentNumberingConfigurationException("Month is invalid.");
        }
    }

    private static void ValidateDateTemplate(AdSmNumDate row)
    {
        if (row.TotLength is null or <= 0)
        {
            throw new DocumentNumberingConfigurationException("TotLength must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(row.NumberingFormat)
            && !row.NumberingFormat.Contains("{1}", StringComparison.Ordinal))
        {
            throw new DocumentNumberingConfigurationException("NumberingFormat must contain {1}.");
        }

        var numCd = (row.NumCd ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(numCd) || numCd.Length > 10)
        {
            throw new DocumentNumberingConfigurationException("NumCd is invalid.");
        }
    }

    private static async Task<AdSmNum?> LoadContinuousAsync(
        AppDbContext db,
        string company,
        string branch,
        string numCd,
        bool isSqlServer,
        bool forUpdate,
        CancellationToken ct)
    {
        if (isSqlServer && forUpdate)
        {
            return await db.AdSmNums
                .FromSqlInterpolated($@"
SELECT * FROM AdSmNum WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE CompanyCode = {company} AND BranchCode = {branch} AND NumCd = {numCd}")
                .AsTracking()
                .SingleOrDefaultAsync(ct);
        }

        return await db.AdSmNums
            .SingleOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd, ct);
    }

    private static async Task<AdSmNumDate?> LoadDateRowAsync(
        AppDbContext db,
        string company,
        string branch,
        string numCd,
        short year,
        short month,
        bool isSqlServer,
        bool forUpdate,
        CancellationToken ct)
    {
        if (isSqlServer && forUpdate)
        {
            return await db.AdSmNumDates
                .FromSqlInterpolated($@"
SELECT * FROM AdSmNumDate WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE CompanyCode = {company} AND BranchCode = {branch} AND NumCd = {numCd}
  AND Year = {year} AND Month = {month}")
                .AsTracking()
                .SingleOrDefaultAsync(ct);
        }

        return await db.AdSmNumDates
            .SingleOrDefaultAsync(
                x => x.CompanyCode == company
                    && x.BranchCode == branch
                    && x.NumCd == numCd
                    && x.Year == year
                    && x.Month == month,
                ct);
    }

    private static Task<AdSmNumDate?> LoadLatestMonth0TemplateAsync(
        AppDbContext db, string company, string branch, string numCd, CancellationToken ct) =>
        db.AdSmNumDates.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd && x.Month == 0)
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .ThenByDescending(x => x.Uid)
            .FirstOrDefaultAsync(ct);

    private static Task<AdSmNumDate?> LoadLatestTemplateAsync(
        AppDbContext db, string company, string branch, string numCd, CancellationToken ct) =>
        db.AdSmNumDates.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd)
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .ThenByDescending(x => x.Uid)
            .FirstOrDefaultAsync(ct);

    private static async Task PersistContinuousIncrementAsync(
        AppDbContext db,
        AdSmNum row,
        string? location,
        string? userId,
        bool isSqlServer,
        CancellationToken ct)
    {
        var next = row.Seq + 1;
        var now = DateTime.UtcNow;
        if (isSqlServer)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE AdSmNum
SET Seq = {next}, LocationCode = {location}, Updated = {now}, UpdatedUID = {userId}
WHERE CompanyCode = {row.CompanyCode} AND BranchCode = {row.BranchCode} AND NumCd = {row.NumCd}", ct);
            return;
        }

        row.Seq = next;
        row.LocationCode = location;
        row.Updated = now;
        row.UpdatedUID = userId;
        await db.SaveChangesAsync(ct);
    }

    private static async Task PersistDateIncrementAsync(
        AppDbContext db,
        AdSmNumDate row,
        string? location,
        string? userId,
        bool isSqlServer,
        CancellationToken ct)
    {
        var next = row.Seq!.Value + 1;
        var now = DateTime.UtcNow;
        if (isSqlServer)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE AdSmNumDate
SET Seq = {next}, LocationCode = {location}, Updated = {now}, UserID = {userId}
WHERE [uid] = {row.Uid}", ct);
            return;
        }

        row.Seq = next;
        row.LocationCode = location;
        row.Updated = now;
        row.UserID = userId;
        await db.SaveChangesAsync(ct);
    }

    private static async Task InsertDatePeriodAsync(
        AppDbContext db,
        AdSmNumDate template,
        string company,
        string branch,
        string? location,
        string? userId,
        string numCd,
        short year,
        short month,
        long nextSeq,
        bool isSqlServer,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (isSqlServer)
        {
            // HOLDLOCK range via existence check under serializable-ish lock on unique key path
            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO AdSmNumDate (
    CompanyCode, BranchCode, LocationCode, Year, Month, NumCd, NumDes,
    TotLength, Prefix, Seq, Created, Updated, UserID, NumberingDelimeter, NumberingFormat)
VALUES (
    {company}, {branch}, {location}, {year}, {month}, {numCd}, {template.NumDes},
    {template.TotLength}, {template.Prefix}, {nextSeq}, {now}, {now}, {userId},
    {template.NumberingDelimeter}, {template.NumberingFormat})", ct);
            return;
        }

        db.AdSmNumDates.Add(new AdSmNumDate
        {
            CompanyCode = company,
            BranchCode = branch,
            LocationCode = location,
            Year = year,
            Month = month,
            NumCd = numCd,
            NumDes = template.NumDes,
            TotLength = template.TotLength,
            Prefix = template.Prefix,
            Seq = nextSeq,
            Created = now,
            Updated = now,
            UserID = userId,
            NumberingDelimeter = template.NumberingDelimeter,
            NumberingFormat = template.NumberingFormat,
            RowVersion = [0]
        });
        await db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is SqlException sql && sql.Number is 2601 or 2627)
            {
                return true;
            }

            if (e is DbUpdateException)
            {
                var message = e.InnerException?.Message ?? e.Message;
                if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("unique", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("2627", StringComparison.Ordinal)
                    || message.Contains("2601", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }
}
