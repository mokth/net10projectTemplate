namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>Paged on-hand balance projection for MI lot/balance pickers.</summary>
public sealed class IvOnHandBalanceRow
{
    public int Id { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string WhCode { get; init; } = string.Empty;
    public string LocCode { get; init; } = string.Empty;
    public string LotNo { get; init; } = string.Empty;
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public string IStatus { get; init; } = string.Empty;
    public DateTime? ExpiryDate { get; init; }
    public string? IClassCode { get; init; }
    public bool LotControl { get; init; }
    public decimal? PurchasePrice { get; init; }
    public int? LotId { get; init; }
}
