namespace ErpWeb.Core.Inventory;

public interface IIvInventoryRefService
{
    // --- Warehouse ---
    Task<IvMasterOperationResult<IReadOnlyList<IvWarehouseListRow>>> ListWarehousesAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvWarehouseEditVm>> GetWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvWarehouseEditVm>> SaveWarehouseAsync(
        IvWarehouseEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetWarehouseActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteWarehousesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteWarehousesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default);

    // --- Location ---
    Task<IvMasterOperationResult<IReadOnlyList<IvLocationListRow>>> ListLocationsAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvLocationEditVm>> GetLocationAsync(
        string warehouseCode,
        string locCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvLocationEditVm>> SaveLocationAsync(
        IvLocationEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetLocationActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteLocationsAsync(
        IReadOnlyList<IvMasterKeyToken> keys,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteLocationsAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default);

    // --- Status ---
    Task<IvMasterOperationResult<IReadOnlyList<IvStatusListRow>>> ListStatusesAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStatusEditVm>> GetStatusAsync(
        string iStatus,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvStatusEditVm>> SaveStatusAsync(
        IvStatusEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetStatusActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteStatusesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteStatusesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default);

    // --- UOM ---
    Task<IvMasterOperationResult<IReadOnlyList<IvUomListRow>>> ListUomsAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvUomEditVm>> GetUomAsync(
        string uomCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvUomEditVm>> SaveUomAsync(
        IvUomEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetUomActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteUomsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteUomsAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default);

    // --- Type ---
    Task<IvMasterOperationResult<IReadOnlyList<IvTypeListRow>>> ListTypesAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvTypeEditVm>> GetTypeAsync(
        string typeCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvTypeEditVm>> SaveTypeAsync(
        IvTypeEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetTypeActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteTypesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteTypesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default);

    // --- Class (+ subclasses) ---
    Task<IvMasterOperationResult<IReadOnlyList<IvClassListRow>>> ListClassesAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvClassEditVm>> GetClassAsync(
        string iClassCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IvClassEditVm>> SaveClassAsync(
        IvClassEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetClassActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteClassesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteClassesAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default);
}

public sealed class IvWarehouseListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public string? WarehouseType { get; init; }
    public string? WarehouseRemark { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvWarehouseEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public string? WarehouseType { get; set; }
    public string? WarehouseRemark { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class IvLocationListRow
{
    public string WarehouseCode { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvLocationEditVm
{
    public string WarehouseCode { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class IvStatusListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvStatusEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class IvUomListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public string? UneceUom { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvUomEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public string? UneceUom { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class IvTypeListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public string? TypeName { get; init; }
    public bool KeepStock { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvTypeEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? TypeName { get; set; }
    public string? TypeDesc { get; set; }
    public bool KeepStock { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class IvClassListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class IvSubClassEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
}

public sealed class IvClassEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Desc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }
    public List<IvSubClassEditVm> SubClasses { get; set; } = [];
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
