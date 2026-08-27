namespace ErpWeb.Model.Repositories.Inventory;

/// <summary>
/// Paging/filter args for stock master list and export. Company comes from the caller, never from UI.
/// </summary>
public sealed record StockMasterSearchArgs(
    string? SearchText,
    bool? IsActive,
    string? IClassCode,
    string? ISubClassCode,
    string? IType,
    string? DefWarehouse,
    string? Brand,
    string? SortField,
    bool SortDescending,
    int Skip,
    int Take);

public sealed record StockMasterReferenceCount(string ReferenceType, int Count);
