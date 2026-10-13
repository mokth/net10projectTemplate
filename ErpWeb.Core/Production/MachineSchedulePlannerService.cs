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
    public bool IsLocked { get; set; }
    public string? LockReason { get; set; }
    public string ExecutionStatus { get; set; } = "NOT STARTED";
    public bool IsBackwardScheduled =>
        string.Equals(SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.OrdinalIgnoreCase);
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
    public bool HasMoreJobsThanLimit { get; init; }
    public bool HasMoreIdleMachinesThanLimit { get; init; }
    public MachineSchedulePlannerReadiness Readiness { get; init; } = new();
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
        if (search is not null)
            queryRows = queryRows.Where(o => o.WorkOrder!.WorkOrderNo.Contains(search)
                || o.WorkOrder.ProductCode.Contains(search)
                || (o.RouteStep!.OutputItemCode != null && o.RouteStep.OutputItemCode.Contains(search))
                || o.OperationCode.Contains(search));

        var selected = queryRows.SelectMany(o => o.Machines.Where(m => m.IsSelected).Select(m => new
            {
                Order = o.WorkOrder!,
                Step = o.RouteStep!,
                Operation = o,
                Machine = m
            }))
            .Where(x => x.Machine.PlannedStartDateTime != null
                && x.Machine.PlannedCompletionDateTime != null
                && x.Machine.PlannedStartDateTime < windowEnd
                && x.Machine.PlannedCompletionDateTime > windowStart);

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

        var machineRows = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode && x.Active
                && (machineFilter == null || x.MachineCd == machineFilter))
            .OrderBy(x => x.MachineCd)
            .Select(x => new { x.MachineCd, x.MachineDes })
            .Take(501)
            .ToListAsync(cancellationToken);
        var hasMoreIdleMachinesThanLimit = machineRows.Count > 500;
        if (hasMoreIdleMachinesThanLimit)
            machineRows = machineRows.Take(500).ToList();

        var machineCodes = machineRows.Select(x => x.MachineCd)
            .Concat(jobs.Select(x => x.MachineCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var calendars = await LoadMachineCalendarsAsync(
            db,
            scope.CompanyCode,
            machineCodes,
            DateOnly.FromDateTime(windowStart).AddDays(-1),
            DateOnly.FromDateTime(windowEnd).AddDays(1),
            cancellationToken);

        var resources = machineCodes.Select(code =>
        {
            var master = machineRows.FirstOrDefault(x => string.Equals(x.MachineCd, code, StringComparison.OrdinalIgnoreCase));
            calendars.TryGetValue(code, out var calendar);
            return new MachineSchedulePlannerResource
            {
                MachineCode = code,
                DisplayName = string.IsNullOrWhiteSpace(master?.MachineDes) ? code : $"{code} · {master.MachineDes}",
                MachineDescription = master?.MachineDes,
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

        var preventiveRows = await db.PrPreventives.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode
                && machineCodes.Contains(x.MachineCd)
                && x.Status == PreventiveStatuses.Planned
                && x.DownDt >= windowStart.Date.AddDays(-1)
                && x.DownDt < windowEnd.Date.AddDays(1))
            .Select(x => new { x.Uid, x.MachineCd, x.DownDt, x.StartTm, x.EndTm, x.ReasonCd, x.Remark })
            .ToListAsync(cancellationToken);

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
                ResourceId = row.MachineCd,
                LabelId = 3,
                IsMaintenance = true
            });
        }

        var readiness = await _readiness.GetReadinessAsync(cancellationToken);
        var board = new MachineSchedulePlannerBoard
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Machines = resources,
            Jobs = jobs,
            Appointments = appointments,
            LockedJobCount = jobs.Count(x => x.IsLocked),
            UnverifiedJobCount = unverified,
            HasMoreJobsThanLimit = hasMoreJobsThanLimit,
            HasMoreIdleMachinesThanLimit = hasMoreIdleMachinesThanLimit,
            Readiness = readiness
        };

        return IvMasterOperationResult<MachineSchedulePlannerBoard>.Ok(board);
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

    private static async Task<MachineSchedulePlannerLookupPage> GetWorkCentreLookupPageAsync(
        AppDbContext db, string companyCode, string branchCode, string? search, int skip, int take, CancellationToken ct)
    {
        var rows = db.ProductionWorkOrderRouteSteps.AsNoTracking()
            .Where(step => step.WorkOrder != null
                && step.WorkOrder.CompanyCode == companyCode
                && step.WorkOrder.BranchCode == branchCode
                && (step.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || step.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress))
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
                    || operation.RouteStep.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress))
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
                    || order.Status == ProductionWorkOrderStatuses.InProgress));
        if (search is not null)
            rows = rows.Where(order => order.WorkOrderNo.Contains(search)
                || order.ProductCode.Contains(search)
                || (order.ProductDescription != null && order.ProductDescription.Contains(search)));

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderBy(order => order.WorkOrderNo).Skip(skip).Take(take)
            .Select(order => new MachineSchedulePlannerLookupOption
            {
                Key = order.WorkOrderNo,
                Description = order.ProductCode + " · " + order.Status
                    + (order.ProductDescription == null ? string.Empty : " · " + order.ProductDescription)
            })
            .ToListAsync(ct);
        return new MachineSchedulePlannerLookupPage { Items = items, TotalCount = total };
    }

    private static async Task<MachineSchedulePlannerLookupPage> GetProductLookupPageAsync(
        AppDbContext db, string companyCode, string branchCode, string? search, int skip, int take, CancellationToken ct)
    {
        var rows = db.ProductionWorkOrders.AsNoTracking()
            .Where(order => order.CompanyCode == companyCode && order.BranchCode == branchCode
                && (order.Status == ProductionWorkOrderStatuses.Released
                    || order.Status == ProductionWorkOrderStatuses.InProgress))
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
            + $"\nExecution: {job.ExecutionStatus}"
            + (job.LockReason is null ? string.Empty : $"\nRescheduling blocked: {job.LockReason}");

        return new MachineSchedulePlannerAppointment
        {
            Id = $"wo-{job.OperationId}-{fragmentNo}",
            Subject = subject,
            Description = details,
            Location = $"operation:{job.OperationId}",
            Start = start,
            End = end,
            ResourceId = job.MachineCode,
            LabelId = job.IsLocked ? 2 : 1,
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
        var start = horizonStart.ToDateTime(TimeOnly.MinValue);
        var end = horizonEnd.ToDateTime(TimeOnly.MaxValue);
        var calendars = await db.PrShiftCalendars.AsNoTracking()
            .Where(x => x.CompCode == companyCode && machineCodes.Contains(x.MachineCode) && x.Dt >= start && x.Dt <= end)
            .ToListAsync(ct);
        var groupCodes = calendars.Where(x => CalendarYearFingerprint.NormalizeDateCd(x.DateCd) == "W"
                && !string.IsNullOrWhiteSpace(x.ShfGrpCd))
            .Select(x => x.ShfGrpCd!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shiftsByGroup = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase);
        if (groupCodes.Count != 0)
        {
            var groupRows = await db.PrShiftGroups.AsNoTracking()
                .Where(x => x.CompCode == companyCode && groupCodes.Contains(x.ShfGrpCd))
                .ToListAsync(ct);
            var shiftCodes = groupRows.Select(x => x.ShiftCd).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shiftRows = await db.PrShifts.AsNoTracking()
                .Where(x => x.CompCode == companyCode && shiftCodes.Contains(x.ShiftCd))
                .ToListAsync(ct);
            var breaks = await db.PrShiftBreaks.AsNoTracking()
                .Where(x => x.CompCode == companyCode && shiftCodes.Contains(x.ShiftCd))
                .ToListAsync(ct);
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
        var preventives = await db.PrPreventives.AsNoTracking()
            .Where(x => x.CompCode == companyCode && machineCodes.Contains(x.MachineCd)
                && x.Status == PreventiveStatuses.Planned
                && x.DownDt >= maintenanceStart && x.DownDt <= maintenanceEnd)
            .ToListAsync(ct);

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
}
