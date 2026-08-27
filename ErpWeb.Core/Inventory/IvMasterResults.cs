namespace ErpWeb.Core.Inventory;

public enum IvMasterErrorCode
{
    None = 0,
    AccessDenied,
    InvalidScope,
    NotFound,
    DuplicateKey,
    Validation,
    InUse,
    Concurrency
}

public sealed class IvMasterReferenceHit
{
    public string ReferenceType { get; init; } = string.Empty;
    public int Count { get; init; }
    public string? Detail { get; init; }
}

public sealed class DeleteCheckResult
{
    public bool CanDelete { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<IvMasterReferenceHit> References { get; init; } = [];

    public static DeleteCheckResult Ok() =>
        new() { CanDelete = true };

    public static DeleteCheckResult Blocked(string message, IReadOnlyList<IvMasterReferenceHit> references) =>
        new()
        {
            CanDelete = false,
            Message = message,
            References = references
        };
}

public sealed class IvMasterOperationResult<T>
{
    public bool Succeeded { get; init; }
    public IvMasterErrorCode ErrorCode { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, string> ValidationErrors { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public T? Data { get; init; }
    public DeleteCheckResult? DeleteCheck { get; init; }

    public static IvMasterOperationResult<T> Ok(T data) =>
        new() { Succeeded = true, ErrorCode = IvMasterErrorCode.None, Data = data };

    public static IvMasterOperationResult<T> Ok() =>
        new() { Succeeded = true, ErrorCode = IvMasterErrorCode.None };

    public static IvMasterOperationResult<T> Fail(
        IvMasterErrorCode code,
        string message,
        IReadOnlyDictionary<string, string>? validationErrors = null,
        DeleteCheckResult? deleteCheck = null) =>
        new()
        {
            Succeeded = false,
            ErrorCode = code,
            Message = message,
            ValidationErrors = validationErrors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DeleteCheck = deleteCheck
        };
}

public sealed class IvStockMasterListQuery
{
    public string? SearchText { get; set; }
    public bool? IsActive { get; set; }
    public string? IClassCode { get; set; }
    public string? ISubClassCode { get; set; }
    public string? IType { get; set; }
    public string? DefWarehouse { get; set; }
    public string? Brand { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class IvStockMasterListRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string? Barcode { get; init; }
    public string? Brand { get; init; }
    public string? DefWarehouse { get; init; }
    public string? IClassCode { get; init; }
    public string? ISubClassCode { get; init; }
    public string? IType { get; init; }
    public string? StdUom { get; init; }
    public string? SellingUom { get; init; }
    public string? PurUom { get; init; }
    public string? SellingGlCode { get; init; }
    public string? PurchaseGlCode { get; init; }
    public string? Classification { get; init; }
    public bool IsActive { get; init; }
    public decimal? SellingPrice { get; init; }
    public decimal? PurchasePrice { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvStockMasterListPage
{
    public IReadOnlyList<IvStockMasterListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class IvStockMasterEditVm
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? Barcode { get; set; }
    public string? Brand { get; set; }
    public bool IsActive { get; set; } = true;
    public string? IType { get; set; }
    public string? IClassCode { get; set; }
    public string? ISubClassCode { get; set; }
    public string? StdUom { get; set; }
    public string? SellingUom { get; set; }
    public string? PurUom { get; set; }
    public bool StockControl { get; set; } = true;
    public bool LotControl { get; set; }
    public string? DefWarehouse { get; set; }
    public string? DefLocation { get; set; }
    public decimal? MinStock { get; set; }
    public decimal? MaxStock { get; set; }
    public decimal? StdPackSize { get; set; }
    public decimal? PurStdPackSize { get; set; }
    public decimal? SellingPrice { get; set; }
    public decimal? PurchasePrice { get; set; }
    public string? SellingGlCode { get; set; }
    public string? PurchaseGlCode { get; set; }
    public string? TaxGroup { get; set; }
    public string? PurchaseTaxGroup { get; set; }
    public string? Classification { get; set; }
    public string? Size { get; set; }
    public string? Color { get; set; }
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class IvMasterKeyToken
{
    public string Code { get; init; } = string.Empty;
    public byte[] RowVersion { get; init; } = [];
    public string? ParentCode { get; init; }
}

public static class IvStockMasterSortFields
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(IvStockMasterListRow.ICode),
        nameof(IvStockMasterListRow.IDesc),
        nameof(IvStockMasterListRow.IType),
        nameof(IvStockMasterListRow.IClassCode),
        nameof(IvStockMasterListRow.ISubClassCode),
        nameof(IvStockMasterListRow.Brand),
        nameof(IvStockMasterListRow.StdUom),
        nameof(IvStockMasterListRow.DefWarehouse),
        nameof(IvStockMasterListRow.SellingPrice),
        nameof(IvStockMasterListRow.PurchasePrice),
        nameof(IvStockMasterListRow.IsActive)
    };
}
