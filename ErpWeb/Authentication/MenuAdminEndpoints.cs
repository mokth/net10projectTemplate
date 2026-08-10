using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;

namespace ErpWeb.Authentication;

public static class MenuAdminEndpoints
{
    public static IEndpointRouteBuilder MapMenuAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/menus");

        group.MapPost("/sync/preview", async (
            IMenuSyncService syncService,
            ICurrentUserService currentUser) =>
        {
            if (!currentUser.IsAuthenticated || !currentUser.IsInRole(AccessRightService.AdminRole))
            {
                return Results.Forbid();
            }

            var result = await syncService.PreviewXmlSyncAsync();
            return Results.Json(result);
        });

        group.MapPost("/sync", async (
            IMenuSyncService syncService,
            ICurrentUserService currentUser) =>
        {
            if (!currentUser.IsAuthenticated || !currentUser.IsInRole(AccessRightService.AdminRole))
            {
                return Results.Forbid();
            }

            var result = await syncService.SyncFromXmlAsync();
            return result.Success ? Results.Json(result) : Results.BadRequest(result);
        });

        return endpoints;
    }
}
