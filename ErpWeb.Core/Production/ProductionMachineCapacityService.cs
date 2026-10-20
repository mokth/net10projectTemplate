using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// How one active selected-machine row participates in physical capacity.
/// </summary>
public enum ProductionMachineCapacityClass
{
    /// <summary>Positive productive duration; authoritative occupancy is its current allocations.</summary>
    PositiveCapacity = 0,

    /// <summary>
    /// Legacy zero-duration row that provably reserves no physical capacity: all timing
    /// components are zero, machine and operation timestamps form a single empty instant, and no
    /// positive allocation fragment exists.
    /// </summary>
    LegacyEmptyCapacity = 1,

    /// <summary>Corrupt or inconsistent row. Runtime capacity must fail closed.</summary>
    InvalidCapacityState = 2
}

public sealed class ProductionMachineCapacityFinding
{
    public string Code { get; init; } = string.Empty;
    public string? WorkOrderNo { get; init; }
    public string? OperationCode { get; init; }
    public string? MachineCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsBlocking { get; init; } = true;
}

/// <summary>
/// Company/machine-scoped physical capacity evidence. <see cref="OccupiedByMachine"/> contains
/// only verified current allocations of <see cref="ProductionMachineCapacityClass.PositiveCapacity"/>
/// rows. Legacy empty rows contribute nothing; invalid rows add a blocking finding.
/// </summary>
public sealed class ProductionMachineCapacityEvidence
{
    public Dictionary<string, List<TimeInterval>> OccupiedByMachine { get; } =
        new(StringComparer.Ordinal);

    public List<ProductionMachineCapacityFinding> Findings { get; } = [];
    public List<string> SourceStamps { get; } = [];

    public IReadOnlyList<string> Blockers => Findings
        .Where(x => x.IsBlocking)
        .Select(x => x.Message)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public bool HasBlockers => Findings.Any(x => x.IsBlocking);
    public int PositiveRowCount { get; internal set; }
    public int LegacyEmptyRowCount { get; internal set; }
    public int InvalidRowCount { get; internal set; }

    public IReadOnlyList<TimeInterval> GetOccupied(string machineCode) =>
        OccupiedByMachine.TryGetValue(Normalize(machineCode), out var intervals) ? intervals : [];

    internal static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

/// <summary>
/// The occupancy view the canonical placement engine consumes. It is deliberately window-aware:
/// every horizon attempt requests capacity for that exact horizon so calendar expansion can never
/// outrun the loaded allocations (plan §12.3).
/// </summary>
public sealed class MachineCapacityWindow
{
    public IReadOnlyDictionary<string, List<TimeInterval>> OccupiedByMachine { get; init; } =
        new Dictionary<string, List<TimeInterval>>(StringComparer.Ordinal);

