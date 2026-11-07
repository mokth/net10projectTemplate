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
        "Incoming before GRN: delivery note is text only. Approval later issues a goods-receipt allowance for accepted qty.";

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
        "FG staging inspection is record-only until approval. Approval later issues a finished-goods receipt allowance for accepted qty.";

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
