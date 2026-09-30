using ErpWeb.Core.Menus;

namespace ErpWeb.Core.Planning;

public interface IPlanningMasterAccess
{
    Task<bool> CanAccessAsync(string menuCode, CancellationToken ct = default);
    Task<bool> CanAddAsync(string menuCode, CancellationToken ct = default);
    Task<bool> CanEditAsync(string menuCode, CancellationToken ct = default);
    Task<bool> CanDeleteAsync(string menuCode, CancellationToken ct = default);
}

public sealed class PlanningMasterAccess : IPlanningMasterAccess
{
    private readonly IAccessRightService _rights;

    public PlanningMasterAccess(IAccessRightService rights) => _rights = rights;

    public Task<bool> CanAccessAsync(string menuCode, CancellationToken ct = default) =>
        _rights.CanAccessAsync(menuCode, ct);

    public Task<bool> CanAddAsync(string menuCode, CancellationToken ct = default) =>
        _rights.CanAsync(menuCode, PermissionCodes.Add, ct);

    public Task<bool> CanEditAsync(string menuCode, CancellationToken ct = default) =>
        _rights.CanAsync(menuCode, PermissionCodes.Edit, ct);

    public Task<bool> CanDeleteAsync(string menuCode, CancellationToken ct = default) =>
        _rights.CanAsync(menuCode, PermissionCodes.Delete, ct);
}
