namespace ErpWeb.Core.Menus;

public interface IUserRoleSyncService
{
    Task ReconcileFromUserLevelAsync(int uid, string companyCode, string? userlevel, CancellationToken cancellationToken = default);
}
