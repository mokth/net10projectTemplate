using ErpWeb.Core.Menus;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

public static class ProductionPermissionCodes
{
    // Release is an approval of a frozen execution snapshot, not an inventory post.
    public const string Release = PermissionCodes.Approve;
}

public static class ProductionWorkOrderRules
{
    public static bool CanEdit(string? status) => status == ProductionWorkOrderStatuses.Draft;
    public static bool CanRelease(string? status) => status == ProductionWorkOrderStatuses.Draft;
    public static bool CanCancelDraft(string? status) => status == ProductionWorkOrderStatuses.Draft;
    public static bool CanReopen(string? status) => status == ProductionWorkOrderStatuses.Released;
    public static bool RequiresChangeOrder(string? status) => status is
        ProductionWorkOrderStatuses.Released or
        ProductionWorkOrderStatuses.InProgress or
        ProductionWorkOrderStatuses.Completed;
}

public static class ProductionChangeOrderRules
{
    public static bool CanRequest(string? workOrderStatus) => workOrderStatus is
        ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress;

    public static bool CanApply(string? changeStatus) => changeStatus == ProductionChangeOrderStatuses.Approved;

    /// <summary>Posted material/operation/FG facts are corrected with explicit documents, never field edits.</summary>
    public static bool MayRewritePostedHistory(string? targetType) => false;
}

