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
    public const string ReopenedForEdit = "REOPENED_FOR_EDIT";
    public const string Cancelled = "CANCELLED";
    public const string ChangeOrderRequested = "CHANGE_ORDER_REQUESTED";
    public const string ChangeOrderApproved = "CHANGE_ORDER_APPROVED";
    public const string Refreshed = "REFRESHED";
    public const string DefinitionChanged = "DEFINITION_CHANGED";
    public const string ScheduleRecalculated = "SCHEDULE_RECALCULATED";
    public const string MachineSelected = "MACHINE_SELECTED";
    public const string MaterialSubstituted = "MATERIAL_SUBSTITUTED";
    public const string MaterialIssued = "MATERIAL_ISSUED";
    public const string MaterialIssueRolledBack = "MATERIAL_ISSUE_ROLLED_BACK";
    public const string OutputPosted = "OUTPUT_POSTED";
    public const string OutputRolledBack = "OUTPUT_ROLLED_BACK";
    public const string Completed = "COMPLETED";
}

public static class ProductionMaterialMovementTypes
{
    public const string Issue = "ISSUE";
    public const string IssueReversal = "ISSUE_REVERSAL";
    public const string Return = "RETURN";
    public const string Consume = "CONSUME";
    public const string ConsumeReversal = "CONSUME_REVERSAL";
    public const string Adjustment = "ADJUST";
}

public static class ProductionPostingCommandTypes
{
    public const string MaterialIssuePost = "MATERIAL_ISSUE_POST";
    public const string MaterialIssueRollback = "MATERIAL_ISSUE_ROLLBACK";
    public const string OutputPost = "OUTPUT_POST";
    public const string OutputRollback = "OUTPUT_ROLLBACK";
}

public static class ProductionDocumentTypes
{
    public const string MaterialIssue = "MATERIAL_ISSUE";
    public const string ProductionOutput = "PRODUCTION_OUTPUT";
}

public static class ProductionBalLotKinds
{
    public const string MaterialIn = "MATERIAL_IN";
    public const string Wip = "WIP";
}

public static class ProductionBalLotMovementTypes
{
    public const string OpeningIn = "OPENING_IN";
    public const string Issue = "ISSUE";
    public const string IssueReversal = "ISSUE_REVERSAL";
    public const string Produce = "PRODUCE";
    public const string ProduceReversal = "PRODUCE_REVERSAL";
    public const string Consume = "CONSUME";
    public const string ConsumeReversal = "CONSUME_REVERSAL";
    public const string Return = "RETURN";
    public const string ReturnReversal = "RETURN_REVERSAL";
    public const string TransferOut = "TRANSFER_OUT";
    public const string TransferIn = "TRANSFER_IN";
    public const string StatusOut = "STATUS_OUT";
    public const string StatusIn = "STATUS_IN";
    public const string AdjustIn = "ADJUST_IN";
    public const string AdjustOut = "ADJUST_OUT";
    public const string ScrapOut = "SCRAP_OUT";
    public const string FgReceiptOut = "FG_RECEIPT_OUT";
}

public static class ProductionOutputStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
    public const string Reversed = "REVERSED";
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
    public const string Draft = "DRAFT";
    public const string Cancelled = "CANCELLED";
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
/// Snapshot format versions. Format 1 = flattened Phase-1; format 2 = full hierarchy with
/// date-based definition selection; format 3 = explicit Product Definition identity.
/// </summary>
public static class ProductionSnapshotFormatVersions
{
    public const int Legacy = 1;
    public const int FullHierarchyV2 = 2;
    public const int DefinitionIdentityV3 = 3;
    public const int Current = DefinitionIdentityV3;

    /// <summary>
    /// Formats that carry the full route/operation/material hierarchy (format 2+) and can
    /// support material issue / posting without requiring a format-3 upgrade on already-released rows.
    /// </summary>
    public static bool IsFullHierarchy(int snapshotFormatVersion) =>
        snapshotFormatVersion >= FullHierarchyV2;
}

/// <summary>
/// Snapshot hash algorithm versions. V1/V2 are immutable executable contracts — dispatch by
/// the stored <c>SnapshotHashVersion</c>; never recompute a stored V1 hash with V2.
/// </summary>
public static class ProductionSnapshotHashVersions
{
    public const int V1 = 1;
    public const int DefinitionIdentityV2 = 2;
    /// <summary>Adds route OutputType, YieldPercent, OutputBaseUom, OutputConversionFactorToBase.</summary>
    public const int RouteOutputContractV3 = 3;
    public const int Current = RouteOutputContractV3;
}

/// <summary>
/// Definition source hash algorithm versions. V1 includes EffectiveFrom/To; V2 uses DefinitionCode
/// and excludes IsDefaultDefinition from manufacturing content.
/// </summary>
public static class ProductionDefinitionSourceHashVersions
{
    public const int V1 = 1;
    public const int DefinitionIdentityV2 = 2;
    public const int Current = DefinitionIdentityV2;
}

