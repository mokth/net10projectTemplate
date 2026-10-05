namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Non-tracked projection from a locked IvBalLoc row. Must never be attached to a DbContext.
/// </summary>
public sealed class IvBalLocLockResult
{
    public string? PriceEvidence { get; init; }
    public int Id { get; init; }
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string ICode { get; init; } = string.Empty;
    public string WhCode { get; init; } = string.Empty;
    public string LocCode { get; init; } = string.Empty;
    public string LotNo { get; init; } = string.Empty;
    public string IStatus { get; init; } = string.Empty;
    public string? LocationCode { get; init; }
    public DateTime? TransDate { get; init; }
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public int? LotId { get; init; }

    /// <summary>
    /// The pile's own price. This is the FIRST operand the stock-count post uses to price its ADJ
    /// line (<c>UnitPrice ?? item.PurchasePrice ?? 0</c>) — never the count sheet's snapshot column.
    /// </summary>
    public decimal? UnitPrice { get; init; }
}
