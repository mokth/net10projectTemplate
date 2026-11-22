using System.Globalization;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.UI.Planning.WorkOrders;

/// <summary>Operator-facing text only; command permissions and production rules stay in their existing owners.</summary>
internal static class WorkOrderEntryText
{
    internal static string Status(string value) => value switch
    {
        ProductionWorkOrderStatuses.Draft => "Draft",
        ProductionWorkOrderStatuses.Released => "Sent to production",
        ProductionWorkOrderStatuses.InProgress => "Production in progress",
        ProductionWorkOrderStatuses.Completed => "Production completed",
        ProductionWorkOrderStatuses.Closed => "Closed",
        ProductionWorkOrderStatuses.Cancelled => "Cancelled",
        _ => ReadableCode(value)
    };

    internal static string Source(string value) => value switch
    {
        ProductionSourceTypes.Manual => "Entered directly",
        ProductionSourceTypes.DeliveryRequest => "Delivery request",
        ProductionSourceTypes.SalesOrder => "Sales order",
        ProductionSourceTypes.Mrp => "Material planning",
        _ => ReadableCode(value)
    };

    internal static string PlanState(bool hasSavedOrder, bool needsUpdate, bool hasChanges, bool hasPreview, bool previewIsCurrent) =>
        needsUpdate ? "Setup update needed"
        : hasPreview ? previewIsCurrent ? "Preview · not saved" : "Preview needs updating"
        : hasChanges ? "Changes to save"
        : hasSavedOrder ? "Saved plan" : "New order · not saved";

    internal static string NextStep(string status, bool needsUpdate, bool hasChanges, bool hasPreview, bool previewIsCurrent,
        bool canEdit = true, bool canOpenEdit = false, bool canSend = true) =>
        needsUpdate ? canOpenEdit
            ? "Choose Edit draft, then Update from production setup before editing or sending this older order."
            : "Update this order from its production setup before editing or sending it to production."
        : status == ProductionWorkOrderStatuses.Draft ? !canEdit
            ? (canOpenEdit ? "Choose Edit draft to change the order." : "Review this draft's product, quantity and dates below.")
                + (canSend ? " Choose Send to production when the order is ready." : string.Empty)
            : hasPreview && !previewIsCurrent
            ? "Your details changed after the preview. Check materials and dates again, or save to calculate the latest plan."
            : !canSend
            ? "Check the product, quantity and dates. Save a draft to keep the order for later."
            : hasChanges
            ? "Save a draft to keep your changes, or send the order to production when you are ready."
            : "Check the product, quantity and dates. Save a draft to finish later, or send the order to production when ready."
        : status is ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress
            ? "This order has been sent to production. Material issues and actual production are recorded separately."
        : status == ProductionWorkOrderStatuses.Cancelled ? "This order is cancelled. Its saved details and history are shown below."
        : "Review the completed quantities and order history below.";

    internal static string FieldLabel(string key) => key.Split('.').Last() switch
    {
        "ProductCode" => "Product",
        "DefinitionCode" or "SourceProductDefinitionRevisionId" => "Production setup",
        "PlannedQty" => "Quantity to make",
        "SchedulingDirection" => "How to plan the dates",
        "PlannedStartDate" => "Start date",
        "PlannedCompletionDate" => "Finish date",
        "ScheduleAnchorDateTime" => "Planning date",
        "SourceReference" => "Your reference",
        "SourceType" or "DeliveryRequestId" => "Order source",
        "Remark" => "Notes",
        "CancellationReason" or "Reason" => "Reason",
        "RowVersion" or "SnapshotHash" or "SnapshotRevision" => "Saved order has changed",
        "Materials" or "MaterialId" => "Materials",
        "Machines" or "MachineId" => "Machines",
        _ => "Work order"
    };

    internal static string Message(string message) => message
        .Replace("Refresh Definition to upgrade the Work Order snapshot/costing contract before editing or re-releasing.",
            "Update this order from its production setup before editing or sending it to production again.", StringComparison.Ordinal)
        .Replace("Product Definition", "production setup", StringComparison.Ordinal)
        .Replace("ACTIVE definition", "production setup in use", StringComparison.Ordinal)
        .Replace("snapshot", "saved production plan", StringComparison.Ordinal)
        .Replace("Snapshot", "Saved production plan", StringComparison.Ordinal);

    internal static string ReadableCode(string? value) => string.IsNullOrWhiteSpace(value) ? "—"
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').ToLowerInvariant());
}
