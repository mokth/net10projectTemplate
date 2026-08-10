using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Security;

public sealed class PermissionAdminOperationResult
{
    public bool Succeeded { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<Permission> Permissions { get; init; } = [];

    public static PermissionAdminOperationResult Ok() =>
        new() { Succeeded = true };

    public static PermissionAdminOperationResult Ok(IReadOnlyList<Permission> permissions) =>
        new() { Succeeded = true, Permissions = permissions };

    public static PermissionAdminOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public interface IPermissionAdminService
{
    Task<PermissionAdminOperationResult> GetPermissionsAsync(CancellationToken cancellationToken = default);

    Task<PermissionAdminOperationResult> AddPermissionAsync(
        Permission permission,
        CancellationToken cancellationToken = default);

    Task<PermissionAdminOperationResult> UpdatePermissionAsync(
        Permission permission,
        CancellationToken cancellationToken = default);

    Task<PermissionAdminOperationResult> DeletePermissionsAsync(
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken = default);
}