    public IReadOnlyList<string> Blockers { get; init; } = [];
    public IReadOnlyList<string> SourceStamps { get; init; } = [];
}

public interface IProductionMachineCapacityWindowProvider
{
    Task<MachineCapacityWindow> LoadAsync(
        IReadOnlyCollection<string> machineCodes,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken cancellationToken = default);
}

public interface IProductionMachineCapacityService
{
    /// <summary>
    /// Transactional read used by Release, Planner Commit, Reopen and Backfill. It must run on the
    /// caller-owned context/transaction that owns the machine applock.
    /// </summary>
    Task<ProductionMachineCapacityEvidence> LoadAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyCollection<string> machineCodes,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        long? excludedWorkOrderId = null,
        IReadOnlySet<long>? excludedWorkOrderMachineIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only preview used by Draft/Recalculate. It opens its own context because a Draft is
    /// not a reservation; Release revalidates under locks.
    /// </summary>
    Task<ProductionMachineCapacityEvidence> LoadPreviewAsync(
        string companyCode,
        IReadOnlyCollection<string> machineCodes,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        long? excludedWorkOrderId = null,
        IReadOnlySet<long>? excludedWorkOrderMachineIds = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Allocation-authoritative physical capacity reader (plan §9–§12).
/// <para>
/// Positive active rows are verified against their current <c>PrMachineScheduleAllocation</c>
/// fragments. Runtime logic never reconstructs an active job from the raw calendar: a rescheduled
/// Released job intentionally differs from its raw-calendar placement, and reconstructing it would
/// fabricate a false conflict. Raw-calendar reconstruction remains exclusively in the controlled
/// allocation backfill/repair service.
/// </para>
/// </summary>
public sealed class ProductionMachineCapacityService : IProductionMachineCapacityService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ProductionMachineCapacityService(IDbContextFactory<AppDbContext> dbFactory) =>
        _dbFactory = dbFactory;

    public async Task<ProductionMachineCapacityEvidence> LoadPreviewAsync(
        string companyCode,
        IReadOnlyCollection<string> machineCodes,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        long? excludedWorkOrderId = null,
        IReadOnlySet<long>? excludedWorkOrderMachineIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await LoadAsync(
            db, companyCode, machineCodes, windowStart, windowEnd,
            excludedWorkOrderId, excludedWorkOrderMachineIds, cancellationToken);
    }

    public async Task<ProductionMachineCapacityEvidence> LoadAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyCollection<string> machineCodes,
        DateTime? windowStart = null,
        DateTime? windowEnd = null,
        long? excludedWorkOrderId = null,
        IReadOnlySet<long>? excludedWorkOrderMachineIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var codes = machineCodes
            .Select(ProductionMachineCapacityEvidence.Normalize)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var evidence = new ProductionMachineCapacityEvidence();
        foreach (var code in codes)
            evidence.OccupiedByMachine[code] = [];
        if (codes.Count == 0)
            return evidence;

        // Include zero and negative duration rows: classification, not a blind duration filter,
        // decides whether a row is legacy-empty, positive, or corrupt (plan §6).
        var rows = await db.ProductionWorkOrderMachines.AsNoTracking()
            .Where(machine => machine.IsSelected
                && codes.Contains(machine.MachineCode)
                && machine.Operation != null
                && machine.Operation.WorkOrder != null
                && machine.Operation.WorkOrder.CompanyCode == companyCode
                && (machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress))
            .Select(machine => new ActiveCapacityRow
            {
                CompanyCode = machine.Operation!.WorkOrder!.CompanyCode,
                WorkOrderId = machine.Operation.WorkOrder.Uid,
                WorkOrderNo = machine.Operation.WorkOrder.WorkOrderNo,
                BranchCode = machine.Operation.WorkOrder.BranchCode,
                MachineId = machine.Uid,
                MachineCode = machine.MachineCode,
                OperationId = machine.OperationId,
                OperationCode = machine.Operation.OperationCode,
                OperationStart = machine.Operation.PlannedStartDateTime,
                OperationCompletion = machine.Operation.PlannedCompletionDateTime,
                PlannedStart = machine.PlannedStartDateTime,
                PlannedCompletion = machine.PlannedCompletionDateTime,
                ScheduleRevision = machine.ScheduleRevision,
                ScheduleSourceHash = machine.ScheduleSourceHash,
                SetupSeconds = machine.SetupSeconds,
                ConversionSeconds = machine.ConversionSeconds,
                QueueSeconds = machine.QueueSeconds,
                PlannedRunMinutes = machine.PlannedRunMinutes,
                HasExecution = machine.Operation!.InputQty != 0m
                    || machine.Operation.ProcessedQty != 0m
                    || machine.Operation.GoodQty != 0m
                    || machine.Operation.ScrapQty != 0m
                    || machine.Operation.RejectQty != 0m
                    || machine.Operation.HoldQty != 0m
                    || machine.Operation.ReworkQty != 0m
                    || machine.Operation.TransferredQty != 0m
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
            return evidence;

        var machineIds = rows.Select(x => x.MachineId).ToList();
        var allocations = new List<ProductionMachineScheduleAllocation>();
        var segments = new List<ProductionMachineScheduleSegment>();
        foreach (var batch in machineIds.Chunk(800))
        {
            allocations.AddRange(await db.ProductionMachineScheduleAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode && batch.Contains(x.WorkOrderMachineId))
                .OrderBy(x => x.WorkOrderMachineId)
                .ThenBy(x => x.ScheduleRevision)
                .ThenBy(x => x.FragmentNo)
                .ToListAsync(cancellationToken));
            segments.AddRange(await db.ProductionMachineScheduleSegments.AsNoTracking()
                .Where(x => batch.Contains(x.WorkOrderMachineId)
                    && x.SegmentStatus != ProductionMachineScheduleSegmentStatuses.Superseded)
                .OrderBy(x => x.WorkOrderMachineId)
                .ThenBy(x => x.SegmentSequence)
                .ToListAsync(cancellationToken));
        }

        var allocationsByMachine = allocations
            .GroupBy(x => x.WorkOrderMachineId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var segmentsByMachine = segments
            .GroupBy(x => x.WorkOrderMachineId)
            .ToDictionary(x => x.Key, x => x.ToList());

        var ordered = rows
            .OrderBy(x => ProductionMachineCapacityEvidence.Normalize(x.MachineCode), StringComparer.Ordinal)
            .ThenBy(x => x.PlannedStart)
            .ThenBy(x => x.WorkOrderId)
            .ThenBy(x => x.MachineId)
            .ToList();

        foreach (var row in ordered)
        {
            var normalizedMachine = ProductionMachineCapacityEvidence.Normalize(row.MachineCode);
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
                .OrderBy(x => x.SegmentSequence)
                .ToList();
            var hasPositiveFragment = machineAllocations.Any(x => x.EndDateTime > x.StartDateTime);

            evidence.SourceStamps.Add(SourceStamp(row, machineAllocations, currentSegments));

            if ((excludedWorkOrderId is long excludedWorkOrder && row.WorkOrderId == excludedWorkOrder)
                || (excludedWorkOrderMachineIds is not null && excludedWorkOrderMachineIds.Contains(row.MachineId)))
            {
                // Replaced by the current command; never blocks itself and never consumes capacity.
                continue;
            }

            var seconds = ProductionMachineScheduleDuration.ComputeSeconds(
                row.SetupSeconds, row.ConversionSeconds, row.QueueSeconds, row.PlannedRunMinutes);
            var hasNegative = row.SetupSeconds < 0m || row.ConversionSeconds < 0m
                || row.QueueSeconds < 0m || row.PlannedRunMinutes < 0m;

            if (hasNegative)
            {
                AddInvalid(evidence, row, "a negative timing component");
                continue;
            }

            if (seconds > 0m)
            {
                evidence.PositiveRowCount++;
                if (!ValidatePositiveRow(evidence, row, currentAllocations, machineAllocations, currentSegments))
                    continue;

                foreach (var allocation in currentAllocations)
                {
                    if (windowStart is DateTime start && allocation.EndDateTime <= start)
                        continue;
                    if (windowEnd is DateTime end && allocation.StartDateTime >= end)
                        continue;
                    evidence.OccupiedByMachine[normalizedMachine]
                        .Add(new TimeInterval(allocation.StartDateTime, allocation.EndDateTime));
                }

                continue;
            }

            if (TryClassifyLegacyEmpty(row, machineAllocations))
            {
                evidence.LegacyEmptyRowCount++;
                continue;
            }

            AddInvalid(evidence, row, DescribeEmptyRowViolation(row, machineAllocations));
        }

        foreach (var pair in evidence.OccupiedByMachine)
        {
            pair.Value.Sort((left, right) => left.Start != right.Start
                ? left.Start.CompareTo(right.Start)
                : left.End.CompareTo(right.End));
        }

        AddOverlapFindings(evidence);
        evidence.SourceStamps.Sort(StringComparer.Ordinal);
        return evidence;
    }

    private static bool ValidatePositiveRow(
        ProductionMachineCapacityEvidence evidence,
        ActiveCapacityRow row,
        IReadOnlyList<ProductionMachineScheduleAllocation> currentAllocations,
        IReadOnlyList<ProductionMachineScheduleAllocation> allAllocations,
        IReadOnlyList<ProductionMachineScheduleSegment> currentSegments)
    {
        if (row.PlannedStart is null || row.PlannedCompletion is null
            || row.OperationStart is null || row.OperationCompletion is null)
        {
            AddInvalid(evidence, row, "missing saved machine/operation timestamps");
            return false;
        }

        if (row.PlannedCompletion <= row.PlannedStart)
        {
            AddInvalid(evidence, row, "saved machine completion is not after its start");
            return false;
        }

        if (row.OperationStart != row.PlannedStart || row.OperationCompletion != row.PlannedCompletion)
        {
            AddInvalid(evidence, row, "machine and operation timestamps disagree");
            return false;
        }

        if (allAllocations.Any(x => x.ScheduleRevision != row.ScheduleRevision))
        {
            AddInvalid(evidence, row, "allocation rows exist for an obsolete schedule revision");
            return false;
        }

        if (currentAllocations.Count == 0)
        {
            AddInvalid(evidence, row, "no current allocation fragments exist; run the controlled allocation backfill");
            return false;
        }

        for (var i = 0; i < currentAllocations.Count; i++)
        {
            var allocation = currentAllocations[i];
            if (allocation.FragmentNo != i + 1)
            {
                AddInvalid(evidence, row, "allocation fragment numbering is not contiguous");
                return false;
            }

            if (allocation.EndDateTime <= allocation.StartDateTime)
            {
                AddInvalid(evidence, row, "allocation fragment is not a positive interval");
                return false;
            }

            if (!string.Equals(allocation.CompanyCode, row.CompanyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(allocation.BranchCode, row.BranchCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    ProductionMachineCapacityEvidence.Normalize(allocation.MachineCode),
                    ProductionMachineCapacityEvidence.Normalize(row.MachineCode),
                    StringComparison.Ordinal)
                || allocation.WorkOrderId != row.WorkOrderId
                || allocation.WorkOrderOperationId != row.OperationId
                || allocation.WorkOrderMachineId != row.MachineId
                || allocation.ScheduleRevision != row.ScheduleRevision
                || !string.Equals(allocation.ScheduleSourceHash, row.ScheduleSourceHash, StringComparison.Ordinal))
            {
                AddInvalid(evidence, row, "allocation provenance does not match the selected active machine operation");
                return false;
            }
        }

        // ── Segment-aware validation ──────────────────────────────────────────────────────
        // A split machine cannot be reconstructed from outer dates + PlannedRunMinutes, so a
        // current segment set must be internally consistent and linked from the allocations.
        var linked = currentAllocations.Where(x => x.MachineScheduleSegmentId is not null).ToList();
        if (currentSegments.Count == 0)
        {
            if (linked.Count > 0)
            {
                AddInvalid(evidence, row, "allocations reference planning segments that were not found");
                return false;
            }

            return true;
        }

        if (linked.Count == 0)
        {
            AddInvalid(evidence, row, "current planning segments exist but no allocation is linked to them");
            return false;
        }

        if (linked.Count != currentAllocations.Count)
        {
            AddInvalid(evidence, row, "allocations mix linked and unlinked planning segments");
            return false;
        }

        var segmentById = currentSegments.ToDictionary(x => x.Uid);
        foreach (var allocation in currentAllocations)
        {
            if (!segmentById.TryGetValue(allocation.MachineScheduleSegmentId!.Value, out var segment))
            {
                AddInvalid(evidence, row, "allocation references a segment outside the current machine revision");
                return false;
            }

            if (!string.Equals(segment.CompanyCode, row.CompanyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(segment.BranchCode, row.BranchCode, StringComparison.OrdinalIgnoreCase)
                || segment.WorkOrderId != row.WorkOrderId
                || segment.WorkOrderOperationId != row.OperationId
                || segment.WorkOrderMachineId != row.MachineId
                || !string.Equals(
                    ProductionMachineCapacityEvidence.Normalize(segment.MachineCode),
                    ProductionMachineCapacityEvidence.Normalize(row.MachineCode),
                    StringComparison.Ordinal)
                || segment.MachineScheduleRevision != row.ScheduleRevision
                || segment.PlannedOutputQty < 0m
                || segment.PlannedCompletionDateTime <= segment.PlannedStartDateTime)
            {
                AddInvalid(evidence, row, "planning segment identity or interval does not match the selected active machine operation");
                return false;
            }

            if (allocation.StartDateTime < segment.PlannedStartDateTime
                || allocation.EndDateTime > segment.PlannedCompletionDateTime)
            {
                AddInvalid(evidence, row, "allocation fragment falls outside its planning segment");
                return false;
            }
        }

        // A replanned operation that already has execution keeps its historical planned start as
        // a lower bound (plan §7.2), so the current segment union may begin after it. Completion
        // must always be the last future segment.
        var segmentStart = currentSegments.Min(x => x.PlannedStartDateTime);
        var segmentCompletion = currentSegments.Max(x => x.PlannedCompletionDateTime);
        if (segmentCompletion != row.PlannedCompletion
            || (!row.HasExecution && segmentStart != row.PlannedStart)
            || (row.HasExecution && segmentStart > row.PlannedCompletion))
        {
            AddInvalid(evidence, row, "segment union does not match the machine summary dates");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Plan §6.2: a zero-duration row may be ignored as physical occupancy only when its saved
    /// shape proves a single empty instant and no positive allocation fragment exists.
    /// </summary>
    private static bool TryClassifyLegacyEmpty(
        ActiveCapacityRow row,
        IReadOnlyList<ProductionMachineScheduleAllocation> allocations)
    {
        if (row.SetupSeconds != 0m || row.ConversionSeconds != 0m
            || row.QueueSeconds != 0m || row.PlannedRunMinutes != 0m)
            return false;

        if (row.PlannedStart is null || row.PlannedCompletion is null
            || row.OperationStart is null || row.OperationCompletion is null)
            return false;

        if (row.PlannedStart != row.PlannedCompletion)
            return false;

        if (row.OperationStart != row.PlannedStart || row.OperationCompletion != row.PlannedCompletion)
            return false;

        return !allocations.Any(x => x.EndDateTime > x.StartDateTime);
    }

    private static string DescribeEmptyRowViolation(
        ActiveCapacityRow row,
        IReadOnlyList<ProductionMachineScheduleAllocation> allocations)
    {
        if (allocations.Any(x => x.EndDateTime > x.StartDateTime))
            return "zero productive duration but a positive allocation fragment exists";
        if (row.SetupSeconds != 0m || row.ConversionSeconds != 0m
            || row.QueueSeconds != 0m || row.PlannedRunMinutes != 0m)
            return "zero combined duration with offsetting/non-zero timing components";
        if (row.PlannedStart is null || row.PlannedCompletion is null
            || row.OperationStart is null || row.OperationCompletion is null)
            return "zero duration with missing machine/operation timestamps";
        if (row.PlannedStart != row.PlannedCompletion)
            return "zero duration but saved start and completion differ";
        return "machine and operation timestamps disagree";
    }

    private static void AddInvalid(ProductionMachineCapacityEvidence evidence, ActiveCapacityRow row, string invariant)
    {
        evidence.InvalidRowCount++;
        evidence.Findings.Add(new ProductionMachineCapacityFinding
        {
            Code = ProductionReadinessErrorCodes.MachineCapacityInvalid,
            WorkOrderNo = row.WorkOrderNo,
            OperationCode = row.OperationCode,
            MachineCode = row.MachineCode,
            Message = $"{ProductionReadinessErrorCodes.MachineCapacityInvalid}: Work Order {row.WorkOrderNo}, "
                + $"operation {row.OperationCode}, machine {row.MachineCode} has invalid active capacity: {invariant}.",
            IsBlocking = true
        });
    }

    private static void AddOverlapFindings(ProductionMachineCapacityEvidence evidence)
    {
        foreach (var pair in evidence.OccupiedByMachine)
        {
            var ordered = pair.Value;
            if (ordered.Count < 2)
                continue;

            var furthestEnd = ordered[0].End;
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Start < furthestEnd)
                {
                    evidence.Findings.Add(new ProductionMachineCapacityFinding
                    {
                        Code = ProductionReadinessErrorCodes.MachineCapacityInvalid,
                        MachineCode = pair.Key,
                        Message = $"{ProductionReadinessErrorCodes.MachineCapacityInvalid}: active machine {pair.Key} "
                            + "has overlapping verified allocation fragments. Resolve the overlap before releasing or rescheduling.",
                        IsBlocking = true
                    });
                    break;
                }

                if (ordered[i].End > furthestEnd)
                    furthestEnd = ordered[i].End;
            }
        }
    }

    private static string SourceStamp(
        ActiveCapacityRow row,
        IReadOnlyList<ProductionMachineScheduleAllocation> allocations,
        IReadOnlyList<ProductionMachineScheduleSegment> segments)
    {
        var allocationStamp = allocations.Count == 0
            ? "none"
            : string.Join(",", allocations.Select(x =>
                $"{x.Uid}:{x.FragmentNo}:{x.StartDateTime:O}:{x.EndDateTime:O}:"
                + $"{x.BranchCode}:{x.WorkOrderOperationId}:{x.ScheduleRevision}:{x.ScheduleSourceHash}:{x.MachineScheduleSegmentId}"));
        var segmentStamp = segments.Count == 0
            ? "none"
            : string.Join(",", segments.Select(x =>
                $"{x.Uid}:{x.SegmentSequence}:{x.SegmentKind}:{x.SegmentStatus}:{x.MachineScheduleRevision}:"
                + $"{x.PlannedStartDateTime:O}:{x.PlannedCompletionDateTime:O}:{x.PlannedOutputQty}:{x.SegmentScheduleSourceHash}"));
        return $"M:{row.MachineId}:{row.ScheduleRevision}:{ProductionMachineCapacityEvidence.Normalize(row.MachineCode)}:"
            + $"{row.PlannedStart:O}:{row.PlannedCompletion:O}:{row.ScheduleSourceHash}:{allocationStamp}:{segmentStamp}";
    }

    private sealed class ActiveCapacityRow
    {
        public string CompanyCode { get; init; } = string.Empty;
        public long WorkOrderId { get; init; }
        public string WorkOrderNo { get; init; } = string.Empty;
        public string BranchCode { get; init; } = string.Empty;
        public long MachineId { get; init; }
        public string MachineCode { get; init; } = string.Empty;
        public long OperationId { get; init; }
        public string OperationCode { get; init; } = string.Empty;
        public DateTime? OperationStart { get; init; }
        public DateTime? OperationCompletion { get; init; }
        public DateTime? PlannedStart { get; init; }
        public DateTime? PlannedCompletion { get; init; }
        public int ScheduleRevision { get; init; }
        public string? ScheduleSourceHash { get; init; }
        public decimal SetupSeconds { get; init; }
        public decimal ConversionSeconds { get; init; }
        public decimal QueueSeconds { get; init; }
        public decimal PlannedRunMinutes { get; init; }
        public bool HasExecution { get; init; }
    }
}

/// <summary>
/// Window provider bound to a caller-owned transaction. The caller must already hold the machine
/// applocks for every code it asks about.
/// </summary>
public sealed class TransactionalMachineCapacityWindowProvider : IProductionMachineCapacityWindowProvider
{
    private readonly IProductionMachineCapacityService _service;
    private readonly AppDbContext _db;
    private readonly string _companyCode;
    private readonly long? _excludedWorkOrderId;
    private readonly IReadOnlySet<long>? _excludedMachineIds;
    private readonly Dictionary<(DateTime Start, DateTime End, string Codes), MachineCapacityWindow> _cache = [];

    public TransactionalMachineCapacityWindowProvider(
        IProductionMachineCapacityService service,
        AppDbContext db,
        string companyCode,
        long? excludedWorkOrderId = null,
        IReadOnlySet<long>? excludedMachineIds = null)
    {
        _service = service;
        _db = db;
        _companyCode = companyCode;
        _excludedWorkOrderId = excludedWorkOrderId;
        _excludedMachineIds = excludedMachineIds;
    }

    public async Task<MachineCapacityWindow> LoadAsync(
        IReadOnlyCollection<string> machineCodes,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken cancellationToken = default)
    {
        var ordered = machineCodes
            .Select(ProductionMachineCapacityEvidence.Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var codeKey = string.Join('|', ordered);
        var key = (windowStart, windowEnd, codeKey);
        if (_cache.TryGetValue(key, out var cached))
            return Filter(cached, ordered);

        var evidence = await _service.LoadAsync(
            _db,
            _companyCode,
            ordered,
            windowStart,
            windowEnd,
            _excludedWorkOrderId,
            _excludedMachineIds,
            cancellationToken);
        var window = new MachineCapacityWindow
        {
            OccupiedByMachine = evidence.OccupiedByMachine,
            Blockers = evidence.Blockers,
            SourceStamps = evidence.SourceStamps
        };
        _cache[key] = window;
        return Filter(window, ordered);
    }

    private static MachineCapacityWindow Filter(MachineCapacityWindow window, IReadOnlyList<string> codes)
    {
        var filtered = new Dictionary<string, List<TimeInterval>>(StringComparer.Ordinal);
        foreach (var code in codes)
            filtered[code] = window.OccupiedByMachine.TryGetValue(code, out var intervals) ? intervals : [];
        return new MachineCapacityWindow
        {
            OccupiedByMachine = filtered,
            Blockers = window.Blockers,
            SourceStamps = window.SourceStamps
        };
    }
}

/// <summary>
/// Window provider used by Draft/Recalculate. It opens read-only contexts; Release revalidates
/// under the caller's machine locks, so a stale Draft proposal is an accepted race.
/// </summary>
public sealed class PreviewMachineCapacityWindowProvider : IProductionMachineCapacityWindowProvider
{
    private readonly IProductionMachineCapacityService _service;
    private readonly string _companyCode;
    private readonly long? _excludedWorkOrderId;
    private readonly IReadOnlySet<long>? _excludedMachineIds;
    private readonly Dictionary<(DateTime Start, DateTime End, string Codes), MachineCapacityWindow> _cache = [];

    public PreviewMachineCapacityWindowProvider(
        IProductionMachineCapacityService service,
        string companyCode,
        long? excludedWorkOrderId = null,
        IReadOnlySet<long>? excludedMachineIds = null)
    {
        _service = service;
        _companyCode = companyCode;
        _excludedWorkOrderId = excludedWorkOrderId;
        _excludedMachineIds = excludedMachineIds;
    }

    public async Task<MachineCapacityWindow> LoadAsync(
        IReadOnlyCollection<string> machineCodes,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken cancellationToken = default)
    {
        var ordered = machineCodes
            .Select(ProductionMachineCapacityEvidence.Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var codeKey = string.Join('|', ordered);
        var key = (windowStart, windowEnd, codeKey);
        if (_cache.TryGetValue(key, out var cached))
            return Filter(cached, ordered);

        var evidence = await _service.LoadPreviewAsync(
            _companyCode,
            ordered,
            windowStart,
            windowEnd,
            _excludedWorkOrderId,
            _excludedMachineIds,
            cancellationToken);
        var window = new MachineCapacityWindow
        {
            OccupiedByMachine = evidence.OccupiedByMachine,
            Blockers = evidence.Blockers,
            SourceStamps = evidence.SourceStamps
        };
        _cache[key] = window;
        return Filter(window, ordered);
    }

    private static MachineCapacityWindow Filter(MachineCapacityWindow window, IReadOnlyList<string> codes)
    {
        var filtered = new Dictionary<string, List<TimeInterval>>(StringComparer.Ordinal);
        foreach (var code in codes)
            filtered[code] = window.OccupiedByMachine.TryGetValue(code, out var intervals) ? intervals : [];
        return new MachineCapacityWindow
        {
            OccupiedByMachine = filtered,
            Blockers = window.Blockers,
            SourceStamps = window.SourceStamps
        };
    }
}
