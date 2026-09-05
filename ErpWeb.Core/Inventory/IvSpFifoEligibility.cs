using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Shared FIFO candidate / persisted-detail eligibility. Matches LoadFifoPilesAsync WHERE verbatim.
/// </summary>
internal static class IvSpFifoEligibility
{
    public static bool IsShipmentRequired(bool stockControl, decimal stdQty) =>
        stockControl && IvQty.Round(stdQty) > 0m;

    public static bool MatchesCandidate(
        string companyCode,
        string branchCode,
        string locationCode,
        string iCode,
        string warehouse,
        DateTime docDate,
        string rowCompanyCode,
        string rowBranchCode,
        string? rowLocationCode,
        string rowICode,
        string rowWhCode,
        decimal rowStdQty,
        string rowIStatus,
        DateTime? rowTransDate)
    {
        return string.Equals(rowCompanyCode, companyCode, StringComparison.OrdinalIgnoreCase)
               && string.Equals(rowBranchCode, branchCode, StringComparison.OrdinalIgnoreCase)
               && string.Equals(rowICode, iCode, StringComparison.OrdinalIgnoreCase)
               && string.Equals(rowWhCode, warehouse, StringComparison.OrdinalIgnoreCase)
               && LocationMatches(rowLocationCode, locationCode)
               && rowStdQty > 0m
               && string.Equals(rowIStatus, IvItemStatuses.Active, StringComparison.OrdinalIgnoreCase)
               && rowTransDate is not null
               && rowTransDate.Value.Date <= docDate.Date;
    }

    /// <summary>
    /// Tenant site on the pile. Blank/null is treated as unstamped legacy stock and is eligible
    /// for the current write-scope location (IvBalLoc unique key does not include LocationCode).
    /// </summary>
    public static bool LocationMatches(string? rowLocationCode, string locationCode) =>
        string.IsNullOrWhiteSpace(rowLocationCode)
        || string.Equals(rowLocationCode.Trim(), locationCode, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesCandidate(IvBalLoc row, string company, string branch, string location, string iCode, string warehouse, DateTime docDate) =>
        MatchesCandidate(
            company,
            branch,
            location,
            iCode,
            warehouse,
            docDate,
            row.CompanyCode,
            row.BranchCode,
            row.LocationCode,
            row.ICode,
            row.WhCode,
            row.StdQty,
            row.IStatus,
            row.TransDate);

    public static bool MatchesCandidate(IvBalLocLockResult row, string company, string branch, string location, string iCode, string warehouse, DateTime docDate) =>
        MatchesCandidate(
            company,
            branch,
            location,
            iCode,
            warehouse,
            docDate,
            row.CompanyCode,
            row.BranchCode,
            row.LocationCode,
            row.ICode,
            row.WhCode,
            row.StdQty,
            row.IStatus,
            row.TransDate);

    public static bool MatchesPersistedDetail(
        IvBalLocLockResult row,
        IvTrxBatchDetail detail,
        string company,
        string branch,
        string location,
        string iCode,
        string warehouse,
        DateTime docDate)
    {
        if (!MatchesCandidate(row, company, branch, location, iCode, warehouse, docDate))
        {
            return false;
        }

        return string.Equals(row.ICode, detail.ICode ?? string.Empty, StringComparison.OrdinalIgnoreCase)
               && string.Equals(row.WhCode, detail.FrWarehouse ?? string.Empty, StringComparison.OrdinalIgnoreCase)
               && string.Equals(row.LocCode, detail.FrLocation ?? string.Empty, StringComparison.OrdinalIgnoreCase)
               && string.Equals(row.LotNo, detail.FrLotNo ?? string.Empty, StringComparison.OrdinalIgnoreCase)
               && string.Equals(row.IStatus, detail.IStatus ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
