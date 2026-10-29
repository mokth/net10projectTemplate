namespace ErpWeb.Core.Inventory;

using ErpWeb.Core.Inventory.Lifecycle;
using ErpWeb.Model.Entities.Inventory;

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

    /// <summary>
    /// Deletable, with an explicit note. Used where a master has no consumer column yet so the
    /// "no references" answer is a documented statement rather than a silent <c>true</c> (plan §10.5).
    /// </summary>
    public static DeleteCheckResult Ok(string message) =>
        new() { CanDelete = true, Message = message };

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

    /// <summary>
    /// Operator-facing explanation when the boundary that refused the action could prove one (a stock
    /// ledger refusal, a stale document, an unexpected exception with a support reference). Null keeps
    /// every existing caller and screen on its current message.
    /// </summary>
    public ErpWeb.Core.Services.ErpOperatorError? OperatorError { get; init; }
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
        DeleteCheckResult? deleteCheck = null,
        T? data = default,
        ErpWeb.Core.Services.ErpOperatorError? operatorError = null) =>
        new()
        {
            Succeeded = false,
            ErrorCode = code,
            Message = message,
            ValidationErrors = validationErrors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DeleteCheck = deleteCheck,
            Data = data,
            OperatorError = operatorError
        };
}

/// <summary>
/// List filter keys for the item lifecycle. <see cref="ActiveAndDiscontinued"/> is the default view:
/// an item master list must not hide the company's open obligations by default.
/// </summary>
public static class IvStockLifecycleFilterKeys
{
    public const string Active = "ACTIVE";
    public const string Discontinued = "DISCONTINUED";
    public const string Inactive = "INACTIVE";
    public const string ActiveAndDiscontinued = "ACTIVE_DISCONTINUED";
    public const string All = "ALL";
}

public sealed class IvStockMasterListQuery
{    public string? SearchText { get; set; }
    public bool? IsActive { get; set; }

    /// <summary>
    /// Optional lifecycle filter: ACTIVE, DISCONTINUED, INACTIVE or ALL. When set it takes precedence
    /// over <see cref="IsActive"/>; null keeps the legacy boolean filter working.
    /// </summary>
    public string? LifecycleStatus { get; set; }

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
    public string MfgType { get; init; } = "BUY";
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

    /// <summary>True only in the DISCONTINUED state (still active for existing fulfilment).</summary>
    public bool IsDiscontinued { get; init; }

    /// <summary>ACTIVE | DISCONTINUED | INACTIVE, derived from the two persisted flags.</summary>
    public string LifecycleStatus { get; init; } = IvStockItemLifecycleStatusCodes.Active;

    public string LifecycleLabel { get; init; } = "Active";

    /// <summary>Server UTC cutover boundary, when the item was last discontinued.</summary>
    public DateTime? DiscontinuedAtUtc { get; init; }

    public decimal? SellingPrice { get; init; }
    public decimal? PurchasePrice { get; init; }

    /// <summary>
    /// Reorder thresholds. Entered on the entry page and, until the stock-alert inquiry landed, invisible
    /// here — which made a low-stock result impossible to explain from the item list.
    /// </summary>
    public decimal? MinStock { get; init; }

    /// <inheritdoc cref="MinStock"/>
    public decimal? MaxStock { get; init; }

    public byte[] RowVersion { get; init; } = [];
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

public sealed class IvStockMasterListPage
{
    public IReadOnlyList<IvStockMasterListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>Per-item outcome of a bulk lifecycle command. All-or-none at transaction level.</summary>
public sealed class IvStockLifecycleItemResult
{
    public string ICode { get; init; } = string.Empty;
    public string? OldStatus { get; init; }
    public string? NewStatus { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => string.IsNullOrEmpty(Error);
}

public sealed class IvStockLifecycleBulkResult
{
    public IReadOnlyList<IvStockLifecycleItemResult> Items { get; init; } = [];
    public int ChangedCount => Items.Count(x => x.Succeeded && !string.Equals(x.OldStatus, x.NewStatus, StringComparison.Ordinal));
}

public sealed class IvStockMasterEditVm
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public bool HasImage { get; set; }
    public string MfgType { get; set; } = "BUY";
    public string? Barcode { get; set; }
    public string? Brand { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The user-visible lifecycle status. The entry page edits this single value and the service maps
    /// it to <see cref="IsActive"/> + <see cref="IsDiscontinued"/> through one authoritative mapper.
    /// </summary>
    public string LifecycleStatus { get; set; } = IvStockItemLifecycleStatusCodes.Active;

    /// <summary>Derived mirror of <see cref="LifecycleStatus"/>; kept for existing consumers.</summary>
    public bool IsDiscontinued { get; set; }

    public DateTime? DiscontinuedAtUtc { get; set; }

    /// <summary>Mandatory reason supplied with a status change. Never persisted on the item itself.</summary>
    public string? LifecycleChangeReason { get; set; }

    public string? IType { get; set; }
    public string? IClassCode { get; set; }
    public string? ISubClassCode { get; set; }
    public string? StdUom { get; set; }
    public string? SellingUom { get; set; }
    public string? PurUom { get; set; }
    public bool StockControl { get; set; } = true;
    public bool LotControl { get; set; }
    public string ExpiryControl { get; set; } = IvExpiryControlModes.None;
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

    /// <summary>
    /// True when <c>StdUom</c>, <c>StockControl</c>, or <c>LotControl</c> may no longer change because
    /// a live workflow or an existing inventory footprint still depends on the old structural meaning.
    /// UI-only advisory: the service re-runs the same guard on every save (plan §5.5, §5.6).
    /// </summary>
    public bool StructuralFieldsLocked { get; set; }

    /// <summary>Short, user-facing explanation of <see cref="StructuralFieldsLocked"/>.</summary>
    public string? StructuralLockReason { get; set; }
}

/// <summary>Read-only structural lock state returned by the Stock Master service.</summary>
public sealed class IvStockStructureState
{
    public static readonly IvStockStructureState Unlocked = new();

    public bool IsLocked { get; init; }
    public string? Reason { get; init; }
    public string? ReferenceCategory { get; init; }

    public static IvStockStructureState Locked(string reason, string category) =>
        new() { IsLocked = true, Reason = reason, ReferenceCategory = category };
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
        nameof(IvStockMasterListRow.IsActive),
        nameof(IvStockMasterListRow.IsDiscontinued),
        nameof(IvStockMasterListRow.LifecycleStatus)
    };
}
