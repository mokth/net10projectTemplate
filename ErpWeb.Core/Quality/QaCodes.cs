namespace ErpWeb.Core.Quality;

public static class QaSourceTypes
{
    public const string IncomingPreGrn = "INCOMING_PRE_GRN";
    public const string PostedGrn = "POSTED_GRN";
    public const string ProductionOutputGood = "PRODUCTION_OUTPUT_GOOD";
    public const string ProductionOutputNonGood = "PRODUCTION_OUTPUT_NON_GOOD";
    public const string ProductionWipLot = "PRODUCTION_WIP_LOT";
    public const string FgStagingLot = "FG_STAGING_LOT";
    public const string InventoryStock = "INVENTORY_STOCK";

    public static readonly IReadOnlyList<string> All =
    [
        IncomingPreGrn, PostedGrn, ProductionOutputGood, ProductionOutputNonGood,
        ProductionWipLot, FgStagingLot, InventoryStock
    ];

    public static bool IsKnown(string? value) =>
        All.Contains((value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
}

public static class QaCaseStatuses
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string InInspection = "IN_INSPECTION";
    public const string PendingReview = "PENDING_REVIEW";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All =
    [
        Draft, Submitted, InInspection, PendingReview, Approved, Rejected, Closed, Cancelled
    ];

    public static bool CanEdit(string? status) =>
        string.Equals(status, Draft, StringComparison.OrdinalIgnoreCase);

    public static bool CanDelete(string? status) =>
        string.Equals(status, Draft, StringComparison.OrdinalIgnoreCase);
}

public static class QaInspectionResults
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Conditional = "CONDITIONAL";

    public static readonly IReadOnlyList<string> All = [Pass, Fail, Conditional];

