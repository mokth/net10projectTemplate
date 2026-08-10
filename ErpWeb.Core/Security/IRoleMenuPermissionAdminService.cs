namespace ErpWeb.Core.Security;

public sealed class RoleMenuPermissionRow
{
    public int RoleMenuPermissionId { get; set; }
    public int RoleId { get; set; }
    public int MenuId { get; set; }
    public int PermissionId { get; set; }
    public bool IsAllowed { get; set; }
    public string RoleCode { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string MenuCode { get; set; } = string.Empty;
    public string MenuName { get; set; } = string.Empty;
    public string PermissionCode { get; set; } = string.Empty;
    public string PermissionName { get; set; } = string.Empty;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}

public sealed class MenuOptionRow
{
    public int MenuId { get; init; }
    public string MenuCode { get; init; } = string.Empty;
    public string MenuName { get; init; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(MenuCode)
        ? MenuName
        : $"{MenuCode} — {MenuName}";
}

public sealed class PermissionOptionRow
{
    public int PermissionId { get; init; }
    public string PermissionCode { get; init; } = string.Empty;
    public string PermissionName { get; init; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(PermissionCode)
        ? PermissionName
        : $"{PermissionCode} — {PermissionName}";
}

public sealed class RoleOptionAdminRow
{
    public int RoleId { get; init; }
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(RoleCode)
        ? RoleName
        : $"{RoleName} ({RoleCode})";
}

public sealed class RolePermissionMatrixColumn
{
    public int PermissionId { get; init; }
    public string PermissionCode { get; init; } = string.Empty;
    public string PermissionName { get; init; } = string.Empty;
}

public sealed class RolePermissionMatrixRow
{
    public int MenuId { get; init; }
    public string MenuCode { get; init; } = string.Empty;
    public string MenuName { get; init; } = string.Empty;
    public int Depth { get; init; }
    public string IndentedName => Depth <= 0
        ? MenuName
        : $"{new string(' ', Depth * 2)}{MenuName}";
}

public sealed class RolePermissionGrant
{
    public int MenuId { get; init; }
    public int PermissionId { get; init; }
    public bool IsAllowed { get; init; }
}

public sealed class RolePermissionMatrixChange
{
    public int MenuId { get; init; }
    public int PermissionId { get; init; }
    public bool IsAllowed { get; init; }
}

public sealed class RoleMenuPermissionAdminOperationResult
{
    public bool Succeeded { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<RoleMenuPermissionRow> Rows { get; init; } = [];

    public IReadOnlyList<RoleOptionAdminRow> Roles { get; init; } = [];

    public IReadOnlyList<MenuOptionRow> Menus { get; init; } = [];

    public IReadOnlyList<MenuOptionRow> Modules { get; init; } = [];

    public IReadOnlyList<PermissionOptionRow> Permissions { get; init; } = [];

    public IReadOnlyList<RolePermissionMatrixColumn> MatrixColumns { get; init; } = [];

    public IReadOnlyList<RolePermissionMatrixRow> MatrixRows { get; init; } = [];

    public IReadOnlyList<RolePermissionGrant> Grants { get; init; } = [];

    public static RoleMenuPermissionAdminOperationResult Ok() =>
        new() { Succeeded = true };

    public static RoleMenuPermissionAdminOperationResult Ok(IReadOnlyList<RoleMenuPermissionRow> rows) =>
        new() { Succeeded = true, Rows = rows };

    public static RoleMenuPermissionAdminOperationResult OkLookups(
        IReadOnlyList<RoleOptionAdminRow> roles,
        IReadOnlyList<MenuOptionRow> menus,
        IReadOnlyList<PermissionOptionRow> permissions,
        IReadOnlyList<MenuOptionRow>? modules = null) =>
        new()
        {
            Succeeded = true,
            Roles = roles,
            Menus = menus,
            Modules = modules ?? [],
            Permissions = permissions
        };

    public static RoleMenuPermissionAdminOperationResult OkMatrix(
        IReadOnlyList<RolePermissionMatrixColumn> columns,
        IReadOnlyList<RolePermissionMatrixRow> rows) =>
        new()
        {
            Succeeded = true,
            MatrixColumns = columns,
            MatrixRows = rows
        };

    public static RoleMenuPermissionAdminOperationResult OkGrants(IReadOnlyList<RolePermissionGrant> grants) =>
        new() { Succeeded = true, Grants = grants };

    public static RoleMenuPermissionAdminOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public interface IRoleMenuPermissionAdminService
{
    Task<RoleMenuPermissionAdminOperationResult> GetRowsAsync(CancellationToken cancellationToken = default);

    Task<RoleMenuPermissionAdminOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default);

    Task<RoleMenuPermissionAdminOperationResult> GetMatrixAsync(
        int moduleMenuId,
        CancellationToken cancellationToken = default);

    Task<RoleMenuPermissionAdminOperationResult> GetRoleGrantsAsync(
        int roleId,
        CancellationToken cancellationToken = default);

    Task<RoleMenuPermissionAdminOperationResult> SaveMatrixAsync(
        int roleId,
        IReadOnlyList<RolePermissionMatrixChange> changes,
        CancellationToken cancellationToken = default);

    Task<RoleMenuPermissionAdminOperationResult> AddAsync(
        RoleMenuPermissionRow row,
        CancellationToken cancellationToken = default);

    Task<RoleMenuPermissionAdminOperationResult> UpdateAsync(
        RoleMenuPermissionRow row,
        CancellationToken cancellationToken = default);

    Task<RoleMenuPermissionAdminOperationResult> DeleteAsync(
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken = default);
}
