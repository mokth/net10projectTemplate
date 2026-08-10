using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Security;

public sealed class RoleAdminOperationResult
{
    public bool Succeeded { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<Role> Roles { get; init; } = [];

    public static RoleAdminOperationResult Ok() =>
        new() { Succeeded = true };

    public static RoleAdminOperationResult Ok(IReadOnlyList<Role> roles) =>
        new() { Succeeded = true, Roles = roles };

    public static RoleAdminOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public interface IRoleAdminService
{
    Task<RoleAdminOperationResult> GetRolesAsync(CancellationToken cancellationToken = default);

    Task<RoleAdminOperationResult> AddRoleAsync(Role role, CancellationToken cancellationToken = default);

    Task<RoleAdminOperationResult> UpdateRoleAsync(Role role, CancellationToken cancellationToken = default);

    Task<RoleAdminOperationResult> DeleteRolesAsync(
        IReadOnlyCollection<int> roleIds,
        CancellationToken cancellationToken = default);
}
