namespace ErpWeb.Model.Repositories.Purchase;

public sealed record PoPrSearchArgs(
    string? SearchText,
    string? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    string? CreatedBy,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);
