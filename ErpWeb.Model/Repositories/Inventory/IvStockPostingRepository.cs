using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Inventory;

public interface IIvStockPostingRepository
{
    Task<IvTrxBatch?> LockBatchForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvTrxBatchDetail>> LoadDetailsForBatchAsync(
        AppDbContext db,
        int batchId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IvStockMaster>> LockStockMastersAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<string> iCodes,
        CancellationToken cancellationToken = default);

    IReadOnlyList<IvStockSliceKey> GetOrderedStockSlices(IEnumerable<IvStockSliceKey> slices);

    Task<IReadOnlyDictionary<IvStockSliceKey, IvBalLoc>> LockBalanceSlicesAsync(
        AppDbContext db,
        IReadOnlyList<IvStockSliceKey> orderedSlices,
        CancellationToken cancellationToken = default);

    Task<IvLot> FindOrCreateLotAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        string lotNo,
        string? sourceType,
        string? sourceDocNo,
        DateTime? receiptDate,
        DateTime? expiryDate,
        string? userId,
        CancellationToken cancellationToken = default);

    Task<IvBalLoc> FindOrCreateBalLocAsync(
        AppDbContext db,
        IvStockSliceKey slice,
        int? lotId,
        string? stdUom,
        string? userId,
        DateTime? transDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IvTrxHistory>> LoadHistoryForBatchAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<bool> HistoryExistsForBatchAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default);

    void AddHistory(AppDbContext db, IvTrxHistory history);

    void RemoveHistory(AppDbContext db, IEnumerable<IvTrxHistory> rows);

    Task<IvBalLocLockResult?> LockBalLocByIdForTenantAsync(
        AppDbContext db,
        int id,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default);

    Task<int> DecreaseBalLocQtyAsync(
        AppDbContext db,
        int id,
        string companyCode,
        string branchCode,
        decimal qty,
        DateTime? transDate,
        CancellationToken cancellationToken = default);

    Task<int> IncreaseBalLocQtyAsync(
        AppDbContext db,
        int id,
        string companyCode,
        string branchCode,
        decimal qty,
        DateTime? transDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sole production writer of <see cref="IvBalLoc.StdQty"/>.
/// SQL Server uses UPDLOCK/HOLDLOCK; SQLite uses tracked loads for business-logic tests only.
/// </summary>
public sealed class IvStockPostingRepository : IIvStockPostingRepository
{
    public async Task<IvTrxBatch?> LockBatchForUpdateAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();

        if (db.Database.IsSqlServer())
        {
            return await db.IvTrxBatches
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND BranchCode = {branch}
  AND BatchNo = {batchNo}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.IvTrxBatches
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo,
                cancellationToken);
    }

    public async Task<IReadOnlyList<IvTrxBatchDetail>> LoadDetailsForBatchAsync(
        AppDbContext db,
        int batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.IvTrxBatchDetails
            .Where(x => x.BatchId == batchId)
            .OrderBy(x => x.TrxLineNo)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IvStockMaster>> LockStockMastersAsync(
        AppDbContext db,
        string companyCode,
        IEnumerable<string> iCodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var codes = iCodes
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count == 0)
        {
            return new Dictionary<string, IvStockMaster>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, IvStockMaster>(StringComparer.OrdinalIgnoreCase);

        if (db.Database.IsSqlServer())
        {
            foreach (var code in codes)
            {
                var row = await db.IvStockMasters
                    .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvStockMaster WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company}
  AND ICode = {code}")
                    .AsTracking()
                    .FirstOrDefaultAsync(cancellationToken);
                if (row is not null)
                {
                    result[row.ICode] = row;
                }
            }

            return result;
        }

        var rows = await db.IvStockMasters
            .Where(x => x.CompanyCode == company && codes.Contains(x.ICode))
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            result[row.ICode] = row;
        }

        return result;
    }

    public IReadOnlyList<IvStockSliceKey> GetOrderedStockSlices(IEnumerable<IvStockSliceKey> slices) =>
        slices
            .Distinct()
            .OrderBy(x => x)
            .ToList();

    public async Task<IReadOnlyDictionary<IvStockSliceKey, IvBalLoc>> LockBalanceSlicesAsync(
        AppDbContext db,
        IReadOnlyList<IvStockSliceKey> orderedSlices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(orderedSlices);

        var map = new Dictionary<IvStockSliceKey, IvBalLoc>();
        foreach (var slice in orderedSlices)
        {
            var row = await LockBalLocExactAsync(db, slice, forUpdate: true, cancellationToken);
            if (row is not null)
            {
                map[slice] = row;
            }
        }

        return map;
    }

    public async Task<IvLot> FindOrCreateLotAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        string lotNo,
        string? sourceType,
        string? sourceDocNo,
        DateTime? receiptDate,
        DateTime? expiryDate,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var code = (iCode ?? string.Empty).Trim();
        var lot = (lotNo ?? string.Empty).Trim();
        if (lot.Length == 0)
        {
            throw new InvalidOperationException("Lot number is required for lot find/create.");
        }

        var existing = await LockLotExactAsync(db, company, code, lot, forUpdate: true, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTime.UtcNow;
        var entity = new IvLot
        {
            CompanyCode = company,
            ICode = code,
            LotNo = lot,
            SourceType = sourceType,
            SourceDocNo = sourceDocNo,
            ReceiptDate = receiptDate,
            ExpiryDate = expiryDate,
            IsActive = true,
            CreatedDate = now,
            CreatedBy = Truncate(userId, 10)
        };

        db.IvLots.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return entity;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(entity).State = EntityState.Detached;
            var raced = await LockLotExactAsync(db, company, code, lot, forUpdate: true, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return raced;
        }
    }

    public async Task<IvBalLoc> FindOrCreateBalLocAsync(
        AppDbContext db,
        IvStockSliceKey slice,
        int? lotId,
        string? stdUom,
        string? userId,
        DateTime? transDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var existing = await LockBalLocExactAsync(db, slice, forUpdate: true, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTime.UtcNow;
        var entity = new IvBalLoc
        {
            CompanyCode = slice.CompanyCode,
            BranchCode = slice.BranchCode,
            ICode = slice.ICode,
            WhCode = slice.WhCode,
            LocCode = slice.LocCode,
            LotNo = slice.LotNo,
            IStatus = slice.IStatus,
            LotId = lotId,
            StdQty = 0m,
            StdUom = Truncate(stdUom, 10),
            TransDate = transDate,
            CreatedDate = now,
            CreatedBy = Truncate(userId, 10)
        };

        db.IvBalLocs.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return entity;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(entity).State = EntityState.Detached;
            var raced = await LockBalLocExactAsync(db, slice, forUpdate: true, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return raced;
        }
    }

    public async Task<IReadOnlyList<IvTrxHistory>> LoadHistoryForBatchAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        return await db.IvTrxHistories
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo)
            .OrderBy(x => x.TrxLineNo)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> HistoryExistsForBatchAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        return db.IvTrxHistories.AnyAsync(
            x => x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo,
            cancellationToken);
    }

    public void AddHistory(AppDbContext db, IvTrxHistory history)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(history);
        db.IvTrxHistories.Add(history);
    }

    public void RemoveHistory(AppDbContext db, IEnumerable<IvTrxHistory> rows)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);
        db.IvTrxHistories.RemoveRange(rows);
    }

    public async Task<IvBalLocLockResult?> LockBalLocByIdForTenantAsync(
        AppDbContext db,
        int id,
        string companyCode,
        string branchCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        if (id <= 0)
        {
            return null;
        }

        IvBalLoc? row;
        if (db.Database.IsSqlServer())
        {
            row = await db.IvBalLocs
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvBalLoc WITH (UPDLOCK, HOLDLOCK)
WHERE ID = {id}
  AND CompanyCode = {company}
  AND BranchCode = {branch}")
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            row = await db.IvBalLocs
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == id && x.CompanyCode == company && x.BranchCode == branch,
                    cancellationToken);
        }

        return row is null
            ? null
            : new IvBalLocLockResult
            {
                Id = row.Id,
                CompanyCode = row.CompanyCode,
                BranchCode = row.BranchCode,
                ICode = row.ICode,
                WhCode = row.WhCode,
                LocCode = row.LocCode,
                LotNo = row.LotNo,
                IStatus = row.IStatus,
                StdQty = row.StdQty,
                StdUom = row.StdUom,
                LotId = row.LotId
            };
    }

    public async Task<int> DecreaseBalLocQtyAsync(
        AppDbContext db,
        int id,
        string companyCode,
        string branchCode,
        decimal qty,
        DateTime? transDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var now = DateTime.UtcNow;
        if (db.Database.IsSqlServer())
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE dbo.IvBalLoc
SET StdQty = StdQty - {qty},
    TransDate = {transDate},
    Updated = {now}
WHERE ID = {id}
  AND CompanyCode = {company}
  AND BranchCode = {branch}
  AND StdQty >= {qty}", cancellationToken);
        }

        // SQLite / tests: mutate then detach so SaveChanges cannot overwrite StdQty later.
        var row = await db.IvBalLocs
            .FirstOrDefaultAsync(
                x => x.Id == id
                     && x.CompanyCode == company
                     && x.BranchCode == branch
                     && x.StdQty >= qty,
                cancellationToken);
        if (row is null)
        {
            return 0;
        }

        row.StdQty -= qty;
        row.TransDate = transDate;
        row.ModifiedDate = now;
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(row).State = EntityState.Detached;
        return 1;
    }

    public async Task<int> IncreaseBalLocQtyAsync(
        AppDbContext db,
        int id,
        string companyCode,
        string branchCode,
        decimal qty,
        DateTime? transDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var now = DateTime.UtcNow;
        if (db.Database.IsSqlServer())
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE dbo.IvBalLoc
SET StdQty = StdQty + {qty},
    TransDate = {transDate},
    Updated = {now}
WHERE ID = {id}
  AND CompanyCode = {company}
  AND BranchCode = {branch}", cancellationToken);
        }

        var row = await db.IvBalLocs
            .FirstOrDefaultAsync(
                x => x.Id == id && x.CompanyCode == company && x.BranchCode == branch,
                cancellationToken);
        if (row is null)
        {
            return 0;
        }

        row.StdQty += qty;
        row.TransDate = transDate;
        row.ModifiedDate = now;
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(row).State = EntityState.Detached;
        return 1;
    }

    private static async Task<IvBalLoc?> LockBalLocExactAsync(
        AppDbContext db,
        IvStockSliceKey slice,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            // Exact unique-key lookup + HOLDLOCK enables key-range protection when the row is missing.
            // Hint is a fixed allow-list string (not user input).
            if (forUpdate)
            {
                return await db.IvBalLocs
                    .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvBalLoc WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {slice.CompanyCode}
  AND BranchCode = {slice.BranchCode}
  AND ICode = {slice.ICode}
  AND WHCode = {slice.WhCode}
  AND LocCode = {slice.LocCode}
  AND LotNo = {slice.LotNo}
  AND IStatus = {slice.IStatus}")
                    .AsTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }

            return await db.IvBalLocs
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvBalLoc WITH (HOLDLOCK)
WHERE CompanyCode = {slice.CompanyCode}
  AND BranchCode = {slice.BranchCode}
  AND ICode = {slice.ICode}
  AND WHCode = {slice.WhCode}
  AND LocCode = {slice.LocCode}
  AND LotNo = {slice.LotNo}
  AND IStatus = {slice.IStatus}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.IvBalLocs
            .FirstOrDefaultAsync(
                x => x.CompanyCode == slice.CompanyCode
                     && x.BranchCode == slice.BranchCode
                     && x.ICode == slice.ICode
                     && x.WhCode == slice.WhCode
                     && x.LocCode == slice.LocCode
                     && x.LotNo == slice.LotNo
                     && x.IStatus == slice.IStatus,
                cancellationToken);
    }

    private static async Task<IvLot?> LockLotExactAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        string lotNo,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            if (forUpdate)
            {
                return await db.IvLots
                    .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvLot WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {companyCode}
  AND ICode = {iCode}
  AND LotNo = {lotNo}")
                    .AsTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }

            return await db.IvLots
                .FromSqlInterpolated($@"
SELECT *
FROM dbo.IvLot WITH (HOLDLOCK)
WHERE CompanyCode = {companyCode}
  AND ICode = {iCode}
  AND LotNo = {lotNo}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.IvLots
            .FirstOrDefaultAsync(
                x => x.CompanyCode == companyCode && x.ICode == iCode && x.LotNo == lotNo,
                cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sql)
        {
            return sql.Number is 2601 or 2627;
        }

        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("unique", StringComparison.OrdinalIgnoreCase)
               || message.Contains("2627", StringComparison.Ordinal)
               || message.Contains("2601", StringComparison.Ordinal);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
