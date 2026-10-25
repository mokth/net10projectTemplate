using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Locates the one existing Inventory MR destination pile for a replay draft.
/// The receipt authority rechecks the full item, lot and cost identity.
/// </summary>
internal static class StockReplayInventoryReceiptPileLocator
{
    internal static async Task<int?> FindUniqueAsync(
        AppDbContext db, string companyCode, string branchCode,
        IvTrxBatchDetail detail, CancellationToken cancellationToken)
    {
        var lotNo = detail.ToLotNo ?? string.Empty;
        var location = detail.ToLocation ?? string.Empty;
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && b.ICode == detail.ICode
                && b.WhCode == detail.ToWarehouse
                && b.LocCode == location
                && b.IStatus == detail.IStatus
                && (b.LotNo == lotNo
                    || (b.LotNo == null && lotNo == string.Empty)))
            .Select(b => b.Id).ToArrayAsync(cancellationToken);
        return piles.Length == 1 ? piles[0] : null;
    }
}
