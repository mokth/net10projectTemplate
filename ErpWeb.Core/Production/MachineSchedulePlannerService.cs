using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ErpWeb.Core.Production;

public sealed class MachineSchedulePlannerQuery
{
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public string? WorkCentreCode { get; set; }
    public string? MachineCode { get; set; }
    public string? OperationCode { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? ProductCode { get; set; }
    public string? WorkOrderStatus { get; set; }
    public string? SearchText { get; set; }
    public string? Priority { get; set; }
    public bool LateOnly { get; set; }
    public bool ShowIdleMachines { get; set; }
}

public enum MachineSchedulePlannerLookupKind
{
    WorkCentre,
    Machine,
    Process,
    WorkOrder,
    Product
}

public sealed class MachineSchedulePlannerLookupQuery
{
    public MachineSchedulePlannerLookupKind Kind { get; init; }
    public string? SearchText { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class MachineSchedulePlannerLookupOption
{
    public string Key { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(Description)
        ? Key
        : $"{Key} · {Description}";
}

public sealed class MachineSchedulePlannerLookupPage
{
    public IReadOnlyList<MachineSchedulePlannerLookupOption> Items { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class MachineSchedulePlannerInterval
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
}

public sealed class MachineSchedulePlannerResource
{
    public string MachineCode { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? MachineDescription { get; init; }
    public IReadOnlyList<MachineSchedulePlannerInterval> UsableIntervals { get; init; } = [];
    public IReadOnlyList<MachineSchedulePlannerInterval> MaintenanceIntervals { get; init; } = [];
}

public sealed class MachineSchedulePlannerJob
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string WorkOrderStatus { get; init; } = string.Empty;
    public string SchedulingDirection { get; init; } = ProductionSchedulingDirections.Forward;
    public int ScheduleRevision { get; init; }
    public byte[] WorkOrderRowVersion { get; init; } = [];
    public long OperationId { get; init; }
    public int StageSequence { get; init; }
    public int ProcessSequence { get; init; }
    public string WorkCentreCode { get; init; } = string.Empty;
    public string? WorkCentreDescription { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationDescription { get; init; }
    public string MachineCode { get; init; } = string.Empty;
    public string? MachineDescription { get; init; }
    public long WorkOrderMachineId { get; init; }
    public int MachineScheduleRevision { get; init; }
    public byte[] MachineRowVersion { get; init; } = [];
    public DateTime PlannedStart { get; init; }
    public DateTime PlannedCompletion { get; init; }
    public decimal PlannedQuantity { get; init; }
    public string? OutputUom { get; init; }
    public decimal SetupSeconds { get; init; }
    public decimal ConversionSeconds { get; init; }
    public decimal QueueSeconds { get; init; }
    public decimal PlannedRunMinutes { get; init; }
    public decimal ProductiveDurationMinutes { get; init; }

    /// <summary>Work Order header planned start projected by the main board query.</summary>
    public DateTime WorkOrderPlannedStart { get; init; }

    /// <summary>
    /// Work Order header planned completion projected by the main board query. This is the
    /// schedule-alignment anchor; it is intentionally not the operation completion.
    /// </summary>
    public DateTime WorkOrderPlannedCompletion { get; init; }

    public bool IsLocked { get; set; }
    public string? LockReason { get; set; }
    public string ExecutionStatus { get; set; } = "NOT STARTED";

    // Demand context is populated after the bounded board query has materialized. These members
    // must remain mutable because they are enriched in memory from a batched DR lookup.
    public long? DeliveryRequestId { get; set; }
    public string? DeliveryRequestNo { get; set; }
    public string? DeliveryRequestStatus { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? Priority { get; set; }
    public string? ProjectCode { get; set; }
    public string? WarehouseCode { get; set; }
    public bool DemandAllocationIsActive { get; set; }

    public bool HasActiveDemandContext => DemandAllocationIsActive
        && DeliveryRequestStatus is MachineSchedulePlannerDemandStatuses.Released
            or MachineSchedulePlannerDemandStatuses.InProduction;

    public bool IsLateToRequiredDate => HasActiveDemandContext
        && RequiredDate.HasValue
        && WorkOrderPlannedCompletion.Date > RequiredDate.Value.Date;

    public int LateDays => IsLateToRequiredDate
        ? (WorkOrderPlannedCompletion.Date - RequiredDate.Value.Date).Days
        : 0;

    public bool IsBackwardScheduled =>
        string.Equals(SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Active demand context uses the DR header lifecycle only as a planner-attention gate.
/// It is never interpreted as a customer-fulfilment fact.
/// </summary>
public static class MachineSchedulePlannerDemandStatuses
{
    public const string Released = "RELEASED";
    public const string InProduction = "IN_PRODUCTION";
}

public sealed class MachineSchedulePlannerAppointment
{
    public string Id { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string ResourceId { get; init; } = string.Empty;
    public int LabelId { get; init; }
    public bool IsMaintenance { get; init; }
    public bool IsExternalCapacity { get; init; }
}

/// <summary>Frozen scheduler label IDs. Never reuse these numbers dynamically.</summary>
public static class MachineSchedulePlannerLabelIds
{
    public const int Scheduled = 1;
    public const int ExecutionLocked = 2;
    public const int PreventiveMaintenance = 3;
    public const int LateToRequiredDate = 4;
    public const int ReservedOtherBranch = 5;
}

public sealed class MachineSchedulePlannerBoard
{
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public IReadOnlyList<MachineSchedulePlannerResource> Machines { get; init; } = [];
    public IReadOnlyList<MachineSchedulePlannerJob> Jobs { get; init; } = [];
    public IReadOnlyList<MachineSchedulePlannerAppointment> Appointments { get; init; } = [];
    public int LockedJobCount { get; init; }
    public int UnverifiedJobCount { get; init; }
    public int LateWorkOrderCount { get; init; }
    public int DueThisWindowWorkOrderCount { get; init; }
    public int VisibleMaintenanceMachineCount { get; init; }
    public int ExternalCapacityMachineCount { get; init; }
    public bool AttentionCountsArePartial { get; init; }
    public bool ShowIdleMachines { get; init; }
    public bool HasMoreJobsThanLimit { get; init; }
    public bool HasMoreIdleMachinesThanLimit { get; init; }
    public MachineSchedulePlannerReadiness Readiness { get; init; } = new();

    /// <summary>
    /// True when readiness reports a machine-capacity integrity finding. This is a human-facing
    /// gate only; raw codes stay in diagnostics.
    /// </summary>
    public bool NeedsCapacityAttention => !Readiness.AllocationSchemaReady
        || Readiness.UnverifiedActiveAllocations > 0
        || Readiness.StaleAllocations > 0
        || Readiness.UnexplainedActiveMachineOverlaps > 0;
}

public sealed class MachineSchedulePlannerWorkOrderWindow
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string WorkOrderStatus { get; init; } = string.Empty;
    public DateTime? EarliestMachineStart { get; init; }
    public DateTime? LatestMachineCompletion { get; init; }
    public DateTime WorkOrderPlannedStart { get; init; }
    public DateTime WorkOrderPlannedCompletion { get; init; }

    public bool IsMachineScheduled => EarliestMachineStart.HasValue && LatestMachineCompletion.HasValue;

    /// <summary>Machine time is preferred; the WO header is only a fallback.</summary>
    public DateTime? AnchorStart => EarliestMachineStart ?? (WorkOrderPlannedStart == default ? null : WorkOrderPlannedStart);

    public DateTime? AnchorCompletion => LatestMachineCompletion ?? (WorkOrderPlannedCompletion == default ? null : WorkOrderPlannedCompletion);
}

public sealed class MachineSchedulePlannerReadiness
{
    public bool IsWriteReady { get; init; }
    public bool WriteSettingEnabled { get; init; }
    public bool AllocationSchemaReady { get; init; }
    public bool ActiveAllocationBackfillComplete { get; init; }
    public int UnverifiedActiveAllocations { get; init; }
    public int UnexplainedActiveMachineOverlaps { get; init; }
    public int StaleAllocations { get; init; }
    public bool CalendarAndReleaseEvidenceVerified { get; init; }
    public DateTime? EvidenceVerifiedAtUtc { get; init; }
    public bool BackwardSchedulingWriteSupported { get; init; }
    public IReadOnlyList<string> BlockingReasons { get; init; } = [];
}

public sealed class MachineSchedulePlannerEvidenceRefreshResult
{
    public bool EvidenceRecorded { get; init; }
    public bool WriteSettingEnabled { get; init; }
    public DateTime? VerifiedAtUtc { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingReasons { get; init; } = [];
}

public interface IMachineSchedulePlannerService
{
    Task<IvMasterOperationResult<MachineSchedulePlannerBoard>> GetBoardAsync(
        MachineSchedulePlannerQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MachineSchedulePlannerLookupPage>> GetFilterLookupAsync(
        MachineSchedulePlannerLookupQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MachineSchedulePlannerWorkOrderWindow>> GetWorkOrderMachineScheduleWindowAsync(
        string workOrderNo,
        CancellationToken cancellationToken = default);
}

public interface IMachineSchedulePlannerReadinessService
{
    Task<MachineSchedulePlannerReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>> RefreshEvidenceAsync(
        CancellationToken cancellationToken = default);
}

public sealed class MachineSchedulePlannerReadinessService : IMachineSchedulePlannerReadinessService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly IAppSettingService _settings;

    public MachineSchedulePlannerReadinessService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        IAppSettingService settings)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _settings = settings;
    }

    public async Task<MachineSchedulePlannerReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || !await _access.CanAsync(
                MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, cancellationToken))
        {
            return new MachineSchedulePlannerReadiness
            {
                BlockingReasons = ["A permitted company and branch scope is required."]
            };
        }

        var enabled = await _settings.GetFlagAsync(
            AppSettingModules.Planning,
            AppSettingCatalogue.PlanningKeys.MachineSchedulePlannerWriteEnabled,
            cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var schemaReady = await IsAllocationSchemaReadyAsync(db, cancellationToken);
        var reasons = new List<string>();
        if (!enabled)
            reasons.Add("WRITE_SETTING_DISABLED: Planner writes are disabled by the company setting.");
        if (!schemaReady)
            reasons.Add("ALLOCATION_SCHEMA_NOT_READY: Machine allocation schema, check constraint, or required indexes are not ready.");

        // Work orders are branch-scoped, but the machine calendar and physical allocation key are
        // company + machine code. Readiness therefore covers active loads from every branch.
        var activeMachines = db.ProductionWorkOrderMachines.AsNoTracking()
            .Where(m => m.IsSelected
                && m.Operation != null
                && m.Operation.WorkOrder != null
                && m.Operation.WorkOrder.CompanyCode == scope.CompanyCode
                && (m.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || m.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && (m.SetupSeconds + m.ConversionSeconds + m.QueueSeconds + (m.PlannedRunMinutes * 60m)) > 0m);

        if (!schemaReady)
        {
            var unverified = await activeMachines.CountAsync(cancellationToken);
            reasons.Add("ALLOCATION_BACKFILL_UNVERIFIED: Existing active machine schedules have not completed the allocation backfill preflight.");
            reasons.Add("CALENDAR_RELEASE_EVIDENCE_MISSING: Calendar freshness and release-gate evidence have not been confirmed; writes remain fail-closed.");
            return new MachineSchedulePlannerReadiness
            {
                WriteSettingEnabled = enabled,
                AllocationSchemaReady = false,
                ActiveAllocationBackfillComplete = false,
                UnverifiedActiveAllocations = unverified,
                CalendarAndReleaseEvidenceVerified = false,
                BackwardSchedulingWriteSupported = false,
                BlockingReasons = reasons
            };
        }

        var missing = await activeMachines.CountAsync(m =>
            !db.ProductionMachineScheduleAllocations.Any(a =>
                a.CompanyCode == scope.CompanyCode
                && a.WorkOrderId == m.Operation!.WorkOrder!.Uid
                && a.WorkOrderOperationId == m.OperationId
                && a.WorkOrderMachineId == m.Uid
                && a.ScheduleRevision == m.ScheduleRevision), cancellationToken);

        var orphaned = await db.ProductionMachineScheduleAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == scope.CompanyCode
                && db.ProductionWorkOrders.Any(w => w.Uid == a.WorkOrderId
                    && w.CompanyCode == scope.CompanyCode
                    && (w.Status == ProductionWorkOrderStatuses.Released
                        || w.Status == ProductionWorkOrderStatuses.InProgress))
                && !db.ProductionWorkOrderMachines.Any(m => m.Uid == a.WorkOrderMachineId))
            .CountAsync(cancellationToken);
        var stale = orphaned + await db.ProductionMachineScheduleAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == scope.CompanyCode
                && db.ProductionWorkOrders.Any(w => w.Uid == a.WorkOrderId
                    && w.CompanyCode == scope.CompanyCode
                    && (w.Status == ProductionWorkOrderStatuses.Released
                        || w.Status == ProductionWorkOrderStatuses.InProgress)))
            .Join(db.ProductionWorkOrderMachines.AsNoTracking(),
                a => a.WorkOrderMachineId,
                m => m.Uid,
                (a, m) => new { Allocation = a, Machine = m })
            .CountAsync(x => x.Allocation.ScheduleRevision != x.Machine.ScheduleRevision
                || x.Machine.Operation == null
                    || x.Machine.Operation.WorkOrder == null
                    || !x.Machine.IsSelected
                    || x.Allocation.WorkOrderOperationId != x.Machine.OperationId
                    || x.Allocation.WorkOrderId != x.Machine.Operation.WorkOrder.Uid
                    || x.Allocation.BranchCode != x.Machine.Operation.WorkOrder.BranchCode
                    || x.Allocation.MachineCode != x.Machine.MachineCode
                    || x.Machine.Operation.WorkOrder.CompanyCode != scope.CompanyCode
                    || x.Allocation.ScheduleSourceHash == null
                    || x.Machine.ScheduleSourceHash == null
                    || x.Allocation.ScheduleSourceHash != x.Machine.ScheduleSourceHash, cancellationToken);

        var overlaps = await CountActiveOverlapsAsync(db, scope.CompanyCode, cancellationToken);
        if (missing > 0)
            reasons.Add($"ALLOCATION_BACKFILL_INCOMPLETE: {missing} active machine operation(s) have no verified current allocation fragments.");
        if (stale > 0)
            reasons.Add($"ALLOCATION_PROVENANCE_STALE: {stale} current allocation fragment(s) do not match their saved calendar provenance.");
        if (overlaps > 0)
            reasons.Add($"ACTIVE_MACHINE_OVERLAP: {overlaps} unexplained active machine allocation overlap(s) require review.");

        var evidence = await db.ProductionMachineScheduleReadinessEvidence.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode, cancellationToken);
        var evidenceCurrent = false;
        if (missing == 0 && stale == 0 && overlaps == 0)
        {
            var currentCalendarFingerprint = await MachineScheduleReadinessEvidenceFingerprint
                .ComputeCalendarSourceAsync(db, scope.CompanyCode, cancellationToken);
            evidenceCurrent = evidence is not null
                && evidence.SchemaContractVersion == MachineScheduleReadinessEvidenceFingerprint.SchemaContractVersion
                && evidence.ReleaseContractVersion == MachineScheduleReadinessEvidenceFingerprint.ReleaseContractVersion
                && string.Equals(evidence.CalendarSourceFingerprint, currentCalendarFingerprint, StringComparison.Ordinal);
        }
        if (!evidenceCurrent)
            reasons.Add("CALENDAR_RELEASE_EVIDENCE_MISSING: Run the authorized readiness preflight after calendar and release checks pass.");

        return new MachineSchedulePlannerReadiness
        {
            IsWriteReady = enabled && schemaReady && missing == 0 && stale == 0 && overlaps == 0 && evidenceCurrent,
            WriteSettingEnabled = enabled,
            AllocationSchemaReady = true,
            ActiveAllocationBackfillComplete = missing == 0,
            UnverifiedActiveAllocations = missing,
            UnexplainedActiveMachineOverlaps = overlaps,
            StaleAllocations = stale,
            CalendarAndReleaseEvidenceVerified = evidenceCurrent,
            EvidenceVerifiedAtUtc = evidenceCurrent ? evidence!.VerifiedAtUtc : null,
            BackwardSchedulingWriteSupported = false,
            BlockingReasons = reasons
        };
    }

    public async Task<IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>> RefreshEvidenceAsync(
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        if (!await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, cancellationToken)
            || !await _access.CanAsync(MenuCodes.AdminSettings, PermissionCodes.Edit, cancellationToken))
        {
            return IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>.Fail(
                IvMasterErrorCode.AccessDenied, "Planner access and Admin Settings edit access are required to certify readiness.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsAllocationSchemaReadyAsync(db, cancellationToken))
        {
            return IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>.Ok(new()
            {
                Message = "Readiness evidence was not recorded because the planner schema is not installed.",
                BlockingReasons = ["ALLOCATION_SCHEMA_NOT_READY: Apply scripts/create-machine-schedule-planner.sql first."]
            });
        }

        var machineCodes = await db.ProductionWorkOrderMachines.AsNoTracking()
            .Where(m => m.IsSelected
                && m.Operation != null
                && m.Operation.WorkOrder != null
                && m.Operation.WorkOrder.CompanyCode == scope.CompanyCode
                && (m.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || m.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress))
            .Select(m => m.MachineCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // This matches the writer lock order. The exclusive scheduling-source lock freezes
            // calendar changes, Release, reschedule, and backfill while the evidence is refreshed.
            await ProductionMachineScheduleLock.AcquireManyAsync(
                db, scope.CompanyCode, machineCodes, cancellationToken);
            await WorkOrderSchedulingLock.AcquireAsync(
                db, scope.CompanyCode, exclusive: true, cancellationToken);

            // Recheck all allocation and overlap invariants after acquiring the lock. The only
            // ignored blockers are the disabled feature flag and this certificate being absent or
            // stale; neither is allowed to hide a data-integrity finding.
            var current = await GetReadinessAsync(cancellationToken);
            var blockers = current.BlockingReasons
                .Where(x => !x.StartsWith("WRITE_SETTING_DISABLED:", StringComparison.Ordinal)
                    && !x.StartsWith("CALENDAR_RELEASE_EVIDENCE_MISSING:", StringComparison.Ordinal))
                .ToList();
            if (!current.AllocationSchemaReady
                || !current.ActiveAllocationBackfillComplete
                || current.UnverifiedActiveAllocations != 0
                || current.StaleAllocations != 0
                || current.UnexplainedActiveMachineOverlaps != 0
                || blockers.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>.Ok(new()
                {
                    Message = "Readiness evidence was not recorded. Resolve the listed allocation, provenance, and overlap findings, then run the preflight again.",
                    BlockingReasons = blockers
                });
            }

            var fingerprint = await MachineScheduleReadinessEvidenceFingerprint
                .ComputeCalendarSourceAsync(db, scope.CompanyCode, cancellationToken);
            var evidence = await db.ProductionMachineScheduleReadinessEvidence
                .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode, cancellationToken);
            var verifiedAt = DateTime.UtcNow;
            var actor = string.IsNullOrWhiteSpace(scope.UserId) ? "SYSTEM" : scope.UserId.Trim();
            if (actor.Length > 10)
                actor = actor[..10];

            if (evidence is null)
            {
                db.ProductionMachineScheduleReadinessEvidence.Add(new ProductionMachineScheduleReadinessEvidence
                {
                    CompanyCode = scope.CompanyCode,
                    CalendarSourceFingerprint = fingerprint,
                    SchemaContractVersion = MachineScheduleReadinessEvidenceFingerprint.SchemaContractVersion,
                    ReleaseContractVersion = MachineScheduleReadinessEvidenceFingerprint.ReleaseContractVersion,
                    VerifiedAtUtc = verifiedAt,
                    VerifiedBy = actor
                });
            }
            else
            {
                evidence.CalendarSourceFingerprint = fingerprint;
                evidence.SchemaContractVersion = MachineScheduleReadinessEvidenceFingerprint.SchemaContractVersion;
                evidence.ReleaseContractVersion = MachineScheduleReadinessEvidenceFingerprint.ReleaseContractVersion;
                evidence.VerifiedAtUtc = verifiedAt;
                evidence.VerifiedBy = actor;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var settingEnabled = await _settings.GetFlagAsync(
                AppSettingModules.Planning,
                AppSettingCatalogue.PlanningKeys.MachineSchedulePlannerWriteEnabled,
                cancellationToken);
            return IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>.Ok(new()
            {
                EvidenceRecorded = true,
                WriteSettingEnabled = settingEnabled,
                VerifiedAtUtc = verifiedAt,
                Message = settingEnabled
                    ? "Readiness evidence is recorded. Reload the planner to confirm the runtime checks."
                    : "Readiness evidence is recorded. The planner remains View Only until an Admin Settings editor enables the company write setting."
            });
        }
        catch (ProductionMachineScheduleLockException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>.Fail(
                IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineSchedulePlannerEvidenceRefreshResult>.Fail(
                IvMasterErrorCode.Concurrency, ex.Message);
        }
    }

    internal static async Task<bool> IsAllocationSchemaReadyAsync(AppDbContext db, CancellationToken ct)
    {
        if (!string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
            return false;

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT CASE WHEN OBJECT_ID(N'dbo.PrMachineScheduleAllocation', N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrWorkOrder', N'ScheduleRevision') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrWorkOrderMachine', N'ScheduleRevision') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'CompanyCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'BranchCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'MachineCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'WorkOrderID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'WorkOrderOperationID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'WorkOrderMachineID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'ScheduleRevision') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'FragmentNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'StartDateTime') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'EndDateTime') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'ScheduleSourceHash') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'CreatedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'CreatedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'RowVersion') IS NOT NULL
    AND OBJECT_ID(N'dbo.PrMachineScheduleReadinessEvidence', N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleReadinessEvidence', N'CalendarSourceFingerprint') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleReadinessEvidence', N'SchemaContractVersion') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleReadinessEvidence', N'ReleaseContractVersion') IS NOT NULL
    AND COL_LENGTH(N'dbo.PrMachineScheduleReadinessEvidence', N'VerifiedAtUtc') IS NOT NULL
    AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocation')
                AND name = N'IX_PrMachineScheduleAllocation_Machine_Start_End')
    AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocation')
                AND name = N'UQ_PrMachineScheduleAllocation_Machine_Revision_Fragment')
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocation')
                AND name = N'CK_PrMachineScheduleAllocation_PositiveInterval')
THEN 1 ELSE 0 END;";
            var value = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt32(value) == 1;
        }
        finally
        {
            if (wasClosed)
                await connection.CloseAsync();
        }
    }

    // Machine capacity is company-wide; BranchCode must not hide an allocation on the same machine.
    private static async Task<int> CountActiveOverlapsAsync(AppDbContext db, string companyCode, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT_BIG(*)
FROM dbo.PrMachineScheduleAllocation a
JOIN dbo.PrMachineScheduleAllocation b
  ON b.CompanyCode = a.CompanyCode AND b.MachineCode = a.MachineCode AND b.UID > a.UID
 AND a.StartDateTime < b.EndDateTime AND a.EndDateTime > b.StartDateTime
JOIN dbo.PrWorkOrderMachine ma ON ma.UID = a.WorkOrderMachineID AND ma.ScheduleRevision = a.ScheduleRevision
JOIN dbo.PrWorkOrderMachine mb ON mb.UID = b.WorkOrderMachineID AND mb.ScheduleRevision = b.ScheduleRevision
JOIN dbo.PrWorkOrder wa ON wa.UID = a.WorkOrderID AND wa.Status IN (N'RELEASED', N'IN_PROGRESS')
JOIN dbo.PrWorkOrder wb ON wb.UID = b.WorkOrderID AND wb.Status IN (N'RELEASED', N'IN_PROGRESS')
WHERE a.CompanyCode = @companyCode AND b.CompanyCode = @companyCode;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@companyCode";
            parameter.Value = companyCode;
            command.Parameters.Add(parameter);
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? 0 : checked((int)Convert.ToInt64(value));
        }
        finally
        {
            if (wasClosed)
                await connection.CloseAsync();
        }
    }
}

public sealed class MachineSchedulePlannerService : IMachineSchedulePlannerService
{
    private const int MaxWindowDays = 31;
    private const int MaxBoardJobs = 3000;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly IMachineSchedulePlannerReadinessService _readiness;

    public MachineSchedulePlannerService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        IMachineSchedulePlannerReadinessService readiness)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _readiness = readiness;
    }

