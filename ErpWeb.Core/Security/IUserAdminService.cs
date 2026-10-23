using ErpWeb.Model.Repositories;

namespace ErpWeb.Core.Security;

public sealed class UserAdminOperationResult
{
    public bool Succeeded { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<UserAdminRow> Users { get; init; } = [];

    public IReadOnlyList<RoleOptionRow> Roles { get; init; } = [];

    public static UserAdminOperationResult Ok() =>
        new() { Succeeded = true };

    public static UserAdminOperationResult Ok(IReadOnlyList<UserAdminRow> users) =>
        new() { Succeeded = true, Users = users };

    public static UserAdminOperationResult OkRoles(IReadOnlyList<RoleOptionRow> roles) =>
        new() { Succeeded = true, Roles = roles };

    public static UserAdminOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public interface IUserAdminService
{
    Task<UserAdminOperationResult> GetUsersAsync(CancellationToken cancellationToken = default);

    Task<UserAdminOperationResult> GetRolesAsync(CancellationToken cancellationToken = default);

    Task<UserAdminOperationResult> AddUserAsync(CreateUserRequest user, CancellationToken cancellationToken = default);

    Task<UserAdminOperationResult> UpdateUserAsync(EditUserRequest user, CancellationToken cancellationToken = default);

    Task<UserAdminOperationResult> DeleteUsersAsync(IReadOnlyCollection<int> uids, CancellationToken cancellationToken = default);

    Task<UserAdminOperationResult> ResetPasswordAsync(
        int uid,
        string newPassword,
        string confirmPassword,
        CancellationToken cancellationToken = default);

    Task<UserAdminOperationResult> ForceChangeAsync(
        int uid,
        bool force,
        CancellationToken cancellationToken = default);
}