    public static bool IsKnown(string? value) =>
        All.Contains((value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
}

public static class QaDispositionKinds
{
    public const string Accept = "ACCEPT";
    public const string Reject = "REJECT";
    public const string Rework = "REWORK";
    public const string Hold = "HOLD";

    public static readonly IReadOnlyList<string> All = [Accept, Reject, Rework, Hold];

    public static bool IsKnown(string? value) =>
        All.Contains((value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
}

public static class QaAuditEvents
{
    public const string Created = "CREATED";
    public const string Saved = "SAVED";
    public const string Submitted = "SUBMITTED";
    public const string InspectionStarted = "INSPECTION_STARTED";
    public const string Inspected = "INSPECTED";
    public const string Disposed = "DISPOSED";
    public const string ReviewSubmitted = "REVIEW_SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";
    public const string GrantIssued = "GRANT_ISSUED";
    public const string AllocationPosted = "ALLOCATION_POSTED";
    public const string AllocationReversed = "ALLOCATION_REVERSED";
    public const string ReopenedForReissue = "REOPENED_FOR_REISSUE";
    public const string HoldPlaced = "HOLD_PLACED";
    public const string HoldReleased = "HOLD_RELEASED";
    public const string ActionCreated = "ACTION_CREATED";
    public const string ActionStarted = "ACTION_STARTED";
    public const string ActionReassigned = "ACTION_REASSIGNED";
    public const string ActionCompleted = "ACTION_COMPLETED";
    public const string ActionCancelled = "ACTION_CANCELLED";
    public const string ReinspectionStarted = "REINSPECTION_STARTED";
}

public static class QaActionTypes
{
    public const string Reject = "REJECT";
    public const string Rework = "REWORK";
    public const string Hold = "HOLD";
    public const string FollowUp = "FOLLOW_UP";

    public static readonly IReadOnlyList<string> All = [Reject, Rework, Hold, FollowUp];

    public static bool IsKnown(string? value) =>
        All.Contains((value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsQuantitative(string? value) =>
        !string.Equals(value, FollowUp, StringComparison.OrdinalIgnoreCase);
}

public static class QaActionStatuses
{
    public const string Open = "OPEN";
    public const string InProgress = "IN_PROGRESS";
    public const string Done = "DONE";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [Open, InProgress, Done, Cancelled];

    public static bool IsOpen(string? value) =>
        string.Equals(value, Open, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, InProgress, StringComparison.OrdinalIgnoreCase);
}

public static class QaLogicalHoldStatuses
{
    public const string Active = "ACTIVE";
    public const string Released = "RELEASED";
}

public static class QaLogicalHoldCauses
{
    public const string KeepHold = "KEEP_HOLD";
    public const string Release = "RELEASE";
}

public static class QaReceiptGrantStatuses
{
    public const string Open = "OPEN";
    public const string Superseded = "SUPERSEDED";
}

public static class QaReceiptAllocationStatuses
{
    public const string Posted = "POSTED";
    public const string Reversed = "REVERSED";
}

public static class QaDestinationKinds
{
    public const string GoodsReceipt = "GRN";
    public const string FinishedGoodReceipt = "FG";
    public const string VendorReturn = "VR";
    public const string InventoryScrap = "SC";
    public const string PurchaseCreditNote = "PO_CN";
    public const string DailyProduction = "WI";
    public const string LaterPhase = "LATER_PHASE";
}

public static class QaDestinationStatuses
{
    public const string Suggested = "SUGGESTED";
    public const string LinkedUnverified = "LINKED_UNVERIFIED";
    public const string Executed = "EXECUTED";
    public const string Reversed = "REVERSED";
    public const string NotStock = "NOT_STOCK";
    public const string RequiresLaterPhase = "REQUIRES_LATER_PHASE";
}

public static class QaEligibilityNotes
{
    public const string RecordOnly =
        "Record-only. Approval does not move stock, change WIP, or authorize posting.";

    public const string IncomingPreGrn =
        "Incoming before GRN: delivery note is text only. Inspection approval is a QA record. It does not create or authorize a goods receipt.";

    public const string IncomingGrant =
        "Accepted qty may be posted as a goods receipt. Rejected qty is not inventory and is not a vendor return.";

    public const string NonGoodOutput =
        "Non-good Daily Production qty is a counter only. Approval later allows posting those counters. This does not create a valued lot and is not inventory scrap.";

    public const string NonGoodGrant =
        "Accepted scrap/reject/hold qty may be posted on Daily Production. It stays a counter. Quality does not create a valued lot or split absorbed cost.";

    public const string WipLot =
        "WIP lot inspection. Approving a Hold quantity reserves that qty on the production lot. It does not move stock to a QA warehouse.";

    public const string WipReserved =
        "Hold qty is reserved on this production lot. Available qty is the lot balance minus reserved qty. Release the hold to make it usable again. This is not a warehouse transfer.";

    public const string FgStaging =
        "FG staging inspection is a QA record. Approval does not issue a finished-goods receipt allowance.";

    public const string FgGrant =
        "Accepted qty may be posted as a finished-good receipt. Rejected qty stays in FG staging and is not inventory.";

    public const string PostedStock =
        "Posted stock inspection is record-only. QCHOLD is not quarantine in this phase.";

    public const string PhysicalReserved =
        "Hold and reject qty are reserved on the source warehouse pile. Available qty is on-hand minus reserved qty. This does not convert IStatus to QCHOLD and is not a warehouse transfer.";

    public static string ForSource(string sourceType) => sourceType.Trim().ToUpperInvariant() switch
    {
        QaSourceTypes.IncomingPreGrn => IncomingPreGrn,
        QaSourceTypes.ProductionOutputNonGood => NonGoodOutput,
        QaSourceTypes.ProductionWipLot => WipLot,
        QaSourceTypes.FgStagingLot => FgStaging,
        QaSourceTypes.PostedGrn or QaSourceTypes.InventoryStock => PostedStock,
        _ => RecordOnly
    };
}

public static class QaCaseMessages
{
    public const string ChangedReload = "This QA case changed; reload.";
    public const string InspectFullQuantity =
        "Inspect the full case quantity on every line before submitting for review. Uninspected quantity is not accepted.";
    public const string PolicyChangedReload = "This policy was changed by someone else. Reload and try again.";
    public const string OpenActionsRemain =
        "Complete or cancel open follow-up tasks before closing this case. Follow-up is a QA task, not a stock posting.";
    public const string FollowUpNotInstalled =
        "Follow-up tracking is not installed on this database. Inspection records still save. Attachments stay deferred.";
    public const string AttachmentsDeferred =
        "Attachments are not enabled. Record completion evidence as text on the follow-up task.";
    public const string ReinspectionBlocked =
        "Reinspection is blocked while a quality hold or a sealed linked posting is active.";
    public const string InquiryRecordOnly =
        "QA records — no automatic stock posting. Rejected quantity is an inspection decision, not on-hand stock or inventory value.";
    public const string ApprovedWithoutRejectNote =
        "Share of inspected cases later approved with no rejected quantity. This is not a true first-pass yield.";
    public const string ExportTooLarge =
        "Export is limited to 5,000 cases. Narrow the filters and try again.";
}

public static class QaInquiryLimits
{
    public const int MaxPageTake = 100;
    public const int MaxExportRows = 5000;
}

public sealed record QaNamedOption(string Key, string Name);

public static class QaLabels
{
    public static IReadOnlyList<QaNamedOption> SourceOptions { get; } =
        QaSourceTypes.All.Select(x => new QaNamedOption(x, Source(x))).ToArray();

    public static IReadOnlyList<QaNamedOption> StatusOptions { get; } =
        QaCaseStatuses.All.Select(x => new QaNamedOption(x, Status(x))).ToArray();

    public static IReadOnlyList<QaNamedOption> ResultOptions { get; } =
        QaInspectionResults.All.Select(x => new QaNamedOption(x, Result(x))).ToArray();

    public static IReadOnlyList<QaNamedOption> ActionTypeOptions { get; } =
        QaActionTypes.All.Select(x => new QaNamedOption(x, ActionType(x))).ToArray();

    public static string Source(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        QaSourceTypes.IncomingPreGrn => "Incoming delivery (before GRN)",
        QaSourceTypes.PostedGrn => "Posted goods receipt",
        QaSourceTypes.ProductionOutputGood => "Daily Production (good)",
        QaSourceTypes.ProductionOutputNonGood => "Daily Production (non-good)",
        QaSourceTypes.ProductionWipLot => "WIP process lot",
        QaSourceTypes.FgStagingLot => "Finished-goods staging",
        QaSourceTypes.InventoryStock => "Posted miscellaneous receipt",
        _ => Blank(value)
    };

    public static string Status(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        QaCaseStatuses.Draft => "Draft",
        QaCaseStatuses.Submitted => "Submitted",
        QaCaseStatuses.InInspection => "In inspection",
        QaCaseStatuses.PendingReview => "Pending review",
        QaCaseStatuses.Approved => "Approved",
        QaCaseStatuses.Rejected => "Rejected",
        QaCaseStatuses.Closed => "Closed",
        QaCaseStatuses.Cancelled => "Cancelled",
        _ => Blank(value)
    };

    public static string Result(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        QaInspectionResults.Pass => "Pass",
        QaInspectionResults.Fail => "Fail",
        QaInspectionResults.Conditional => "Conditional",
        _ => Blank(value)
    };

    public static string NextAction(string? status) => (status ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "" or QaCaseStatuses.Draft => "Save the source and lines, then submit.",
        QaCaseStatuses.Submitted => "Record inspection results, or start inspection first.",
        QaCaseStatuses.InInspection => "Record every line, then submit for review.",
        QaCaseStatuses.PendingReview => "Approve or reject this inspection record.",
        QaCaseStatuses.Approved => "Close the case, or open a suggested follow-up document. Quality does not post stock.",
        QaCaseStatuses.Rejected => "Close the case. Quality does not post stock.",
        QaCaseStatuses.Closed => "This case is closed.",
        QaCaseStatuses.Cancelled => "This case was cancelled.",
        _ => "Open the case to continue."
    };

    public static string NextActionShort(string? status) => (status ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "" or QaCaseStatuses.Draft => "Submit",
        QaCaseStatuses.Submitted => "Inspect",
        QaCaseStatuses.InInspection => "Submit for review",
        QaCaseStatuses.PendingReview => "Approve or reject",
        QaCaseStatuses.Approved or QaCaseStatuses.Rejected => "Close case",
        _ => "—"
    };

    public static string Audit(string? eventType) => (eventType ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        QaAuditEvents.Created => "Created",
        QaAuditEvents.Saved => "Saved",
        QaAuditEvents.Submitted => "Submitted",
        QaAuditEvents.InspectionStarted => "Inspection started",
        QaAuditEvents.Inspected => "Inspected",
        QaAuditEvents.Disposed => "Disposition saved",
        QaAuditEvents.ReviewSubmitted => "Submitted for review",
        QaAuditEvents.Approved => "Approved",
        QaAuditEvents.Rejected => "Rejected",
        QaAuditEvents.Closed => "Closed",
        QaAuditEvents.Cancelled => "Cancelled",
        QaAuditEvents.GrantIssued => "Existing posting allowance recorded",
        QaAuditEvents.AllocationPosted => "Linked document posted",
        QaAuditEvents.AllocationReversed => "Linked document reversed",
        QaAuditEvents.ReopenedForReissue => "Reopened for reissue",
        QaAuditEvents.HoldPlaced => "Hold recorded",
        QaAuditEvents.HoldReleased => "Hold released",
        QaAuditEvents.ActionCreated => "Follow-up created",
        QaAuditEvents.ActionStarted => "Follow-up started",
        QaAuditEvents.ActionReassigned => "Follow-up reassigned",
        QaAuditEvents.ActionCompleted => "Follow-up completed",
        QaAuditEvents.ActionCancelled => "Follow-up cancelled",
        QaAuditEvents.ReinspectionStarted => "Reinspection started",
        _ => Blank(eventType)
    };

    public static string ActionType(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        QaActionTypes.Reject => "Reject follow-up",
        QaActionTypes.Rework => "Rework follow-up",
        QaActionTypes.Hold => "Hold follow-up",
        QaActionTypes.FollowUp => "Follow-up",
        _ => Blank(value)
    };

    public static string ActionStatus(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        QaActionStatuses.Open => "Open",
        QaActionStatuses.InProgress => "In progress",
        QaActionStatuses.Done => "Done",
        QaActionStatuses.Cancelled => "Cancelled",
        _ => Blank(value)
    };

    public static string WorkflowStep(string? status) => (status ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        QaCaseStatuses.Submitted or QaCaseStatuses.InInspection => "inspect",
        QaCaseStatuses.PendingReview => "review",
        QaCaseStatuses.Approved or QaCaseStatuses.Rejected or QaCaseStatuses.Closed or QaCaseStatuses.Cancelled => "followup",
        _ => "source"
    };

    private static string Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
}
