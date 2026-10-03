namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Stable, persisted error codes for snapshot build / recalculation / Release readiness.
/// <para>
/// These strings are part of the contract between the Product Definition readiness gate, the Work
/// Order snapshot builder, the scheduler and the Release command. They are surfaced to callers
/// (see <c>PrProductionPostingLink.ResultCode</c> and the validation dictionaries) and recorded on
/// audit events, so they must not be reworded once released. Do not scatter these literals:
/// reference the constants.
/// </para>
/// <para>
/// The canonical list is <c>plans/work-order-plan.md</c> §9.1. The codes below that are not in that
/// list are additional, finer-grained quantity-input failures; the plan's list is a minimum, not a
/// maximum.
/// </para>
/// </summary>
public static class ProductionReadinessErrorCodes
{
    // ── Route structure (plan §9.1) ───────────────────────────────────────────────────────────

    /// <summary>No route step exists on a version-2 Work Order.</summary>
    public const string NoRoute = "WO_NO_ROUTE";

    /// <summary>A route step has no process operations.</summary>
    public const string NoOperation = "WO_NO_OPERATION";

    /// <summary>A route step has no valid positive stage sequence.</summary>
    public const string StageSequenceInvalid = "WO_STAGE_SEQUENCE_INVALID";

    /// <summary>An operation has no valid positive process sequence.</summary>
    public const string ProcessSequenceInvalid = "WO_PROCESS_SEQUENCE_INVALID";

    /// <summary>An active route step has no row flagged <c>IsFinalOperation</c>.</summary>
    public const string FinalProcessMissing = "WO_FINAL_PROCESS_MISSING";

    /// <summary>An active route step has more than one final operation.</summary>
    public const string FinalProcessAmbiguous = "WO_FINAL_PROCESS_AMBIGUOUS";

    /// <summary>The final process is not the last process sequence on the route step.</summary>
    public const string FinalProcessNotLast = "WO_FINAL_PROCESS_NOT_LAST";

    /// <summary>The terminal route step's output item does not match the Work Order product.</summary>
    public const string FinalOutputMismatch = "WO_FINAL_OUTPUT_MISMATCH";

    /// <summary>Current snapshot route step is missing OutputType / base UOM / conversion.</summary>
    public const string RouteOutputContractIncomplete = "WO_ROUTE_OUTPUT_CONTRACT_INCOMPLETE";

    /// <summary>Exactly one FINISHED_GOODS route must produce the Work Order product.</summary>
    public const string TerminalFgRouteInvalid = "WO_TERMINAL_FG_ROUTE_INVALID";

    /// <summary>INTERNAL_ROUTE_WIP producer is not WIP_STOCKED.</summary>
    public const string WipProducerNotStocked = "WO_WIP_PRODUCER_NOT_STOCKED";

    /// <summary>Snapshot hash version is below the current execution contract.</summary>
    public const string SnapshotHashVersionStale = "WO_SNAPSHOT_HASH_VERSION_STALE";

    // ── Process and resources (plan §9.1) ─────────────────────────────────────────────────────

    /// <summary>A machine-based operation has no selected/default machine option.</summary>
    public const string MachineRequired = "WO_MACHINE_REQUIRED";

    /// <summary>A non-machine operation has no positive approved duration.</summary>
    public const string OperationDurationRequired = "WO_OPERATION_DURATION_REQUIRED";

    /// <summary>A material has no consuming operation (<c>WorkOrderOperationID</c> is null).</summary>
    public const string MaterialOperationMissing = "WO_MATERIAL_OPERATION_MISSING";

    /// <summary>An <c>INTERNAL_ROUTE_WIP</c> material has no <c>ProducingRouteStepID</c>.</summary>
    public const string WipProducerMissing = "WO_WIP_PRODUCER_MISSING";

    /// <summary>An <c>INTERNAL_ROUTE_WIP</c> item resolves to more than one producer route step.</summary>
    public const string WipProducerAmbiguous = "WO_WIP_PRODUCER_AMBIGUOUS";

    /// <summary>The persisted <c>IssueMethod</c> is unknown; see <see cref="PrMaterialIssueMethods"/>.</summary>
    public const string IssueMethodInvalid = "WO_ISSUE_METHOD_INVALID";

    /// <summary>The persisted <c>SupplySource</c> is unknown; see <see cref="PrMaterialSupplySources"/>.</summary>
    public const string SupplySourceInvalid = "WO_SUPPLY_SOURCE_INVALID";

    /// <summary>An item, UOM, warehouse, machine or labour reference is invalid.</summary>
    public const string ReferenceInvalid = "WO_REFERENCE_INVALID";

    /// <summary>An operation quantity, route output or machine basis has no explicit UOM conversion.</summary>
    public const string OperationUomInvalid = "WO_OPERATION_UOM_INVALID";

