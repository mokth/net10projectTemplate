namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Paging/filter args for inventory transaction batch lists. Tenant scope comes from the caller.
/// </summary>
public sealed record IvTrxBatchSearchArgs(
    string TrxType,
    string? SearchText,
    string? BatchStatus,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public sealed class IvTrxBatchListRow
{
    public int Id { get; init; }
    public int BatchNo { get; init; }
    public DateTime TrxDtTime { get; init; }
    public string BatchStatus { get; init; } = string.Empty;
    public string? RefNo { get; init; }
    public string? Remarks { get; init; }
    public int LineCount { get; init; }
    public decimal TotalAmount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
}
