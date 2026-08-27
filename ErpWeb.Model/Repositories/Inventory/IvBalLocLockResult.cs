namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Non-tracked projection from a locked IvBalLoc row. Must never be attached to a DbContext.
/// </summary>
public sealed class IvBalLocLockResult
{
    public int Id { get; init; }
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string WhCode { get; init; } = string.Empty;
    public string LocCode { get; init; } = string.Empty;
    public string LotNo { get; init; } = string.Empty;
    public string IStatus { get; init; } = string.Empty;
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public int? LotId { get; init; }
}
