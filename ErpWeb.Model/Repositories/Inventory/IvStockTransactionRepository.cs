using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories.Inventory;

public interface IIvStockTransactionRepository
{
    Task InsertAsync(AppDbContext db, IvTrxBatch batch, CancellationToken cancellationToken = default);

    Task<IvTrxBatch?> GetByBatchNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<IvTrxBatchListRow> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvTrxBatchSearchArgs args,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(AppDbContext db, IvTrxBatch batch, CancellationToken cancellationToken = default);

    Task<bool> DeleteNewAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default);
}

public sealed class IvStockTransactionRepository : IIvStockTransactionRepository
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> AllowedSortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvTrxBatchListRow.BatchNo),
        nameof(IvTrxBatchListRow.TrxDtTime),
        nameof(IvTrxBatchListRow.BatchStatus),
        nameof(IvTrxBatchListRow.RefNo),
        nameof(IvTrxBatchListRow.CreatedDate),
        nameof(IvTrxBatchListRow.TotalAmount),
        nameof(IvTrxBatchListRow.LineCount)
    };

    public Task InsertAsync(AppDbContext db, IvTrxBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(batch);
        db.IvTrxBatches.Add(batch);
        return Task.CompletedTask;
    }

    public Task<IvTrxBatch?> GetByBatchNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        return db.IvTrxBatches
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.BatchNo == batchNo,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<IvTrxBatchListRow> Rows, int TotalCount)> SearchPagedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvTrxBatchSearchArgs args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(args);

        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var trxType = (args.TrxType ?? string.Empty).Trim();
        var skip = Math.Max(0, args.Skip);
        var take = Math.Clamp(args.Take <= 0 ? 20 : args.Take, 1, MaxPageSize);

        var query = db.IvTrxBatches.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.TrxType == trxType);

        if (!string.IsNullOrWhiteSpace(args.BatchStatus))
        {
            var status = args.BatchStatus.Trim();
            query = query.Where(x => x.BatchStatus == status);
        }

        if (args.DateFrom is DateTime from)
        {
            var fromDate = from.Date;
            query = query.Where(x => x.TrxDtTime >= fromDate);
        }

        if (args.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            query = query.Where(x => x.TrxDtTime < toExclusive);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            var term = args.SearchText.Trim();
            if (int.TryParse(term, out var batchNo))
            {
                query = query.Where(x =>
                    x.BatchNo == batchNo
                    || (x.RefNo != null && x.RefNo.Contains(term))
                    || (x.Remarks != null && x.Remarks.Contains(term)));
            }
            else
            {
                query = query.Where(x =>
                    (x.RefNo != null && x.RefNo.Contains(term))
                    || (x.Remarks != null && x.Remarks.Contains(term)));
            }
        }

        var total = await query.CountAsync(cancellationToken);

        // Avoid projecting SUM(qty * price) into decimal: SQL Server can return float for that
        // aggregate, which throws InvalidCastException on materialization.
        var page = await ApplySort(query, args.SortField, args.SortDescending)
            .Skip(skip)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.BatchNo,
                x.TrxDtTime,
                x.BatchStatus,
                x.RefNo,
                x.Remarks,
                LineCount = x.Details.Count,
                x.CreatedDate,
                x.CreatedBy
            })
            .ToListAsync(cancellationToken);

        var ids = page.Select(x => x.Id).ToList();
        var amountByBatchId = new Dictionary<int, decimal>();
        if (ids.Count > 0)
        {
            var detailRows = await db.IvTrxBatchDetails.AsNoTracking()
                .Where(d => ids.Contains(d.BatchId))
                .Select(d => new { d.BatchId, d.TrxType, d.ToStdQty, d.FrStdQty, d.UnitPrice })
                .ToListAsync(cancellationToken);

            amountByBatchId = detailRows
                .GroupBy(d => d.BatchId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(d => QtyForAmount(d.TrxType, d.ToStdQty, d.FrStdQty) * (d.UnitPrice ?? 0m)));
        }

        var rows = page.Select(x => new IvTrxBatchListRow
        {
            Id = x.Id,
            BatchNo = x.BatchNo,
            TrxDtTime = x.TrxDtTime,
            BatchStatus = x.BatchStatus,
            RefNo = x.RefNo,
            Remarks = x.Remarks,
            LineCount = x.LineCount,
            TotalAmount = amountByBatchId.GetValueOrDefault(x.Id),
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy
        }).ToList();

        return (rows, total);
    }

    public async Task<bool> UpdateAsync(
        AppDbContext db,
        IvTrxBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(batch);

        if (!string.Equals(batch.BatchStatus, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        db.IvTrxBatches.Update(batch);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteNewAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var batch = await GetByBatchNoAsync(db, companyCode, branchCode, batchNo, cancellationToken);
        if (batch is null)
        {
            return false;
        }

        if (!string.Equals(batch.BatchStatus, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        db.IvTrxBatchDetails.RemoveRange(batch.Details);
        db.IvTrxBatches.Remove(batch);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static IQueryable<IvTrxBatch> ApplySort(
        IQueryable<IvTrxBatch> query,
        string? sortField,
        bool descending)
    {
        var field = string.IsNullOrWhiteSpace(sortField) || !AllowedSortFields.Contains(sortField)
            ? nameof(IvTrxBatchListRow.BatchNo)
            : sortField;

        return (field, descending) switch
        {
            (nameof(IvTrxBatchListRow.TrxDtTime), true) => query.OrderByDescending(x => x.TrxDtTime).ThenByDescending(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.TrxDtTime), false) => query.OrderBy(x => x.TrxDtTime).ThenBy(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.BatchStatus), true) => query.OrderByDescending(x => x.BatchStatus).ThenByDescending(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.BatchStatus), false) => query.OrderBy(x => x.BatchStatus).ThenBy(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.RefNo), true) => query.OrderByDescending(x => x.RefNo).ThenByDescending(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.RefNo), false) => query.OrderBy(x => x.RefNo).ThenBy(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.CreatedDate), true) => query.OrderByDescending(x => x.CreatedDate).ThenByDescending(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.CreatedDate), false) => query.OrderBy(x => x.CreatedDate).ThenBy(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.TotalAmount), true) => query
                .OrderByDescending(x => x.Details.Sum(d =>
                    (d.TrxType == "MI" ? (d.FrStdQty ?? 0m) : (d.ToStdQty ?? 0m)) * (d.UnitPrice ?? 0m)))
                .ThenByDescending(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.TotalAmount), false) => query
                .OrderBy(x => x.Details.Sum(d =>
                    (d.TrxType == "MI" ? (d.FrStdQty ?? 0m) : (d.ToStdQty ?? 0m)) * (d.UnitPrice ?? 0m)))
                .ThenBy(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.LineCount), true) => query.OrderByDescending(x => x.Details.Count).ThenByDescending(x => x.BatchNo),
            (nameof(IvTrxBatchListRow.LineCount), false) => query.OrderBy(x => x.Details.Count).ThenBy(x => x.BatchNo),
            (_, true) => query.OrderByDescending(x => x.BatchNo),
            _ => query.OrderBy(x => x.BatchNo)
        };
    }

    private static decimal QtyForAmount(string? trxType, decimal? toStdQty, decimal? frStdQty) =>
        string.Equals(trxType, "MI", StringComparison.OrdinalIgnoreCase)
            ? (frStdQty ?? 0m)
            : (toStdQty ?? 0m);
}
