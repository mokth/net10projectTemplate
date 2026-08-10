namespace ErpWeb.Core.Menus;

public interface IMenuSyncService
{
    Task<MenuSyncResult> PreviewXmlSyncAsync(CancellationToken cancellationToken = default);
    Task<MenuSyncResult> SyncFromXmlAsync(CancellationToken cancellationToken = default);
}