    public async Task<IvMasterOperationResult<MachineSchedulePlannerBoard>> GetBoardAsync(
        MachineSchedulePlannerQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<MachineSchedulePlannerBoard>.Fail(
                IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<MachineSchedulePlannerBoard>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        query ??= new MachineSchedulePlannerQuery();
        var (windowStart, windowEnd) = NormalizeWindow(query.WindowStart, query.WindowEnd);
        var machineFilter = Normalize(query.MachineCode);
        var centreFilter = Normalize(query.WorkCentreCode);
        var operationFilter = Normalize(query.OperationCode);
        var workOrderFilter = Normalize(query.WorkOrderNo);
        var productFilter = Normalize(query.ProductCode);
        var statusFilter = Normalize(query.WorkOrderStatus);
        var priorityFilter = Normalize(query.Priority);
        var lateOnly = query.LateOnly;
        var showIdleMachines = query.ShowIdleMachines;
        var search = Normalize(query.SearchText);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var queryRows = db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(o => o.WorkOrder != null
                && o.RouteStep != null
                && o.WorkOrder.CompanyCode == scope.CompanyCode
                && o.WorkOrder.BranchCode == scope.BranchCode
                && (o.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || o.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && o.Machines.Any(m => m.IsSelected
                    && m.PlannedStartDateTime != null
                    && m.PlannedCompletionDateTime != null
                    && m.PlannedStartDateTime < windowEnd
                    && m.PlannedCompletionDateTime > windowStart));

        if (centreFilter is not null)
            queryRows = queryRows.Where(o => o.RouteStep!.WorkCentreCode == centreFilter);
        if (operationFilter is not null)
            queryRows = queryRows.Where(o => o.OperationCode == operationFilter);
        if (statusFilter is not null)
            queryRows = queryRows.Where(o => o.WorkOrder!.Status == statusFilter);
        if (machineFilter is not null)
            queryRows = queryRows.Where(o => o.Machines.Any(m => m.IsSelected && m.MachineCode == machineFilter));
        if (workOrderFilter is not null)
            queryRows = queryRows.Where(o => o.WorkOrder!.WorkOrderNo == workOrderFilter);
        if (productFilter is not null)
            queryRows = queryRows.Where(o => o.WorkOrder!.ProductCode == productFilter);
        if (priorityFilter is not null)
            queryRows = queryRows.Where(o => o.WorkOrder!.DemandAllocations.Any(a =>
                a.IsActive
                && a.DeliveryRequest != null
                && (a.DeliveryRequest.Status == MachineSchedulePlannerDemandStatuses.Released
                    || a.DeliveryRequest.Status == MachineSchedulePlannerDemandStatuses.InProduction)
                && a.DeliveryRequest.Priority == priorityFilter));
        if (lateOnly)
        {
            // Schedule alignment, not fulfilment risk: WO header completion after the current DR
            // required date. Date-only comparison matches the read-model late rule.
            queryRows = queryRows.Where(o => o.WorkOrder!.DemandAllocations.Any(a =>
                a.IsActive
                && a.DeliveryRequest != null
                && (a.DeliveryRequest.Status == MachineSchedulePlannerDemandStatuses.Released
                    || a.DeliveryRequest.Status == MachineSchedulePlannerDemandStatuses.InProduction)
                && o.WorkOrder.PlannedCompletionDateTime.Date > a.DeliveryRequest.RequiredDate.Date));
        }
        if (search is not null)
        {
            queryRows = queryRows.Where(o => o.WorkOrder!.WorkOrderNo.Contains(search)
                || o.WorkOrder.ProductCode.Contains(search)
                || (o.RouteStep!.OutputItemCode != null && o.RouteStep.OutputItemCode.Contains(search))
                || o.OperationCode.Contains(search)
                || o.WorkOrder.DemandAllocations.Any(a =>
                    a.DeliveryRequest != null && a.DeliveryRequest.DeliveryRequestNo.Contains(search)));
        }

        // Explicit join instead of SelectMany over the navigation collection. The latter compiles to
        // CROSS APPLY, which SQLite cannot execute, and this shape is a plain INNER JOIN on both.
        var selected = from m in db.ProductionWorkOrderMachines.AsNoTracking()
            join o in queryRows on m.OperationId equals o.Uid
            where m.IsSelected
                && m.PlannedStartDateTime != null
                && m.PlannedCompletionDateTime != null
                && m.PlannedStartDateTime < windowEnd
                && m.PlannedCompletionDateTime > windowStart
            select new
            {
                Order = o.WorkOrder!,
                Step = o.RouteStep!,
                Operation = o,
                Machine = m
            };

        var jobs = await selected
            .OrderBy(x => x.Machine.MachineCode)
            .ThenBy(x => x.Machine.PlannedStartDateTime)
            .ThenBy(x => x.Order.WorkOrderNo)
            .Take(MaxBoardJobs + 1)
            .Select(x => new MachineSchedulePlannerJob
            {
                WorkOrderId = x.Order.Uid,
                WorkOrderNo = x.Order.WorkOrderNo,
                ProductCode = x.Order.ProductCode,
                ProductDescription = x.Order.ProductDescription,
                WorkOrderStatus = x.Order.Status,
                SchedulingDirection = x.Order.SchedulingDirection,
                ScheduleRevision = x.Order.ScheduleRevision,
                WorkOrderRowVersion = x.Order.RowVersion,
                WorkOrderPlannedStart = x.Order.PlannedStartDateTime,
                WorkOrderPlannedCompletion = x.Order.PlannedCompletionDateTime,
                OperationId = x.Operation.Uid,
                StageSequence = x.Step.StageSequence,
                ProcessSequence = x.Operation.ProcessSequence,
                WorkCentreCode = x.Step.WorkCentreCode,
                WorkCentreDescription = x.Step.WorkCentreDescription,
                OperationCode = x.Operation.OperationCode,
                OperationDescription = x.Operation.OperationDescription,
                MachineCode = x.Machine.MachineCode,
                MachineDescription = x.Machine.MachineDescription,
                WorkOrderMachineId = x.Machine.Uid,
                MachineScheduleRevision = x.Machine.ScheduleRevision,
                MachineRowVersion = x.Machine.RowVersion,
                PlannedStart = x.Machine.PlannedStartDateTime!.Value,
                PlannedCompletion = x.Machine.PlannedCompletionDateTime!.Value,
                PlannedQuantity = x.Operation.PlannedOutputQty,
                OutputUom = x.Operation.PlannedOutputUom ?? x.Step.OutputUom,
                SetupSeconds = x.Machine.SetupSeconds,
                ConversionSeconds = x.Machine.ConversionSeconds,
                QueueSeconds = x.Machine.QueueSeconds,
                PlannedRunMinutes = x.Machine.PlannedRunMinutes,
                ProductiveDurationMinutes = x.Machine.SetupSeconds / 60m
                    + x.Machine.ConversionSeconds / 60m
                    + x.Machine.QueueSeconds / 60m
                    + x.Machine.PlannedRunMinutes,
                IsLocked = x.Operation.ProcessedQty > 0m || x.Operation.GoodQty > 0m
                    || x.Operation.ScrapQty > 0m || x.Operation.RejectQty > 0m
                    || x.Operation.HoldQty > 0m || x.Operation.ReworkQty > 0m
                    || x.Operation.TransferredQty > 0m
            })
            .ToListAsync(cancellationToken);
        var hasMoreJobsThanLimit = jobs.Count > MaxBoardJobs;
        if (hasMoreJobsThanLimit)
            jobs = jobs.Take(MaxBoardJobs).ToList();

        // Demand context is advisory planner information. It is loaded from a bounded, batched
        // lookup after the board query so no per-job database query is issued.
        await EnrichDemandContextAsync(db, scope, jobs, cancellationToken);

        var operationIds = jobs.Select(x => x.OperationId).Distinct().ToList();
        var outputStates = new List<(long OperationId, string Status)>();
        foreach (var operationIdBatch in operationIds.Chunk(1000))
        {
            var batch = await db.ProductionOutputs.AsNoTracking()
                .Where(x => operationIdBatch.Contains(x.WorkOrderOperationId)
                    && ((x.Status == ProductionOutputStatuses.New && x.DeletedAtUtc == null)
                        || x.Status == ProductionOutputStatuses.Posted
                        || x.PostingId != null))
                .Select(x => new { x.WorkOrderOperationId, x.Status, x.PostingId })
                .ToListAsync(cancellationToken);
            outputStates.AddRange(batch.Select(x => (x.WorkOrderOperationId,
                x.PostingId is not null ? ProductionOutputStatuses.Posted : x.Status)));
        }

        var outputStatusesByOperation = outputStates
            .GroupBy(x => x.OperationId)
            .ToDictionary(
                x => x.Key,
                x => x.Select(row => row.Status).ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (var job in jobs)
        {
            outputStatusesByOperation.TryGetValue(job.OperationId, out var outputStatuses);
            if (outputStatuses?.Contains(ProductionOutputStatuses.Posted) == true)
            {
                job.IsLocked = true;
                job.ExecutionStatus = "POSTED";
                job.LockReason = "Posted Daily Production is execution history and blocks rescheduling.";
            }
            else if (outputStatuses?.Contains(ProductionOutputStatuses.New) == true)
            {
                job.IsLocked = true;
                job.ExecutionStatus = "DRAFT EXISTS";
                job.LockReason = "A NEW Daily Production draft exists. Resolve it and recreate it after rescheduling.";
            }
            else if (job.IsLocked)
            {
                job.ExecutionStatus = "EXECUTION STARTED";
                job.LockReason = "Operation execution quantities show that production has started.";
            }
            else if (job.IsBackwardScheduled)
            {
                job.IsLocked = true;
                job.ExecutionStatus = "BACKWARD SCHEDULE";
                job.LockReason = "Backward-scheduled Work Orders remain read-only until their placement rules are proven.";
            }
        }

        var machineRows = new List<MachineMasterRow>();
        var hasMoreIdleMachinesThanLimit = false;
        if (showIdleMachines)
        {
            var idleQuery = db.PrMachines.AsNoTracking()
                .Where(x => x.CompCode == scope.CompanyCode && x.Active
                    && (x.BranchCode == null || x.BranchCode == scope.BranchCode));
            if (machineFilter is not null)
                idleQuery = idleQuery.Where(x => x.MachineCd == machineFilter);
            var idleRows = await idleQuery
                .OrderBy(x => x.MachineCd)
                .Select(x => new MachineMasterRow { MachineCode = x.MachineCd, Description = x.MachineDes })
                .Take(501)
                .ToListAsync(cancellationToken);
            hasMoreIdleMachinesThanLimit = idleRows.Count > 500;
            machineRows.AddRange(hasMoreIdleMachinesThanLimit ? idleRows.Take(500) : idleRows);
        }
        else if (machineFilter is not null)
        {
            // A selected machine must be inspectable even when it currently has zero jobs. Load
            // only that eligible master instead of the whole idle-master set.
            var selectedMaster = await db.PrMachines.AsNoTracking()
                .Where(x => x.CompCode == scope.CompanyCode && x.Active
                    && (x.BranchCode == null || x.BranchCode == scope.BranchCode)
                    && x.MachineCd == machineFilter)
                .Select(x => new MachineMasterRow { MachineCode = x.MachineCd, Description = x.MachineDes })
                .Take(1)
                .ToListAsync(cancellationToken);
            machineRows.AddRange(selectedMaster);
        }

        var machineCodes = machineRows.Select(x => x.MachineCode)
            .Concat(jobs.Select(x => x.MachineCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Scheduled-job machines must still show their master description even when idle masters
        // were skipped or the current master eligibility changed.
        var loadedMasterCodes = machineRows
            .Select(x => x.MachineCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingMasterCodes = machineCodes
            .Where(code => !loadedMasterCodes.Contains(code))
            .ToList();
        foreach (var masterBatch in missingMasterCodes.Chunk(800))
        {
            machineRows.AddRange(await db.PrMachines.AsNoTracking()
                .Where(x => x.CompCode == scope.CompanyCode && masterBatch.Contains(x.MachineCd))
                .Select(x => new MachineMasterRow { MachineCode = x.MachineCd, Description = x.MachineDes })
                .ToListAsync(cancellationToken));
        }

        var calendars = await LoadMachineCalendarsAsync(
            db,
            scope.CompanyCode,
            machineCodes,
            DateOnly.FromDateTime(windowStart).AddDays(-1),
            DateOnly.FromDateTime(windowEnd).AddDays(1),
            cancellationToken);

        var mastersByCode = machineRows
            .GroupBy(x => x.MachineCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var resources = machineCodes.Select(code =>
        {
            mastersByCode.TryGetValue(code, out var master);
            calendars.TryGetValue(code, out var calendar);
            return new MachineSchedulePlannerResource
            {
                MachineCode = code,
                DisplayName = string.IsNullOrWhiteSpace(master?.Description) ? code : $"{code} · {master.Description}",
                MachineDescription = master?.Description,
                UsableIntervals = calendar?.GlobalUsableIntervals.Select(ToInterval).ToList() ?? [],
                MaintenanceIntervals = calendar?.PreventiveWindows.Select(x => new MachineSchedulePlannerInterval
                {
                    Start = x.Start,
                    End = x.End
                }).ToList() ?? []
            };
        }).ToList();

        var appointments = new List<MachineSchedulePlannerAppointment>();
        var unverified = 0;
        foreach (var job in jobs)
        {
            var fragments = TryResolveConsumedIntervals(job, calendars);
            if (fragments is null || fragments.Count == 0)
            {
                unverified++;
                if (job.PlannedStart < windowEnd && job.PlannedCompletion > windowStart)
                {
                    appointments.Add(ToJobAppointment(job, job.PlannedStart, job.PlannedCompletion, 0));
                }
                continue;
            }

            for (var fragmentNo = 0; fragmentNo < fragments.Count; fragmentNo++)
            {
                var interval = fragments[fragmentNo];
                if (interval.Start >= windowEnd || interval.End <= windowStart)
                    continue;
                appointments.Add(ToJobAppointment(job, interval.Start, interval.End, fragmentNo));
            }
        }

        var preventiveRows = new List<PreventiveRow>();
        foreach (var machineBatch in machineCodes.Chunk(800))
        {
            preventiveRows.AddRange(await db.PrPreventives.AsNoTracking()
                .Where(x => x.CompCode == scope.CompanyCode
                    && machineBatch.Contains(x.MachineCd)
                    && x.Status == PreventiveStatuses.Planned
                    && x.DownDt >= windowStart.Date.AddDays(-1)
                    && x.DownDt < windowEnd.Date.AddDays(1))
                .Select(x => new PreventiveRow
                {
                    Uid = x.Uid,
                    MachineCode = x.MachineCd,
                    DownDt = x.DownDt,
                    StartTm = x.StartTm,
                    EndTm = x.EndTm,
                    ReasonCd = x.ReasonCd,
                    Remark = x.Remark
                })
                .ToListAsync(cancellationToken));
        }

        foreach (var row in preventiveRows)
        {
            if (!PreventiveWindowNormalizer.TryNormalize(row.DownDt, row.StartTm, row.EndTm, out var normalized, out _))
                continue;
            if (normalized.WindowStart >= windowEnd || normalized.WindowEnd <= windowStart)
                continue;
            var subject = string.IsNullOrWhiteSpace(row.ReasonCd) ? "PM · Maintenance" : $"PM · {row.ReasonCd}";
            appointments.Add(new MachineSchedulePlannerAppointment
            {
                Id = $"pm-{row.Uid}",
                Subject = subject,
                Description = string.IsNullOrWhiteSpace(row.Remark) ? "Planned preventive maintenance" : row.Remark,
                Location = $"maintenance:{row.Uid}",
                Start = DateTime.SpecifyKind(normalized.WindowStart, DateTimeKind.Unspecified),
                End = DateTime.SpecifyKind(normalized.WindowEnd, DateTimeKind.Unspecified),
                ResourceId = row.MachineCode,
                LabelId = MachineSchedulePlannerLabelIds.PreventiveMaintenance,
                IsMaintenance = true
            });
        }

        // Readiness is loaded before any external allocation query. Older databases may not have
        // the allocation schema installed, and the allocation DbSet must not be touched in that case.
        var readiness = await _readiness.GetReadinessAsync(cancellationToken);
        var externalMachineCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (readiness.AllocationSchemaReady && machineCodes.Count > 0)
            await AddExternalCapacityAppointmentsAsync(
                db,
                scope,
                machineCodes,
                windowStart,
                windowEnd,
                appointments,
                externalMachineCodes,
                cancellationToken);

        var lateWorkOrderCount = jobs.Where(x => x.IsLateToRequiredDate)
            .Select(x => x.WorkOrderId)
            .Distinct()
            .Count();
        var dueThisWindowCount = jobs.Where(x => x.HasActiveDemandContext
                && x.RequiredDate.HasValue
                && x.RequiredDate.Value.Date >= windowStart.Date
                && x.RequiredDate.Value.Date < windowEnd.Date)
            .Select(x => x.WorkOrderId)
            .Distinct()
            .Count();

        var board = new MachineSchedulePlannerBoard
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Machines = resources,
            Jobs = jobs,
            Appointments = appointments,
            LockedJobCount = jobs.Count(x => x.IsLocked),
            UnverifiedJobCount = unverified,
            LateWorkOrderCount = lateWorkOrderCount,
            DueThisWindowWorkOrderCount = dueThisWindowCount,
            VisibleMaintenanceMachineCount = resources.Count(x =>
                x.MaintenanceIntervals.Any(interval => interval.Start < windowEnd && interval.End > windowStart)),
            ExternalCapacityMachineCount = externalMachineCodes.Count,
            AttentionCountsArePartial = hasMoreJobsThanLimit,
            ShowIdleMachines = showIdleMachines,
            HasMoreJobsThanLimit = hasMoreJobsThanLimit,
            HasMoreIdleMachinesThanLimit = hasMoreIdleMachinesThanLimit,
            Readiness = readiness
        };

        return IvMasterOperationResult<MachineSchedulePlannerBoard>.Ok(board);
    }

    public async Task<IvMasterOperationResult<MachineSchedulePlannerWorkOrderWindow>> GetWorkOrderMachineScheduleWindowAsync(
        string workOrderNo,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<MachineSchedulePlannerWorkOrderWindow>.Fail(
                IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<MachineSchedulePlannerWorkOrderWindow>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        var normalizedNo = Normalize(workOrderNo);
        if (normalizedNo is null)
            return IvMasterOperationResult<MachineSchedulePlannerWorkOrderWindow>.Fail(
                IvMasterErrorCode.Validation, "A Work Order number is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ProductionWorkOrders.AsNoTracking()
            .Where(order => order.CompanyCode == scope.CompanyCode
                && order.BranchCode == scope.BranchCode
                && order.WorkOrderNo == normalizedNo)
            .Select(order => new
            {
                order.Uid,
                order.WorkOrderNo,
                order.Status,
                order.PlannedStartDateTime,
                order.PlannedCompletionDateTime,
                EarliestMachineStart = order.Operations
                    .SelectMany(operation => operation.Machines)
                    .Where(machine => machine.IsSelected && machine.PlannedStartDateTime != null)
                    .Min(machine => (DateTime?)machine.PlannedStartDateTime),
                LatestMachineCompletion = order.Operations
                    .SelectMany(operation => operation.Machines)
                    .Where(machine => machine.IsSelected && machine.PlannedCompletionDateTime != null)
                    .Max(machine => (DateTime?)machine.PlannedCompletionDateTime)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
            return IvMasterOperationResult<MachineSchedulePlannerWorkOrderWindow>.Fail(
                IvMasterErrorCode.NotFound, "The Work Order was not found in the current company and branch scope.");

        return IvMasterOperationResult<MachineSchedulePlannerWorkOrderWindow>.Ok(new MachineSchedulePlannerWorkOrderWindow
        {
            WorkOrderId = row.Uid,
            WorkOrderNo = row.WorkOrderNo,
            WorkOrderStatus = row.Status,
            EarliestMachineStart = row.EarliestMachineStart is { } start
                ? DateTime.SpecifyKind(start, DateTimeKind.Unspecified)
                : null,
            LatestMachineCompletion = row.LatestMachineCompletion is { } completion
                ? DateTime.SpecifyKind(completion, DateTimeKind.Unspecified)
                : null,
            WorkOrderPlannedStart = DateTime.SpecifyKind(row.PlannedStartDateTime, DateTimeKind.Unspecified),
            WorkOrderPlannedCompletion = DateTime.SpecifyKind(row.PlannedCompletionDateTime, DateTimeKind.Unspecified)
        });
    }

    public async Task<IvMasterOperationResult<MachineSchedulePlannerLookupPage>> GetFilterLookupAsync(
        MachineSchedulePlannerLookupQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<MachineSchedulePlannerLookupPage>.Fail(
                IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<MachineSchedulePlannerLookupPage>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        query ??= new MachineSchedulePlannerLookupQuery();
        var search = Normalize(query.SearchText);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 40 : query.Take, 1, 100);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var page = query.Kind switch
        {
            MachineSchedulePlannerLookupKind.WorkCentre => await GetWorkCentreLookupPageAsync(
                db, scope.CompanyCode, scope.BranchCode, search, skip, take, cancellationToken),
            MachineSchedulePlannerLookupKind.Machine => await GetMachineLookupPageAsync(
                db, scope.CompanyCode, scope.BranchCode, search, skip, take, cancellationToken),
            MachineSchedulePlannerLookupKind.Process => await GetProcessLookupPageAsync(
                db, scope.CompanyCode, scope.BranchCode, search, skip, take, cancellationToken),
            MachineSchedulePlannerLookupKind.WorkOrder => await GetWorkOrderLookupPageAsync(
                db, scope.CompanyCode, scope.BranchCode, search, skip, take, cancellationToken),
            MachineSchedulePlannerLookupKind.Product => await GetProductLookupPageAsync(
                db, scope.CompanyCode, scope.BranchCode, search, skip, take, cancellationToken),
            _ => new MachineSchedulePlannerLookupPage()
        };

        return IvMasterOperationResult<MachineSchedulePlannerLookupPage>.Ok(page);
    }

    private static async Task EnrichDemandContextAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        IReadOnlyList<MachineSchedulePlannerJob> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
            return;

        var branchCode = scope.BranchCode ?? string.Empty;
        var workOrderIds = jobs.Select(x => x.WorkOrderId).Distinct().ToList();
        var rows = new List<MachineSchedulePlannerDemandRow>();
        foreach (var workOrderBatch in workOrderIds.Chunk(800))
        {
            rows.AddRange(await db.PrWorkOrderDemandAllocations.AsNoTracking()
                .Where(allocation => allocation.CompanyCode == scope.CompanyCode
                    && allocation.BranchCode == branchCode
                    && workOrderBatch.Contains(allocation.WorkOrderId)
                    && allocation.DeliveryRequest != null)
                .Select(allocation => new MachineSchedulePlannerDemandRow
                {
                    WorkOrderId = allocation.WorkOrderId,
                    IsActive = allocation.IsActive,
                    DeliveryRequestId = allocation.DeliveryRequestId,
                    DeliveryRequestNo = allocation.DeliveryRequest!.DeliveryRequestNo,
                    DeliveryRequestStatus = allocation.DeliveryRequest.Status,
                    RequiredDate = allocation.DeliveryRequest.RequiredDate,
                    Priority = allocation.DeliveryRequest.Priority,
                    ProjectCode = allocation.DeliveryRequest.ProjectCode,
                    WarehouseCode = allocation.DeliveryRequest.WarehouseCode
                })
                .ToListAsync(cancellationToken));
        }

        // The unique WorkOrderId bridge should produce one row per Work Order. Ordering is a
        // defensive tie-break only if legacy data contains more than one row.
        var byWorkOrder = rows
            .GroupBy(x => x.WorkOrderId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(x => x.IsActive)
                    .ThenByDescending(x => x.RequiredDate)
                    .First());

        foreach (var job in jobs)
        {
            if (!byWorkOrder.TryGetValue(job.WorkOrderId, out var demand))
                continue;
            job.DeliveryRequestId = demand.DeliveryRequestId;
            job.DeliveryRequestNo = demand.DeliveryRequestNo;
            job.DeliveryRequestStatus = demand.DeliveryRequestStatus;
            job.RequiredDate = demand.RequiredDate;
            job.Priority = demand.Priority;
            job.ProjectCode = demand.ProjectCode;
            job.WarehouseCode = demand.WarehouseCode;
            job.DemandAllocationIsActive = demand.IsActive;
        }
    }

    /// <summary>
    /// Adds anonymous, non-interactive bars for company-wide physical capacity already reserved by
    /// another branch. This method must only be called after readiness confirms the allocation
    /// schema exists. No external Work Order, product, customer, DR, or quantity detail is exposed.
    /// </summary>
    private static async Task AddExternalCapacityAppointmentsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        IReadOnlyList<string> machineCodes,
        DateTime windowStart,
        DateTime windowEnd,
        List<MachineSchedulePlannerAppointment> appointments,
        HashSet<string> externalMachineCodes,
        CancellationToken cancellationToken)
    {
        var branchCode = scope.BranchCode ?? string.Empty;
        foreach (var machineBatch in machineCodes.Chunk(800))
        {
            var rows = await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(allocation => allocation.CompanyCode == scope.CompanyCode
                    && machineBatch.Contains(allocation.MachineCode)
                    && allocation.BranchCode != branchCode
                    && allocation.StartDateTime < windowEnd
                    && allocation.EndDateTime > windowStart)
                .Join(db.ProductionWorkOrderMachines.AsNoTracking(),
                    allocation => allocation.WorkOrderMachineId,
                    machine => machine.Uid,
                    (allocation, machine) => new { Allocation = allocation, Machine = machine })
                .Where(x => x.Machine.IsSelected
                    && x.Machine.Operation != null
                    && x.Machine.Operation.WorkOrder != null
                    && x.Machine.Operation.WorkOrder.CompanyCode == scope.CompanyCode
                    && (x.Machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                        || x.Machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                    && x.Machine.ScheduleRevision == x.Allocation.ScheduleRevision
                    && x.Machine.MachineCode == x.Allocation.MachineCode
                    && x.Machine.OperationId == x.Allocation.WorkOrderOperationId
                    && x.Machine.Operation.WorkOrder.Uid == x.Allocation.WorkOrderId
                    && x.Machine.Operation.WorkOrder.BranchCode == x.Allocation.BranchCode)
                .Select(x => new
                {
                    x.Allocation.Uid,
                    x.Allocation.MachineCode,
                    x.Allocation.StartDateTime,
                    x.Allocation.EndDateTime
                })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                appointments.Add(new MachineSchedulePlannerAppointment
                {
                    Id = $"external-capacity-{row.Uid}",
                    Subject = "Reserved · other branch",
                    Description = "This physical machine capacity is already reserved outside the current branch.",
                    Location = $"external-capacity:{row.Uid}",
                    Start = DateTime.SpecifyKind(row.StartDateTime, DateTimeKind.Unspecified),
                    End = DateTime.SpecifyKind(row.EndDateTime, DateTimeKind.Unspecified),
                    ResourceId = row.MachineCode,
                    LabelId = MachineSchedulePlannerLabelIds.ReservedOtherBranch,
                    IsMaintenance = false,
                    IsExternalCapacity = true
                });
                externalMachineCodes.Add(row.MachineCode);
            }
        }
    }

    private static async Task<MachineSchedulePlannerLookupPage> GetWorkCentreLookupPageAsync(
        AppDbContext db, string companyCode, string branchCode, string? search, int skip, int take, CancellationToken ct)
    {
        var rows = db.ProductionWorkOrderRouteSteps.AsNoTracking()
            .Where(step => step.WorkOrder != null
                && step.WorkOrder.CompanyCode == companyCode
                && step.WorkOrder.BranchCode == branchCode
                && (step.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || step.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && step.Operations.Any(operation => operation.Machines.Any(machine =>
                    machine.IsSelected
                    && machine.PlannedStartDateTime != null
                    && machine.PlannedCompletionDateTime != null)))
            .GroupBy(step => step.WorkCentreCode)
            .Select(group => new { Key = group.Key, Description = group.Max(step => step.WorkCentreDescription) });
        if (search is not null)
            rows = rows.Where(x => x.Key.Contains(search) || (x.Description != null && x.Description.Contains(search)));

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(x => x.Key).Skip(skip).Take(take)
            .Select(x => new MachineSchedulePlannerLookupOption { Key = x.Key, Description = x.Description })
            .ToListAsync(ct);
        return new MachineSchedulePlannerLookupPage { Items = items, TotalCount = total };
    }

    private static async Task<MachineSchedulePlannerLookupPage> GetMachineLookupPageAsync(
        AppDbContext db, string companyCode, string branchCode, string? search, int skip, int take, CancellationToken ct)
    {
        var rows = db.PrMachines.AsNoTracking()
            .Where(machine => machine.CompCode == companyCode && machine.Active
                && (machine.BranchCode == null || machine.BranchCode == branchCode));
        if (search is not null)
            rows = rows.Where(machine => machine.MachineCd.Contains(search)
                || (machine.MachineDes != null && machine.MachineDes.Contains(search))
                || machine.ProcessCd.Contains(search));

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(machine => machine.MachineCd).Skip(skip).Take(take)
            .Select(machine => new MachineSchedulePlannerLookupOption
            {
                Key = machine.MachineCd,
                Description = machine.MachineDes
            })
            .ToListAsync(ct);
        return new MachineSchedulePlannerLookupPage { Items = items, TotalCount = total };
    }

    private static async Task<MachineSchedulePlannerLookupPage> GetProcessLookupPageAsync(
        AppDbContext db, string companyCode, string branchCode, string? search, int skip, int take, CancellationToken ct)
    {
        var rows = db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(operation => operation.RouteStep != null
                && operation.RouteStep.WorkOrder != null
                && operation.RouteStep.WorkOrder.CompanyCode == companyCode
                && operation.RouteStep.WorkOrder.BranchCode == branchCode
                && (operation.RouteStep.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || operation.RouteStep.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && operation.Machines.Any(machine =>
                    machine.IsSelected
                    && machine.PlannedStartDateTime != null
                    && machine.PlannedCompletionDateTime != null))
            .GroupBy(operation => operation.OperationCode)
            .Select(group => new { Key = group.Key, Description = group.Max(operation => operation.OperationDescription) });
        if (search is not null)
            rows = rows.Where(x => x.Key.Contains(search) || (x.Description != null && x.Description.Contains(search)));

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(x => x.Key).Skip(skip).Take(take)
            .Select(x => new MachineSchedulePlannerLookupOption { Key = x.Key, Description = x.Description })
            .ToListAsync(ct);
        return new MachineSchedulePlannerLookupPage { Items = items, TotalCount = total };
    }

    private static async Task<MachineSchedulePlannerLookupPage> GetWorkOrderLookupPageAsync(
        AppDbContext db, string companyCode, string branchCode, string? search, int skip, int take, CancellationToken ct)
    {
        var rows = db.ProductionWorkOrders.AsNoTracking()
            .Where(order => order.CompanyCode == companyCode && order.BranchCode == branchCode
                && (order.Status == ProductionWorkOrderStatuses.Released
                    || order.Status == ProductionWorkOrderStatuses.InProgress)
                && order.Operations.Any(operation => operation.Machines.Any(machine =>
                    machine.IsSelected
                    && machine.PlannedStartDateTime != null
                    && machine.PlannedCompletionDateTime != null)));
        if (search is not null)
            rows = rows.Where(order => order.WorkOrderNo.Contains(search)
                || order.ProductCode.Contains(search)
                || (order.ProductDescription != null && order.ProductDescription.Contains(search)));

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(order => order.WorkOrderNo).Skip(skip).Take(take)
            .Select(order => new
            {
                order.WorkOrderNo,
                order.ProductCode,
                order.ProductDescription,
                order.Status,
                EarliestMachineStart = order.Operations
                    .SelectMany(operation => operation.Machines)
                    .Where(machine => machine.IsSelected && machine.PlannedStartDateTime != null)
                    .Min(machine => (DateTime?)machine.PlannedStartDateTime)
            })
            .ToListAsync(ct);

        return new MachineSchedulePlannerLookupPage
        {
            Items = items.Select(order => new MachineSchedulePlannerLookupOption
            {
                Key = order.WorkOrderNo,
                Description = order.ProductCode + " · " + order.Status
                    + (order.ProductDescription == null ? string.Empty : " · " + order.ProductDescription)
                    + (order.EarliestMachineStart is { } earliest ? $" · {earliest:dd MMM yyyy}" : string.Empty)
            }).ToList(),
            TotalCount = total
        };
    }

    private static async Task<MachineSchedulePlannerLookupPage> GetProductLookupPageAsync(
        AppDbContext db, string companyCode, string branchCode, string? search, int skip, int take, CancellationToken ct)
    {
        var rows = db.ProductionWorkOrders.AsNoTracking()
            .Where(order => order.CompanyCode == companyCode && order.BranchCode == branchCode
                && (order.Status == ProductionWorkOrderStatuses.Released
                    || order.Status == ProductionWorkOrderStatuses.InProgress)
                && order.Operations.Any(operation => operation.Machines.Any(machine =>
                    machine.IsSelected
                    && machine.PlannedStartDateTime != null
                    && machine.PlannedCompletionDateTime != null)))
            .GroupBy(order => order.ProductCode)
            .Select(group => new { Key = group.Key, Description = group.Max(order => order.ProductDescription) });
        if (search is not null)
            rows = rows.Where(x => x.Key.Contains(search) || (x.Description != null && x.Description.Contains(search)));

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(x => x.Key).Skip(skip).Take(take)
            .Select(x => new MachineSchedulePlannerLookupOption { Key = x.Key, Description = x.Description })
            .ToListAsync(ct);
        return new MachineSchedulePlannerLookupPage { Items = items, TotalCount = total };
    }

    private static MachineSchedulePlannerAppointment ToJobAppointment(
        MachineSchedulePlannerJob job,
        DateTime start,
        DateTime end,
        int fragmentNo)
    {
        var subject = $"{job.WorkOrderNo} · {job.ProductCode}";
        var details = $"Work Order: {job.WorkOrderNo}\nProduct: {job.ProductCode}{Suffix(job.ProductDescription)}"
            + $"\nWork Centre: {job.WorkCentreCode}{Suffix(job.WorkCentreDescription)}"
            + $"\nOperation: {job.OperationCode}{Suffix(job.OperationDescription)}"
            + $"\nMachine: {job.MachineCode}{Suffix(job.MachineDescription)}"
            + $"\nPlanned quantity: {job.PlannedQuantity:n4} {job.OutputUom}"
            + $"\nProductive duration: {job.ProductiveDurationMinutes:n1} min"
            + $"\nPlanned start: {job.PlannedStart:dd MMM yyyy HH:mm}"
            + $"\nPlanned completion: {job.PlannedCompletion:dd MMM yyyy HH:mm}"
            + $"\nWork Order status: {job.WorkOrderStatus}"
            + $"\nExecution: {job.ExecutionStatus}";
        if (job.HasActiveDemandContext)
        {
            details += $"\nDR: {job.DeliveryRequestNo}"
                + $"\nDR required date: {job.RequiredDate:dd MMM yyyy}"
                + $"\nPriority: {job.Priority ?? "—"}";
            if (job.IsLateToRequiredDate)
                details += $"\nLate vs DR date: {job.LateDays} calendar day(s)";
        }
        else
        {
            details += "\nDemand source: Manual / no active Delivery Request";
        }
        if (job.LockReason is not null)
            details += $"\nRescheduling blocked: {job.LockReason}";

        return new MachineSchedulePlannerAppointment
        {
            Id = $"wo-{job.OperationId}-{fragmentNo}",
            Subject = subject,
            Description = details,
            Location = $"operation:{job.OperationId}",
            Start = start,
            End = end,
            ResourceId = job.MachineCode,
            LabelId = job.IsLocked
                ? MachineSchedulePlannerLabelIds.ExecutionLocked
                : job.IsLateToRequiredDate
                    ? MachineSchedulePlannerLabelIds.LateToRequiredDate
                    : MachineSchedulePlannerLabelIds.Scheduled,
            IsMaintenance = false
        };
    }

    private static IReadOnlyList<TimeInterval>? TryResolveConsumedIntervals(
        MachineSchedulePlannerJob job,
        IReadOnlyDictionary<string, ScheduleMachineData> calendars)
    {
        if (!calendars.TryGetValue(job.MachineCode, out var calendar)
            || job.PlannedCompletion <= job.PlannedStart)
            return null;

        // Match WorkOrderScheduleCalculator.PlaceOperationAsync exactly. Avoid converting
        // second-based components to decimal minutes and back before asking the canonical placer.
        var duration = TimeSpan.FromSeconds((double)(job.SetupSeconds + job.ConversionSeconds + job.QueueSeconds))
            + TimeSpan.FromMinutes((double)job.PlannedRunMinutes);
        var direction = job.IsBackwardScheduled ? ScheduleDirection.Backward : ScheduleDirection.Forward;
        var anchor = direction == ScheduleDirection.Forward ? job.PlannedStart : job.PlannedCompletion;
        try
        {
            var result = ProductionCalendarScheduler.ScheduleMachine(calendar, anchor, duration, direction);
            return result.ActualStart == job.PlannedStart && result.ActualCompletion == job.PlannedCompletion
                ? result.ConsumedIntervals
                : null;
        }
        catch (ScheduleFailureException)
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, ScheduleMachineData>> LoadMachineCalendarsAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> machineCodes,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken ct)
    {
        if (machineCodes.Count == 0)
            return new Dictionary<string, ScheduleMachineData>(StringComparer.OrdinalIgnoreCase);

        // Load all visible machine calendars in batches. The read model must not issue a query per bar
        // or per resource, and the only date span is the visible window plus overnight ownership.
        // Every IN list is chunked so a 3,000-operation board never exceeds SQL parameter limits.
        var start = horizonStart.ToDateTime(TimeOnly.MinValue);
        var end = horizonEnd.ToDateTime(TimeOnly.MaxValue);
        var calendars = new List<PrShiftCalendar>();
        foreach (var machineBatch in machineCodes.Chunk(800))
        {
            calendars.AddRange(await db.PrShiftCalendars.AsNoTracking()
                .Where(x => x.CompCode == companyCode && machineBatch.Contains(x.MachineCode)
                    && x.Dt >= start && x.Dt <= end)
                .ToListAsync(ct));
        }
        var groupCodes = calendars.Where(x => CalendarYearFingerprint.NormalizeDateCd(x.DateCd) == "W"
                && !string.IsNullOrWhiteSpace(x.ShfGrpCd))
            .Select(x => x.ShfGrpCd!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shiftsByGroup = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase);
        if (groupCodes.Count != 0)
        {
            var groupRows = new List<PrShiftGroup>();
            foreach (var groupBatch in groupCodes.Chunk(800))
            {
                groupRows.AddRange(await db.PrShiftGroups.AsNoTracking()
                    .Where(x => x.CompCode == companyCode && groupBatch.Contains(x.ShfGrpCd))
                    .ToListAsync(ct));
            }
            var shiftCodes = groupRows.Select(x => x.ShiftCd).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shiftRows = new List<PrShift>();
            var breaks = new List<PrShiftBreak>();
            foreach (var shiftBatch in shiftCodes.Chunk(800))
            {
                shiftRows.AddRange(await db.PrShifts.AsNoTracking()
                    .Where(x => x.CompCode == companyCode && shiftBatch.Contains(x.ShiftCd))
                    .ToListAsync(ct));
                breaks.AddRange(await db.PrShiftBreaks.AsNoTracking()
                    .Where(x => x.CompCode == companyCode && shiftBatch.Contains(x.ShiftCd))
                    .ToListAsync(ct));
            }
            var shiftByCode = shiftRows.ToDictionary(x => x.ShiftCd, StringComparer.OrdinalIgnoreCase);
            var breaksByCode = breaks.GroupBy(x => x.ShiftCd, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => (IReadOnlyList<PrShiftBreak>)x.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var group in groupRows.GroupBy(x => x.ShfGrpCd, StringComparer.OrdinalIgnoreCase))
            {
                var definitions = new List<ShiftSourceDefinition>();
                foreach (var member in group)
                {
                    if (!shiftByCode.TryGetValue(member.ShiftCd, out var shift))
                        continue;
                    var childBreaks = breaksByCode.TryGetValue(shift.ShiftCd, out var rows) ? rows : [];
                    definitions.Add(new ShiftSourceDefinition
                    {
                        ShiftCd = shift.ShiftCd,
                        Start = ShiftTimeCalculator.FromLegacyDateTime(shift.StartTm) ?? default,
                        End = ShiftTimeCalculator.FromLegacyDateTime(shift.EndTm) ?? default,
                        Breaks = PrShiftBreakMapper.ResolveBreaks(shift, childBreaks)
                    });
                }
                shiftsByGroup[group.Key] = new ShiftGroupSource { ShiftGroupCode = group.Key, Shifts = definitions };
            }
        }

        var maintenanceStart = horizonStart.ToDateTime(TimeOnly.MinValue).AddDays(-1);
        var maintenanceEnd = horizonEnd.ToDateTime(TimeOnly.MaxValue).AddDays(1);
        var preventives = new List<PrPreventive>();
        foreach (var machineBatch in machineCodes.Chunk(800))
        {
            preventives.AddRange(await db.PrPreventives.AsNoTracking()
                .Where(x => x.CompCode == companyCode && machineBatch.Contains(x.MachineCd)
                    && x.Status == PreventiveStatuses.Planned
                    && x.DownDt >= maintenanceStart && x.DownDt <= maintenanceEnd)
                .ToListAsync(ct));
        }

        var results = new Dictionary<string, ScheduleMachineData>(StringComparer.OrdinalIgnoreCase);
        foreach (var machineCode in machineCodes)
        {
            var calendar = calendars.Where(x => string.Equals(x.MachineCode, machineCode, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => DateOnly.FromDateTime(x.Dt))
                .ToDictionary(
                    group => group.Key,
                    group => new CalendarDayRow
                    {
                        OperationalDate = group.Key,
                        DateCd = CalendarYearFingerprint.NormalizeDateCd(group.First().DateCd),
                        ShfGrpCd = group.First().ShfGrpCd
                    });
            var machineGroupCodes = calendar.Values
                .Where(x => x.DateCd == "W" && !string.IsNullOrWhiteSpace(x.ShfGrpCd))
                .Select(x => x.ShfGrpCd!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var machineShifts = shiftsByGroup.Where(x => machineGroupCodes.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var windows = new List<PreventiveWindow>();
            foreach (var row in preventives.Where(x => string.Equals(x.MachineCd, machineCode, StringComparison.OrdinalIgnoreCase)))
            {
                if (!PreventiveWindowNormalizer.TryNormalize(row.DownDt, row.StartTm, row.EndTm, out var normalized, out _))
                    continue;
                windows.Add(new PreventiveWindow
                {
                    Start = DateTime.SpecifyKind(normalized.WindowStart, DateTimeKind.Unspecified),
                    End = DateTime.SpecifyKind(normalized.WindowEnd, DateTimeKind.Unspecified)
                });
            }

            try
            {
                results[machineCode] = ProductionCalendarScheduler.BuildScheduleData(
                    machineCode, calendar, machineShifts, windows);
            }
            catch (ScheduleFailureException)
            {
                // A malformed calendar remains visible on the board as unverified work.
            }
        }
        return results;
    }

    private static (DateTime Start, DateTime End) NormalizeWindow(DateTime start, DateTime end)
    {
        if (start == default)
        {
            var today = DateTime.Today;
            var mondayOffset = ((int)today.DayOfWeek + 6) % 7;
            start = today.AddDays(-mondayOffset);
        }
        start = DateTime.SpecifyKind(start.Date, DateTimeKind.Unspecified);
        end = end <= start ? start.AddDays(7) : DateTime.SpecifyKind(end.Date, DateTimeKind.Unspecified);
        if (end <= start)
            end = start.AddDays(7);
        if ((end - start).TotalDays > MaxWindowDays)
            end = start.AddDays(MaxWindowDays);
        return (start, end);
    }

    private static MachineSchedulePlannerInterval ToInterval(TimeInterval interval) => new()
    {
        Start = interval.Start,
        End = interval.End
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Suffix(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : $" — {value}";

    private sealed class MachineMasterRow
    {
        public string MachineCode { get; init; } = string.Empty;
        public string? Description { get; init; }
    }

    private sealed class PreventiveRow
    {
        public long Uid { get; init; }
        public string MachineCode { get; init; } = string.Empty;
        public DateTime DownDt { get; init; }
        public DateTime StartTm { get; init; }
        public DateTime EndTm { get; init; }
        public string? ReasonCd { get; init; }
        public string? Remark { get; init; }
    }

    private sealed class MachineSchedulePlannerDemandRow
    {
        public long WorkOrderId { get; init; }
        public bool IsActive { get; init; }
        public long DeliveryRequestId { get; init; }
        public string DeliveryRequestNo { get; init; } = string.Empty;
        public string DeliveryRequestStatus { get; init; } = string.Empty;
        public DateTime RequiredDate { get; init; }
        public string? Priority { get; init; }
        public string? ProjectCode { get; init; }
        public string? WarehouseCode { get; init; }
    }
}
