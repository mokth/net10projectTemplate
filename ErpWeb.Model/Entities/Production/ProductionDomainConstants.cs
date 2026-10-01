namespace ErpWeb.Model.Entities.Production;

public static class ProductionWorkOrderStatuses
{
    public const string Draft = "DRAFT";
    public const string Released = "RELEASED";
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    public static bool IsKnown(string? value) => value is
        Draft or Released or InProgress or Completed or Closed or Cancelled;
}

public static class ProductionSchedulingDirections
{
    public const string Forward = "FORWARD";
    public const string Backward = "BACKWARD";

    public static bool IsKnown(string? value) => value is Forward or Backward;

    /// <summary>
    /// Converts a date-only planner input into a neutral scheduling boundary. Resource-specific
    /// calendars remain responsible for snapping this boundary to a usable interval.
    /// </summary>
    public static DateTime NormalizePlannerDateAnchor(DateTime date, string? direction) =>
        string.Equals(direction, Backward, StringComparison.Ordinal)
            ? date.Date.AddDays(1).AddTicks(-1)
            : date.Date;
}

public static class ProductionSourceTypes
{
    public const string Manual = "MANUAL";
    public const string DeliveryRequest = "DELIVERY_REQUEST";
    public const string SalesOrder = "SALES_ORDER";
    public const string Mrp = "MRP";

    public static bool IsKnown(string? value) => value is Manual or DeliveryRequest or SalesOrder or Mrp;
}

public static class ProductionAuditEventTypes
{
    public const string Created = "CREATED";
    public const string DraftUpdated = "DRAFT_UPDATED";
    public const string Released = "RELEASED";
    public const string Cancelled = "CANCELLED";
    public const string ChangeOrderRequested = "CHANGE_ORDER_REQUESTED";
    public const string ChangeOrderApproved = "CHANGE_ORDER_APPROVED";
    public const string Refreshed = "REFRESHED";
    public const string ScheduleRecalculated = "SCHEDULE_RECALCULATED";
    public const string MachineSelected = "MACHINE_SELECTED";
    public const string MaterialSubstituted = "MATERIAL_SUBSTITUTED";
    public const string MaterialIssued = "MATERIAL_ISSUED";
    public const string MaterialIssueRolledBack = "MATERIAL_ISSUE_ROLLED_BACK";
}

public static class ProductionMaterialMovementTypes
{
    public const string Issue = "ISSUE";
    public const string IssueReversal = "ISSUE_REVERSAL";
    public const string Return = "RETURN";
    public const string Consume = "CONSUME";
    public const string Adjustment = "ADJUST";
}

public static class ProductionPostingCommandTypes
{
    public const string MaterialIssuePost = "MATERIAL_ISSUE_POST";
    public const string MaterialIssueRollback = "MATERIAL_ISSUE_ROLLBACK";
}

public static class ProductionDocumentTypes
{
    public const string MaterialIssue = "MATERIAL_ISSUE";
}

public static class ProductionChangeOrderStatuses
{
    public const string Draft = "DRAFT";
    public const string Requested = "REQUESTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Applied = "APPLIED";
    public const string Cancelled = "CANCELLED";
}

public static class ProductionPostingLinkStatuses
{
    public const string Pending = "PENDING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Reversed = "REVERSED";
}

/// <summary>
/// How a machine option's cycle quantity is interpreted. Fixed to <see cref="Discrete"/> in this
/// milestone (plan §6.4); a continuous/rate mode would need an explicit snapshotted conversion.
/// </summary>
public static class ProductionMachineCycleQuantityModes
{
    public const string Discrete = "DISCRETE";

    public static bool IsKnown(string? value) => value is Discrete;
}

/// <summary>
/// Basis a snapshotted labour standard's rate is expressed in. The Product Definition labour
/// standard supports <see cref="PerOutputUnit"/> only (plan §6.4); do not infer the basis from
/// whichever numeric field happens to be non-zero.
/// </summary>
public static class ProductionLabourRateBases
{
    public const string PerOutputUnit = "PER_OUTPUT_UNIT";

    public static bool IsKnown(string? value) => value is PerOutputUnit;
}

/// <summary>Which calendar drove a scheduled snapshot row (plan §6.9).</summary>
public static class ProductionCalendarSourceTypes
{
    /// <summary>Duration came from the selected machine option; the machine calendar applies.</summary>
    public const string Machine = "MACHINE";

    /// <summary>
    /// Duration came from <c>StandardDurationMinutes</c>; the plant-default calendar stored on
    /// the operation applies.
    /// </summary>
    public const string PlantDefault = "PLANT_DEFAULT";

    public static bool IsKnown(string? value) => value is Machine or PlantDefault;
}

/// <summary>
/// Snapshot format versions. Version 1 is the flattened Phase-1 snapshot; version 2 is the full
/// route step / operation / machine / labour hierarchy (plan §6.6).
/// </summary>
public static class ProductionSnapshotFormatVersions
{
    public const int Legacy = 1;
    public const int Current = 2;
}

/// <summary>Current canonical snapshot hash algorithm version (plan §4.2).</summary>
public static class ProductionSnapshotHashVersions
{
    public const int Current = 1;
}

/// <summary>
/// Current canonicalization version for <c>DefinitionSourceHash</c> — the hash of the exact
/// Product Definition revision payload a snapshot was built from (plan §4.2).
/// </summary>
public static class ProductionDefinitionSourceHashVersions
{
    public const int Current = 1;
}

