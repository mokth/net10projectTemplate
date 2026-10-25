using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Verifies the exact Inventory source pile selected by an issue document.
/// Historical quantity is proved separately by the physical replay plan.
/// </summary>
internal static class StockReplayInventoryIssuePileAuthority
{
    internal static async Task<bool> MatchesAsync(
        AppDbContext db, IvStockMaster master, IvBalLoc balance,
        IvTrxBatchDetail detail, CancellationToken cancellationToken,
        bool requireActive = true)
    {
        if ((requireActive && !master.IsActive) || !master.StockControl
            || master.CompanyCode != balance.CompanyCode
            || master.ICode != balance.ICode
            || detail.CompanyCode != balance.CompanyCode
            || detail.BranchCode != balance.BranchCode
            || detail.ICode != balance.ICode
            || detail.FromBalLocId != balance.Id
            || !string.Equals(balance.WhCode, detail.FrWarehouse,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(balance.LocCode,
                detail.FrLocation ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(balance.IStatus, detail.IStatus,
                StringComparison.OrdinalIgnoreCase)
            || balance.StdUom != detail.FrStdUom)
            return false;

        if (!master.LotControl)
            return balance.LotId is null
                && string.IsNullOrWhiteSpace(balance.LotNo)
                && detail.FromLotId is null
                && string.IsNullOrWhiteSpace(detail.FrLotNo);

        if (balance.LotId is not > 0
            || string.IsNullOrWhiteSpace(balance.LotNo)
            || !string.Equals(balance.LotNo, detail.FrLotNo,
                StringComparison.OrdinalIgnoreCase)
            || detail.FromLotId is int stagedLotId
                && stagedLotId != balance.LotId.Value)
            return false;

        return await db.IvLots.AsNoTracking().AnyAsync(l =>
            l.Id == balance.LotId.Value
            && l.CompanyCode == balance.CompanyCode
            && l.ICode == balance.ICode
            && l.LotNo == balance.LotNo
            && (!requireActive || l.IsActive), cancellationToken);
    }
}
