using System.Security.Cryptography;
using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class MachineScheduleSegmentBackfillReport
{
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string PreviewToken { get; init; } = string.Empty;
    public bool CanApply { get; init; }
    public int ActiveMachineCount { get; init; }
    public int AlreadySegmentedCount { get; init; }
    public int MissingSegmentCount { get; init; }
    public int UnverifiableCount { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = [];
}

public interface IMachineScheduleSegmentBackfillService
{
    Task<IvMasterOperationResult<MachineScheduleSegmentBackfillReport>> PreviewAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MachineScheduleSegmentBackfillReport>> ApplyAsync(
        string previewToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the missing <c>ORIGINAL</c> segment for already-active one-span schedules and links
/// their current allocation fragments. It never changes dates, quantities, revisions, snapshot
/// fields, or production facts. This is migration/readiness repair, not an operator action.
/// </summary>
public sealed class MachineScheduleSegmentBackfillService : IMachineScheduleSegmentBackfillService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public MachineScheduleSegmentBackfillService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<IvMasterOperationResult<MachineScheduleSegmentBackfillReport>> PreviewAsync(
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var report = await BuildReportAsync(db, authorization.Scope!, cancellationToken);
        return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Ok(report.Report);
    }

    public async Task<IvMasterOperationResult<MachineScheduleSegmentBackfillReport>> ApplyAsync(
        string previewToken, CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(cancellationToken);
        if (authorization.Error is not null)
            return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(
                authorization.Error.Value.Code, authorization.Error.Value.Message);
        if (string.IsNullOrWhiteSpace(previewToken))
            return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(
                IvMasterErrorCode.Validation, "Run a fresh backfill preview before applying.");

        var scope = authorization.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var seed = await BuildReportAsync(db, scope, cancellationToken);
            await ProductionMachineScheduleLock.AcquireManyAsync(
                db, scope.CompanyCode, seed.Candidates.Select(x => x.MachineCode), cancellationToken);
            await WorkOrderSchedulingLock.AcquireAsync(
                db, scope.CompanyCode, exclusive: false, cancellationToken);
            foreach (var workOrderId in seed.Candidates.Select(x => x.WorkOrderId).Distinct().Order())
            {
                await ProductionWorkOrderExecutionLock.AcquireAsync(
                    db, scope.CompanyCode, scope.BranchCode!, workOrderId, cancellationToken);
            }

            var current = await BuildReportAsync(db, scope, cancellationToken);
            if (!current.Report.CanApply)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(
                    IvMasterErrorCode.Validation,
                    "Segment backfill is blocked because one or more active schedules could not be verified.");
            }
            if (!string.Equals(current.Report.PreviewToken, previewToken, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(
                    IvMasterErrorCode.Concurrency, "Schedule inputs changed after preview. Run the preview again.");
            }

            var now = DateTime.UtcNow;
            var created = new List<(Candidate candidate, ProductionMachineScheduleSegment segment)>();
            var setCandidates = new List<(Candidate candidate, ProductionWorkOrderMachine machine,
                ProductionWorkOrder order, ProductionMachineScheduleSegmentSet set)>();
            foreach (var candidate in current.Candidates)
            {
                var machine = await db.ProductionWorkOrderMachines
                    .Include(x => x.Operation)
                    .FirstAsync(x => x.Uid == candidate.MachineId, cancellationToken);
                var order = new ProductionWorkOrder
                {
                    Uid = candidate.WorkOrderId,
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode!,
                    PlannedStartDateTime = candidate.MachineStart!.Value,
                    PlannedCompletionDateTime = candidate.MachineCompletion!.Value
                };
                var set = MachineScheduleSegmentPersistence.BuildCurrentSet(
                    order, machine.Operation!, machine,
                    ProductionMachineScheduleSegmentSetModes.FullPlan,
                    ProductionMachineSchedulePlanningStates.Scheduled,
                    candidate.PlannedQty,
                    "Backfilled existing one-span active schedule.",
                    scope.UserId, now, db);
                db.ProductionMachineScheduleSegmentSets.Add(set);
                setCandidates.Add((candidate, machine, order, set));
            }

            if (setCandidates.Count > 0)
                await db.SaveChangesAsync(cancellationToken);

            foreach (var (candidate, machine, order, set) in setCandidates)
            {
                var segment = MachineScheduleSegmentPersistence.BuildOriginalSegment(
                    order, machine.Operation!, machine, scope.UserId, now, db);
                segment.SegmentSetId = set.Uid;
                segment.SegmentSequence = 1;
                segment.ReasonCode = "BACKFILL";
                segment.Reason = "Backfilled existing one-span active schedule.";
                segment.PlannedOutputQty = candidate.PlannedQty;
                segment.OutputUom = machine.Operation!.PlannedOutputUom;
                db.ProductionMachineScheduleSegments.Add(segment);
                created.Add((candidate, segment));
            }

            await db.SaveChangesAsync(cancellationToken);

            foreach (var (candidate, segment) in created)
            {
                var allocations = await db.ProductionMachineScheduleAllocations
                    .Where(x => x.WorkOrderMachineId == candidate.MachineId
                        && x.ScheduleRevision == candidate.ScheduleRevision)
                    .ToListAsync(cancellationToken);
                var fragmentNo = 1;
                foreach (var allocation in allocations.OrderBy(x => x.FragmentNo))
                {
                    allocation.MachineScheduleSegmentId = segment.Uid;
                    allocation.FragmentNo = fragmentNo++;
                    MachineScheduleSegmentPersistence.AddAllocationLink(
                        db, allocation, segment, scope.UserId, now);
                }
            }

            foreach (var group in created.GroupBy(x => x.candidate.WorkOrderId))
            {
                db.ProductionAuditEvents.Add(new ProductionAuditEvent
                {
                    WorkOrderId = group.Key,
                    EventType = ProductionAuditEventTypes.ScheduleSegmented,
                    FromStatus = ProductionWorkOrderStatuses.Released,
                    ToStatus = ProductionWorkOrderStatuses.Released,
                    SnapshotRevision = 0,
                    Reason = "Machine schedule segment backfill",
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        Segments = group.Select(x => new
                        {
                            SegmentId = x.segment.Uid,
                            x.candidate.MachineId,
                            x.candidate.MachineCode,
                            x.candidate.ScheduleRevision
                        }).ToList()
                    }),
                    OccurredDate = now,
                    ActorUserId = scope.UserId
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            var completed = await BuildReportAsync(db, scope, cancellationToken);
            return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Ok(completed.Report);
        }
        catch (ProductionMachineScheduleLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch (ProductionWorkOrderExecutionLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<MachineScheduleSegmentBackfillReport>.Fail(IvMasterErrorCode.Concurrency, ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<(MachineScheduleSegmentBackfillReport Report, List<Candidate> Candidates)> BuildReportAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var rows = await db.ProductionWorkOrderMachines.AsNoTracking()
            .Where(x => x.IsSelected
                && x.Operation != null
                && x.Operation.WorkOrder != null
                && x.Operation.WorkOrder.CompanyCode == scope.CompanyCode
                && x.Operation.WorkOrder.BranchCode == scope.BranchCode
                && (x.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || x.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress))
            .Select(x => new Candidate
            {
                WorkOrderId = x.Operation!.WorkOrder!.Uid,
                WorkOrderNo = x.Operation.WorkOrder.WorkOrderNo,
                OperationId = x.OperationId,
                MachineId = x.Uid,
                MachineCode = x.MachineCode,
                ScheduleRevision = x.ScheduleRevision,
                MachineStart = x.PlannedStartDateTime,
                MachineCompletion = x.PlannedCompletionDateTime,
                PlannedQty = x.Operation.PlannedOutputQty,
                OperationStart = x.Operation.PlannedStartDateTime,
                OperationCompletion = x.Operation.PlannedCompletionDateTime
            })
            .ToListAsync(cancellationToken);

        var machineIds = rows.Select(x => x.MachineId).ToList();
        var segments = new List<ProductionMachineScheduleSegment>();
        var allocations = new List<ProductionMachineScheduleAllocation>();
        var sets = new List<ProductionMachineScheduleSegmentSet>();
        var links = new List<ProductionMachineScheduleAllocationSegment>();
        foreach (var batch in machineIds.Chunk(800))
        {
            segments.AddRange(await db.ProductionMachineScheduleSegments.AsNoTracking()
                .Where(x => batch.Contains(x.WorkOrderMachineId)
                    && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
                .ToListAsync(cancellationToken));
            var batchAllocations = await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => batch.Contains(x.WorkOrderMachineId))
                .ToListAsync(cancellationToken);
            allocations.AddRange(batchAllocations);
            foreach (var allocationBatch in batchAllocations.Select(x => x.Uid).Chunk(800))
                links.AddRange(await db.ProductionMachineScheduleAllocationSegments.AsNoTracking()
                    .Where(x => allocationBatch.Contains(x.AllocationId))
                    .ToListAsync(cancellationToken));
            sets.AddRange(await db.ProductionMachineScheduleSegmentSets.AsNoTracking()
                .Where(x => batch.Contains(x.WorkOrderMachineId) && x.IsCurrent)
                .ToListAsync(cancellationToken));
        }
        var segmentsByMachine = segments.GroupBy(x => x.WorkOrderMachineId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var allocationsByMachine = allocations.GroupBy(x => x.WorkOrderMachineId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var setsByMachine = sets.GroupBy(x => x.WorkOrderMachineId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var linkedAllocationIds = links.Select(x => x.AllocationId).ToHashSet();

        var findings = new List<string>();
        var candidates = new List<Candidate>();
        var alreadySegmented = 0;
        foreach (var row in rows.OrderBy(x => x.MachineCode, StringComparer.Ordinal).ThenBy(x => x.MachineId))
        {
            allocationsByMachine.TryGetValue(row.MachineId, out var machineAllocations);
            machineAllocations ??= [];
            segmentsByMachine.TryGetValue(row.MachineId, out var machineSegments);
            machineSegments ??= [];
            var currentAllocations = machineAllocations
                .Where(x => x.ScheduleRevision == row.ScheduleRevision)
                .OrderBy(x => x.FragmentNo)
                .ToList();
            var currentSegments = machineSegments
                .Where(x => x.MachineScheduleRevision == row.ScheduleRevision)
                .ToList();
            setsByMachine.TryGetValue(row.MachineId, out var machineSets);
            machineSets ??= [];
            if (machineSets.Count > 1)
            {
                findings.Add($"Work Order {row.WorkOrderNo}, machine {row.MachineCode} has multiple current SegmentSets.");
                continue;
            }
            if (currentSegments.Count > 0)
            {
                var currentSet = machineSets.SingleOrDefault();
                if (currentSet is null || currentSet.MachineScheduleRevision != row.ScheduleRevision
                    || currentSegments.Any(x => x.SegmentSetId != currentSet.Uid)
                    || currentAllocations.Any(x => !linkedAllocationIds.Contains(x.Uid)))
                {
                    findings.Add($"Work Order {row.WorkOrderNo}, machine {row.MachineCode} has current segments without a complete current SegmentSet and allocation links; run the SegmentSet schema/data migration.");
                    continue;
                }
                alreadySegmented++;
                continue;
            }
            if (machineSets.Count > 0)
            {
                var currentSet = machineSets[0];
                if (currentSet.MachineScheduleRevision == row.ScheduleRevision
                    && currentAllocations.Count == 0
                    && currentSet.PlanningState is
                        ProductionMachineSchedulePlanningStates.HeldUnscheduled or
                        ProductionMachineSchedulePlanningStates.MaintenanceUnscheduled or
                        ProductionMachineSchedulePlanningStates.Completed)
                {
                    alreadySegmented++;
                    continue;
                }
                findings.Add($"Work Order {row.WorkOrderNo}, machine {row.MachineCode} has a current SegmentSet without segments; repair its plan before backfill.");
                continue;
            }
            if (row.MachineStart is null || row.MachineCompletion is null
                || row.OperationStart is null || row.OperationCompletion is null
                || row.MachineCompletion <= row.MachineStart)
            {
                findings.Add($"Work Order {row.WorkOrderNo}, machine {row.MachineCode} has incomplete saved dates.");
                continue;
            }
            if (currentAllocations.Count == 0)
            {
                findings.Add($"Work Order {row.WorkOrderNo}, machine {row.MachineCode} has no current allocation fragments; run the controlled allocation backfill first.");
                continue;
            }
            if (currentAllocations.Any(x => x.MachineScheduleSegmentId is not null)
                || currentAllocations.Any(x => x.EndDateTime <= x.StartDateTime))
            {
                findings.Add($"Work Order {row.WorkOrderNo}, machine {row.MachineCode} has invalid current allocation fragments.");
                continue;
            }
            candidates.Add(row);
        }

        var token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                scope.CompanyCode,
                scope.BranchCode,
                Candidates = candidates.OrderBy(x => x.MachineId).Select(x => new
                {
                    x.MachineId,
                    x.ScheduleRevision,
                    x.MachineCode,
                    x.MachineStart,
                    x.MachineCompletion,
                    x.PlannedQty
                }).ToList(),
                Segments = segments.OrderBy(x => x.Uid).Select(x => new
                {
                    x.Uid,
                    x.WorkOrderMachineId,
                    x.MachineScheduleRevision,
                    x.SegmentStatus
                }).ToList(),
                Sets = sets.OrderBy(x => x.Uid).Select(x => new
                {
                    x.Uid, x.WorkOrderMachineId, x.MachineScheduleRevision,
                    x.IsCurrent, x.PlanningState
                }).ToList(),
                Links = links.OrderBy(x => x.Uid).Select(x => new
                {
                    x.Uid, x.AllocationId, x.SegmentId
                }).ToList(),
                Allocations = allocations.OrderBy(x => x.Uid).Select(x => new
                {
                    x.Uid,
                    x.WorkOrderMachineId,
                    x.ScheduleRevision,
                    x.FragmentNo,
                    x.StartDateTime,
                    x.EndDateTime,
                    x.MachineScheduleSegmentId
                }).ToList()
            })));

        return (new MachineScheduleSegmentBackfillReport
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode ?? string.Empty,
            PreviewToken = token,
            CanApply = findings.Count == 0,
            ActiveMachineCount = rows.Count,
            AlreadySegmentedCount = alreadySegmented,
            MissingSegmentCount = candidates.Count,
            UnverifiableCount = findings.Count,
            Findings = findings
        }, candidates);
    }

    private async Task<(InventoryTenantScope? Scope, (IvMasterErrorCode Code, string Message)? Error)> AuthorizeAsync(
        CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return (null, (IvMasterErrorCode.InvalidScope, "A company and branch context is required."));
        if (!await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, cancellationToken)
            || !await _access.CanAsync(MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Edit, cancellationToken))
            return (null, (IvMasterErrorCode.AccessDenied, "Edit access to Machine Schedule Planner is required."));
        return (scope, null);
    }

    private sealed class Candidate
    {
        public long WorkOrderId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
        public long OperationId { get; init; }
        public long MachineId { get; init; }
        public string MachineCode { get; init; } = string.Empty;
        public int ScheduleRevision { get; init; }
        public DateTime? MachineStart { get; init; }
        public DateTime? MachineCompletion { get; init; }
        public DateTime? OperationStart { get; init; }
        public DateTime? OperationCompletion { get; init; }
        public decimal PlannedQty { get; init; }
    }
}