    /// <summary>
    /// No approved <c>IvItemUomConversion</c> exists for a required pair, or the pair is
    /// incompatible. Never substitute an assumed factor.
    /// </summary>
    public const string UomConversionMissing = "WO_UOM_CONVERSION_MISSING";

    /// <summary>A labour row's rate basis or basis UOM cannot be resolved.</summary>
    public const string LabourRateUomInvalid = "WO_LABOUR_RATE_UOM_INVALID";

    /// <summary>A duration basis is missing or not a known value.</summary>
    public const string DurationBasisInvalid = "WO_DURATION_BASIS_INVALID";

    /// <summary>The persisted <c>ProcessType</c> is unknown; see <see cref="PrProcessTypes"/>.</summary>
    public const string ProcessTypeInvalid = "WO_PROCESS_TYPE_INVALID";

    // ── Scheduling (plan §9.1) ────────────────────────────────────────────────────────────────

    /// <summary>No calendar covers a required machine or plant-default interval inside the horizon.</summary>
    public const string CalendarCoverageMissing = "WO_CALENDAR_COVERAGE_MISSING";

    /// <summary>A saved calendar/schedule source hash no longer matches the current sources.</summary>
    public const string ScheduleStale = "WO_SCHEDULE_STALE";

    /// <summary>The company scheduling lock could not be acquired (timeout or cancellation).</summary>
    public const string SchedulingSourceBusy = "WO_SCHEDULING_SOURCE_BUSY";

    /// <summary>Schedule timestamps are incomplete or invalid.</summary>
    public const string ScheduleInvalid = "WO_SCHEDULE_INVALID";

    /// <summary>The WIP dependency graph contains a cycle.</summary>
    public const string DependencyCycle = "WO_DEPENDENCY_CYCLE";

    /// <summary>Sequence order contradicts a production dependency.</summary>
    public const string SequenceDependencyConflict = "WO_SEQUENCE_DEPENDENCY_CONFLICT";

    // ── Snapshot lifecycle and concurrency (plan §9.1) ────────────────────────────────────────

    /// <summary>The persisted snapshot hash does not match the recomputed hash.</summary>
    public const string SnapshotHashInvalid = "WO_SNAPSHOT_HASH_INVALID";

    /// <summary>A saved snapshot changed after the caller's preview; reload and re-preview.</summary>
    public const string SnapshotStale = "WO_SNAPSHOT_STALE";

    /// <summary>No active Product Definition revision covers the effective date.</summary>
    public const string DefinitionRevisionNotFound = "WO_DEFINITION_REVISION_NOT_FOUND";

    /// <summary>More than one active revision covers the effective date; never break the tie silently.</summary>
    public const string DefinitionRevisionAmbiguous = "WO_DEFINITION_REVISION_AMBIGUOUS";

    /// <summary>The Product Definition changed during refresh preview; re-preview.</summary>
    public const string DefinitionSourceStale = "WO_DEFINITION_SOURCE_STALE";

    /// <summary>A legacy version-1 snapshot cannot be released until it is explicitly refreshed.</summary>
    public const string LegacySnapshotRefreshRequired = "WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED";

    /// <summary>The row version read by the caller is stale.</summary>
    public const string ConcurrencyConflict = "WO_CONCURRENCY_CONFLICT";

    /// <summary>The new Release path is disabled by configuration (fail-closed).</summary>
    public const string ReleaseDisabled = "WO_RELEASE_DISABLED";

    // ── Additional quantity-input codes ───────────────────────────────────────────────────────

    /// <summary>The Work Order planned quantity is missing or non-positive.</summary>
    public const string WorkOrderQtyInvalid = "WO_WORK_ORDER_QTY_INVALID";

    /// <summary>The Product Definition base quantity is missing or non-positive.</summary>
    public const string DefinitionBaseQtyInvalid = "WO_DEFINITION_BASE_QTY_INVALID";

    /// <summary>A route step's output base quantity is missing or non-positive.</summary>
    public const string RouteOutputBaseQtyInvalid = "WO_ROUTE_OUTPUT_BASE_QTY_INVALID";

    /// <summary>A material's BOM denominator quantity is missing or non-positive.</summary>
    public const string BomOutputQtyInvalid = "WO_BOM_OUTPUT_QTY_INVALID";

    /// <summary>A material's standard quantity is missing or non-positive.</summary>
    public const string MaterialStandardQtyInvalid = "WO_MATERIAL_STANDARD_QTY_INVALID";

    /// <summary>A machine option has a non-positive <c>OutputPerCycle</c>.</summary>
    public const string OutputPerCycleMissing = "WO_OUTPUT_PER_CYCLE_MISSING";

    /// <summary>A machine option has a non-positive <c>ParallelMachineCount</c>.</summary>
    public const string ParallelMachineInvalid = "WO_PARALLEL_MACHINE_INVALID";
}
