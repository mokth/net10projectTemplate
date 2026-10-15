using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Reads an inclusive physical as-of quantity from the active V2 timeline.
/// A count-owned ADJ is positioned by its generation's end-of-day key, not
/// by the legacy history timestamp stored at midnight.
/// </summary>
public static class StockReplayPhysicalAsOfReader
{
    public static async Task<IReadOnlyDictionary<int, InventoryAsOfStock>> ReadAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> balanceIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(balanceIds);
        if (string.IsNullOrWhiteSpace(companyCode)
            || string.IsNullOrWhiteSpace(branchCode))
            throw new ArgumentException("An Inventory tenant is required.");
        if (balanceIds.Count == 0)
            return new Dictionary<int, InventoryAsOfStock>();
        var ids = balanceIds.Distinct().ToArray();
        if (ids.Any(id => id <= 0))
            throw Invalid("An as-of pile ID is not positive.");
        var epochCount = await db.StockLedgerEpochs.AsNoTracking()
            .CountAsync(e => e.CompanyCode == companyCode
                && e.BranchCode == branchCode
                && e.Status == StockLedgerEpochStatuses.Active
                && e.Version == 2 && e.EffectiveFrom <= asOf,
                cancellationToken);
        if (epochCount != 1)
            throw Invalid("Strict as-of stock requires one active V2 epoch at the requested time.");

        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && ids.Contains(b.Id))
            .Select(b => new { b.Id, b.StdQty })
            .ToArrayAsync(cancellationToken);
        if (balances.Length != ids.Length)
            throw Invalid("An as-of pile does not belong to the requested tenant.");

        // Every ordinary event at the instant, and every count at the final
        // tick of that day, is included in an inclusive as-of observation.
        var cutoff = new StockReplayEventKey(asOf,
            StockReplayEventKey.CountClass, long.MaxValue, "\uffff", int.MaxValue);
        var result = new Dictionary<int, InventoryAsOfStock>(ids.Length);
        foreach (var balance in balances)
        {
            if (balance.StdQty < 0m || IvQty.Round(balance.StdQty) != balance.StdQty)
                throw Invalid($"Pile {balance.Id} has invalid live quantity.");
            var later = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
                db, companyCode, branchCode, balance.Id, cutoff,
                cancellationToken);
            var quantity = balance.StdQty;
            for (var i = later.Count - 1; i >= 0; i--)
            {
                quantity = IvQty.Round(quantity - later[i].SignedDelta);
                if (quantity < 0m)
                    throw Invalid($"Pile {balance.Id} has a negative physical slice before event {later[i].EventId}.");
            }
            result.Add(balance.Id, new InventoryAsOfStock(balance.Id,
                balance.StdQty, quantity, Math.Min(balance.StdQty, quantity)));
        }
        return result;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
