using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayCountPreviewLine(
    int LineId,
    short LineNumber,
    int BalLocId,
    decimal GeneratedSystemQty,
    decimal HistoricalPreCountQty,
    decimal LiveQty,
    decimal? PhysicalQty,
    decimal? SignedAdjustmentQty,
    decimal? ProjectedFinalQty,
    bool IsGeneratedSnapshotStale);

public sealed record StockReplayCountSheetPreview(
    int CountHeaderId,
    DateTime CountDate,
    IReadOnlyList<StockReplayCountPreviewLine> Lines)
{
    public decimal TotalIncrease => Lines.Sum(l => Math.Max(0m, l.SignedAdjustmentQty ?? 0m));
    public decimal TotalDecrease => Lines.Sum(l => Math.Max(0m, -(l.SignedAdjustmentQty ?? 0m)));
}

/// <summary>
/// Read-only V2 count preview. A counted line is projected through its active
/// later physical tail; an uncounted line is read at the same end-of-day key.
/// Neither this snapshot nor its variance authorizes a posting without root
/// validation and financial replay.
/// </summary>
public static class StockReplayCountSheetPreviewReader
{
    public static async Task<StockReplayCountSheetPreview> ReadAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int countHeaderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode)
            || countHeaderId <= 0)
            throw new ArgumentException("A count tenant and header ID are required.");
        var company = companyCode.Trim();
        var branch = branchCode.Trim();
        var header = await db.IvStockCountHdrs.AsNoTracking()
            .SingleOrDefaultAsync(h => h.Id == countHeaderId
                && h.CompanyCode == company && h.BranchCode == branch,
                cancellationToken);
        if (header is null || header.CountDate != header.CountDate.Date
            || header.Status is not (IvStockCountStatuses.Counted
                or IvStockCountStatuses.RolledBack))
            throw Invalid("The count sheet is not an eligible V2 preview.");

        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countHeaderId)
            .OrderBy(l => l.LineNumber).ThenBy(l => l.Id)
            .ToArrayAsync(cancellationToken);
        if (lines.Length == 0 || lines.Any(l => l.BalLocId <= 0)
            || lines.Select(l => l.BalLocId).Distinct().Count() != lines.Length)
            throw Invalid("The count sheet has no unique positive pile identities.");
        var ids = lines.Select(l => l.BalLocId).ToArray();
        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == company && b.BranchCode == branch
                && ids.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (balances.Count != lines.Length)
            throw Invalid("A counted pile no longer belongs to the sheet tenant.");
        foreach (var line in lines)
            if (!Matches(line, balances[line.BalLocId]))
                throw Invalid($"Count line {line.Id} no longer matches its item, warehouse, location, lot and status.");

        var uncountedIds = lines.Where(l => l.PhysicalQty is null)
            .Select(l => l.BalLocId).ToArray();
        var uncounted = await StockReplayPhysicalAsOfReader.ReadAsync(db,
            company, branch, uncountedIds,
            StockReplayEventKey.CountEndOfDay(header.CountDate), cancellationToken);
        var previews = new List<StockReplayCountPreviewLine>(lines.Length);
        foreach (var line in lines)
        {
            decimal historical;
            decimal? adjustment = null;
            decimal? projectedFinal = null;
            if (line.PhysicalQty is not null)
            {
                var plan = await StockReplayPhysicalPlanner.BuildCountInsertionAsync(
                    db, company, branch, line.Id, cancellationToken);
                historical = plan.Projection.HistoricalQuantityBeforeCandidate;
                adjustment = plan.Projection.Candidate.SignedDelta;
                projectedFinal = plan.Projection.FinalQuantity;
            }
            else
            {
                historical = uncounted[line.BalLocId].AsOfBaseQty;
            }
            previews.Add(new StockReplayCountPreviewLine(line.Id, line.LineNumber,
                line.BalLocId, line.SystemQty, historical,
                balances[line.BalLocId].StdQty, line.PhysicalQty,
                adjustment, projectedFinal,
                line.SystemQty != historical));
        }
        return new StockReplayCountSheetPreview(header.Id, header.CountDate, previews);
    }

    internal static bool Matches(IvStockCountLine line, IvBalLoc balance) =>
        Equal(balance.ICode, line.ICode)
        && Equal(balance.WhCode, line.WHCode)
        && Equal(balance.LocCode, line.LocCode)
        && Equal(balance.LotNo, line.LotNo)
        && Equal(balance.IStatus, line.IStatus);

    private static bool Equal(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
