using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;

namespace ErpWeb.Core.Inventory;

internal static class InventoryServiceHelper
{
    public static async Task<(bool Ok, string? Error)> EnsureAccessAsync(
        IAccessRightService accessRights,
        string menuCode,
        string permissionCode,
        CancellationToken cancellationToken)
    {
        if (!await accessRights.CanAsync(menuCode, permissionCode, cancellationToken))
        {
            return (false, "Not authorized.");
        }

        return (true, null);
    }

    public static async Task<(bool Ok, string? Error)> ResolveCompanyAsync(
        ICompanyContext companyContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await companyContext.ResolveAsync(cancellationToken);
            return (true, null);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }
    }

    public static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
