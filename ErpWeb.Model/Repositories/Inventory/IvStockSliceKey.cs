namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Business key for <c>UQ_IvBalLoc_StockSlice</c>.
/// Column order: CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus.
/// Unused Loc/Lot/Status must be empty string, never null.
/// </summary>
public readonly record struct IvStockSliceKey(
    string CompanyCode,
    string BranchCode,
    string ICode,
    string WhCode,
    string LocCode,
    string LotNo,
    string IStatus) : IComparable<IvStockSliceKey>
{
    public static IvStockSliceKey Create(
        string companyCode,
        string branchCode,
        string iCode,
        string whCode,
        string? locCode,
        string? lotNo,
        string? iStatus) =>
        new(
            (companyCode ?? string.Empty).Trim(),
            (branchCode ?? string.Empty).Trim(),
            (iCode ?? string.Empty).Trim(),
            (whCode ?? string.Empty).Trim(),
            (locCode ?? string.Empty).Trim(),
            (lotNo ?? string.Empty).Trim(),
            (iStatus ?? string.Empty).Trim());

    public int CompareTo(IvStockSliceKey other)
    {
        var c = string.CompareOrdinal(CompanyCode, other.CompanyCode);
        if (c != 0) return c;
        c = string.CompareOrdinal(BranchCode, other.BranchCode);
        if (c != 0) return c;
        c = string.CompareOrdinal(ICode, other.ICode);
        if (c != 0) return c;
        c = string.CompareOrdinal(WhCode, other.WhCode);
        if (c != 0) return c;
        c = string.CompareOrdinal(LocCode, other.LocCode);
        if (c != 0) return c;
        c = string.CompareOrdinal(LotNo, other.LotNo);
        if (c != 0) return c;
        return string.CompareOrdinal(IStatus, other.IStatus);
    }

    public override string ToString() =>
        $"{CompanyCode}/{BranchCode}/{ICode}/{WhCode}/{LocCode}/{LotNo}/{IStatus}";
}
