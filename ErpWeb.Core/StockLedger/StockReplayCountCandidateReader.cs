using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayCountCandidate(
    IvOnHandBalanceRow Balance,
    decimal HistoricalQty);

/// <summary>
/// Enumerates count piles using their active V2 quantity immediately before a
/// new end-of-day count. Static scope is applied in SQL; quantity and paging
/// are applied only after strict historical reconstruction.
/// </summary>
public static class StockReplayCountCandidateReader
{
    private const int BatchSize = 500;

    public static async Task<(IReadOnlyList<StockReplayCountCandidate> Rows, int TotalCount)> ReadAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IvStockCountScope scope,
        DateTime countDate,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode))
            throw new ArgumentException("A count tenant is required.");
        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(skip));
        if (take <= 0)
            throw new ArgumentOutOfRangeException(nameof(take));

        var company = companyCode.Trim();
        var branch = branchCode.Trim();
        var asOf = StockReplayEventKey.CountEndOfDay(countDate);
        var epochCount = await db.StockLedgerEpochs.AsNoTracking()
            .CountAsync(e => e.CompanyCode == company && e.BranchCode == branch
                && e.Status == StockLedgerEpochStatuses.Active
                && e.Version == 2 && e.EffectiveFrom <= asOf,
                cancellationToken);
        if (epochCount != 1)
            throw new StockLedgerException(new StockLedgerError(
                StockLedgerErrorCodes.LedgerMismatch,
                "Historical count candidates require one active V2 epoch on the count date."));
        var query =
            from bal in db.IvBalLocs.AsNoTracking()
            join sm in db.IvStockMasters.AsNoTracking()
                on new { bal.CompanyCode, bal.ICode } equals new { sm.CompanyCode, sm.ICode }
            join lot in db.IvLots.AsNoTracking()
                on bal.LotId equals lot.Id into lots
            from lot in lots.DefaultIfEmpty()
            where bal.CompanyCode == company && bal.BranchCode == branch && sm.StockControl
            select new { bal, sm, lot };

        if (!scope.IncludeInactive)
            query = query.Where(x => x.sm.IsActive);
        var statuses = scope.Statuses.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();
        query = statuses.Length > 0
            ? query.Where(x => statuses.Contains(x.bal.IStatus.ToUpper()))
            : query.Where(x => x.bal.IStatus.ToUpper() != IvStockCountScope.ScrapStatus);
        if (!string.IsNullOrWhiteSpace(scope.WHCode))
        {
            var warehouse = scope.WHCode.Trim();
            query = query.Where(x => x.bal.WhCode == warehouse);
        }
        if (!string.IsNullOrWhiteSpace(scope.LocCode))
        {
            var location = scope.LocCode.Trim();
            query = query.Where(x => x.bal.LocCode == location);
        }
        if (!string.IsNullOrWhiteSpace(scope.IClassCode))
        {
            var itemClass = scope.IClassCode.Trim();
            query = query.Where(x => x.sm.IClassCode == itemClass);
        }
        if (!string.IsNullOrWhiteSpace(scope.ISubClassCode))
        {
            var subClass = scope.ISubClassCode.Trim();
            query = query.Where(x => x.sm.ISubClassCode == subClass);
        }
        if (!string.IsNullOrWhiteSpace(scope.IType))
        {
            var itemType = scope.IType.Trim();
            query = query.Where(x => x.sm.IType == itemType);
        }
        var itemCodes = scope.ICodes.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim()).Distinct().ToArray();
        if (itemCodes.Length > 0)
            query = query.Where(x => itemCodes.Contains(x.bal.ICode));

        var ordered = query.OrderBy(x => x.bal.ICode)
            .ThenBy(x => x.bal.WhCode)
            .ThenBy(x => x.bal.LocCode)
            .ThenBy(x => x.bal.LotNo)
            .ThenBy(x => x.bal.Id)
            .Select(x => new IvOnHandBalanceRow
            {
                Id = x.bal.Id, ICode = x.bal.ICode, IDesc = x.sm.IDesc,
                WhCode = x.bal.WhCode, LocCode = x.bal.LocCode,
                LotNo = x.bal.LotNo, StdQty = x.bal.StdQty,
                StdUom = x.bal.StdUom ?? x.sm.StdUom,
                IStatus = x.bal.IStatus,
                ExpiryDate = x.lot != null ? x.lot.ExpiryDate : null,
                TransDate = x.bal.TransDate,
                IClassCode = x.sm.IClassCode,
                LotControl = x.sm.LotControl,
                PurchasePrice = x.sm.PurchasePrice,
                LotId = x.bal.LotId
            });

        var results = new List<StockReplayCountCandidate>();
        var total = 0;
        var offset = 0;
        while (true)
        {
            var page = await ordered.Skip(offset).Take(BatchSize).ToArrayAsync(cancellationToken);
            if (page.Length == 0)
                break;
            var quantities = await StockReplayPhysicalAsOfReader.ReadAsync(db,
                company, branch, page.Select(x => x.Id).ToArray(), asOf, cancellationToken);
            foreach (var row in page)
            {
                var historicalQty = quantities[row.Id].AsOfBaseQty;
                if (!scope.IncludeZeroQty && historicalQty == 0m)
                    continue;
                if (total >= skip && (long)total < (long)skip + take)
                    results.Add(new StockReplayCountCandidate(row, historicalQty));
                total++;
            }
            offset += page.Length;
            if (page.Length < BatchSize)
                break;
        }
        return (results, total);
    }
}
