namespace ErpWeb.Core.Inventory;

using ErpWeb.Core.Menus;

/// <summary>
/// The resolve step every inquiry-kit service performs, in the one order the shipped plan fixed:
/// tenant scope FIRST (<see cref="IInventoryTenantContext.TryBranchScope"/>, fail closed), then ACCESS
/// on the <em>caller's own</em> menu.
///
/// <para>
/// It lives here rather than being copied per service so the order and the two failure messages cannot
/// drift between the pages of one suite — a page that resolved ACCESS before the tenant would leak a
/// cross-company existence signal.
/// </para>
/// </summary>
internal readonly record struct IvInquiryScopeContext(
    string? CompanyCode,
    string? BranchCode,
    string? Error,
    IvMasterErrorCode ErrorCode)
{
    /// <summary>
    /// The menu that was authorized, exactly as the allow-list resolved it. Callers reuse THIS value for
    /// any further permission check, so a differently-cased request can never be authorized against one
    /// code and then checked against another.
    /// </summary>
    public string? MenuCode { get; init; }

    public bool Succeeded => Error is null;

    public static IvInquiryScopeContext Ok(string companyCode, string branchCode, string menuCode) =>
        new(companyCode, branchCode, null, IvMasterErrorCode.None) { MenuCode = menuCode };

    public static IvInquiryScopeContext Fail(IvMasterErrorCode code, string error) =>
        new(null, null, error, code);
}

internal static class IvInquiryScopeResolver
{
    /// <summary>
    /// Resolves the tenant scope and the ACCESS right for <paramref name="menuCode"/>.
    /// <paramref name="knownMenus"/> is the allow-list of menus the calling service is permitted to
    /// serve — a page cannot borrow another screen's rights, and a typo in a menu code fails loudly
    /// instead of silently reading with someone else's permission.
    /// </summary>
    public static async Task<IvInquiryScopeContext> ResolveAsync(
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        string menuCode,
        IReadOnlySet<string> knownMenus,
        CancellationToken cancellationToken)
    {
        var menu = (menuCode ?? string.Empty).Trim();
        if (!knownMenus.Contains(menu))
        {
            return IvInquiryScopeContext.Fail(
                IvMasterErrorCode.Validation,
                $"Unknown inquiry menu '{menuCode}'.");
        }

        var scope = tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return IvInquiryScopeContext.Fail(
                IvMasterErrorCode.InvalidScope,
                "Invalid company or branch context.");
        }

        if (!await accessRights.CanAsync(menu, PermissionCodes.Access, cancellationToken))
        {
            return IvInquiryScopeContext.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return IvInquiryScopeContext.Ok(scope.CompanyCode, scope.BranchCode, menu);
    }
}
