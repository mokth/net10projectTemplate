using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Circuit-scoped in-memory workspace for Draft Reschedule Mode (v10 §§18-19).
/// Supports multi-change staging, temporary draft overlaps with visual conflict detection,
/// hidden off-filter occupancy checks, and lossless Undo / Redo / Reset without database writes.
/// </summary>
public sealed class MachineScheduleDraftWorkspace
{
    private readonly Stack<MachineScheduleDraftSnapshot> _undoStack = new();
    private readonly Stack<MachineScheduleDraftSnapshot> _redoStack = new();

    private IReadOnlyList<MachineSchedulePlannerAppointment> _baseAppointments = [];
    private IReadOnlyList<MachineScheduleDraftHiddenInterval> _hiddenIntervals = [];
    private List<MachineScheduleDraftCommand> _pendingCommands = [];
    private List<MachineSchedulePlannerAppointment> _draftAppointments = [];
    private List<MachineScheduleDraftConflict> _conflicts = [];
    private List<string> _warnings = [];

    public bool IsActive { get; private set; }
    public string BaseBoardFingerprint { get; private set; } = string.Empty;
    public IReadOnlyList<MachineScheduleDraftCommand> PendingCommands => _pendingCommands;
    public IReadOnlyList<MachineSchedulePlannerAppointment> DraftAppointments => _draftAppointments;
    public IReadOnlyList<MachineScheduleDraftConflict> Conflicts => _conflicts;
    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyCollection<MachineScheduleDraftSnapshot> UndoStack => _undoStack;
    public IReadOnlyCollection<MachineScheduleDraftSnapshot> RedoStack => _redoStack;
    public MachineScheduleDraftPreview? LastServerPreview { get; private set; }

    public int PendingCount => _pendingCommands.Count;
    public int ConflictCount => _conflicts.Count;
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Initialize(MachineSchedulePlannerBoard board, bool enterDraftMode = false)
    {
        ArgumentNullException.ThrowIfNull(board);
        Initialize(board.BoardFingerprint, board.Appointments, board.HiddenOccupiedIntervals, enterDraftMode);
    }

    public void Initialize(
        string baseBoardFingerprint,
        IReadOnlyList<MachineSchedulePlannerAppointment> baseAppointments,
        IReadOnlyList<MachineScheduleDraftHiddenInterval> hiddenOccupiedIntervals,
        bool enterDraftMode = false)
    {
        BaseBoardFingerprint = baseBoardFingerprint ?? string.Empty;
        _baseAppointments = (baseAppointments ?? []).Select(CloneAppointment).ToList();
        _hiddenIntervals = (hiddenOccupiedIntervals ?? []).ToList();

        if (enterDraftMode)
            IsActive = true;

        if (!IsActive || _pendingCommands.Count == 0)
        {
            _pendingCommands = [];
            _undoStack.Clear();
            _redoStack.Clear();
            _warnings = [];
            _draftAppointments = _baseAppointments.Select(CloneAppointment).ToList();
            LastServerPreview = null;
            RecomputeConflicts();
        }
        else
        {
            RecomputeConflicts();
        }
    }

    public void EnterDraftMode()
    {
        IsActive = true;
        if (_draftAppointments.Count == 0 && _baseAppointments.Count > 0)
        {
            _draftAppointments = _baseAppointments.Select(CloneAppointment).ToList();
            RecomputeConflicts();
        }
    }

    public void ExitDraftMode()
    {
        IsActive = false;
        ResetAll();
    }

    public void SetServerPreview(MachineScheduleDraftPreview? preview) => LastServerPreview = preview;

    public void StageMove(
        MachineSchedulePlannerJob job,
        DateTime requestedStart,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!IsActive)
            EnterDraftMode();

        PushUndoSnapshot();
        _redoStack.Clear();
        LastServerPreview = null;

        var matching = _draftAppointments
            .Where(x => !x.IsMaintenance && !x.IsExternalCapacity && MatchesJob(x, job))
            .OrderBy(x => x.Start)
            .ToList();
        var currentStart = matching.Count > 0 ? matching[0].Start : job.PlannedStart;
        var delta = DateTime.SpecifyKind(requestedStart, DateTimeKind.Unspecified) - currentStart;

        if (matching.Count > 0)
        {
            var matchedIds = matching.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < _draftAppointments.Count; i++)
            {
                var item = _draftAppointments[i];
                if (!matchedIds.Contains(item.Id))
                    continue;
                _draftAppointments[i] = CloneWith(item,
                    start: item.Start + delta,
                    end: item.End + delta,
                    isDraft: true,
                    isConflict: false);
            }
        }
        else
        {
            var duration = job.PlannedCompletion > job.PlannedStart
                ? job.PlannedCompletion - job.PlannedStart
                : TimeSpan.FromMinutes((double)Math.Max(1m, job.PlannedRunMinutes));
            var start = DateTime.SpecifyKind(requestedStart, DateTimeKind.Unspecified);
            _draftAppointments.Add(new MachineSchedulePlannerAppointment
            {
                Id = $"draft-wo-{job.OperationId}",
                Subject = $"{job.WorkOrderNo} · {job.ProductCode}",
                Description = $"Draft move for {job.WorkOrderNo}",
                Location = $"operation:{job.OperationId}",
                Start = start,
                End = start + duration,
                ResourceId = job.MachineCode,
                MachineCode = job.MachineCode,
                WorkOrderId = job.WorkOrderId,
                OperationId = job.OperationId,
                WorkOrderMachineId = job.WorkOrderMachineId,
                WorkOrderNo = job.WorkOrderNo,
                LabelId = MachineSchedulePlannerLabelIds.Scheduled,
                PlannedOutputQty = job.PlannedQuantity,
                OutputUom = job.OutputUom,
                IsDraft = true
            });
        }

        _pendingCommands.Add(new MachineScheduleDraftCommand
        {
            Kind = MachineScheduleDraftCommandKind.Move,
            WorkOrderId = job.WorkOrderId,
            OperationId = job.OperationId,
            WorkOrderMachineId = job.WorkOrderMachineId,
            MachineCode = job.MachineCode,
            WorkOrderNo = job.WorkOrderNo,
            RequestedStart = DateTime.SpecifyKind(requestedStart, DateTimeKind.Unspecified),
            Reason = string.IsNullOrWhiteSpace(reason) ? "Draft schedule move" : reason.Trim()
        });

        RecomputeConflicts();
    }

    public void StageAdvancedResult(
        MachineScheduleDraftCommand command,
        MachineScheduleAdvancedPreview preview)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(preview);
        if (!IsActive)
            EnterDraftMode();

        PushUndoSnapshot();
        _redoStack.Clear();
        LastServerPreview = null;

        var touchedWorkOrderIds = preview.AffectedWorkOrders
            .Select(x => x.WorkOrderId)
            .Append(command.WorkOrderId)
            .ToHashSet();
        var touchedMachineIds = preview.Segments
            .Select(x => x.WorkOrderMachineId)
            .Append(command.WorkOrderMachineId)
            .ToHashSet();

        // Keep unchanged appointments and maintenance/external bars; replace touched WO operations.
        _draftAppointments = _draftAppointments
            .Where(x => x.IsMaintenance
                || x.IsExternalCapacity
                || (!IsWorkOrderAppointment(x, touchedWorkOrderIds) && !IsMachineAppointment(x, touchedMachineIds)))
            .Select(x => CloneWith(x, isConflict: false))
            .ToList();

        var existingByOpId = _baseAppointments
            .Where(x => x.OperationId is not null)
            .GroupBy(x => x.OperationId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var seqCounter = 0;
        foreach (var seg in preview.Segments.Where(x => x.Status != ProductionMachineScheduleSegmentStatuses.Superseded))
        {
            var baseApp = existingByOpId.TryGetValue(seg.OperationId, out var found) ? found : null;
            var intervals = seg.ProductiveIntervals.Count > 0
                ? seg.ProductiveIntervals
                : [new MachineScheduleAdvancedInterval { Start = seg.Start, End = seg.Completion }];
            foreach (var interval in intervals)
            {
                if (interval.End <= interval.Start)
                    continue;
                seqCounter++;
                var labelId = seg.Kind switch
                {
                    ProductionMachineScheduleSegmentKinds.ManualSplit => MachineSchedulePlannerLabelIds.ManualSplit,
                    ProductionMachineScheduleSegmentKinds.UrgentInsert => MachineSchedulePlannerLabelIds.UrgentInsert,
                    ProductionMachineScheduleSegmentKinds.Resume => MachineSchedulePlannerLabelIds.Resume,
                    ProductionMachineScheduleSegmentKinds.HoldResume => MachineSchedulePlannerLabelIds.HoldResume,
                    ProductionMachineScheduleSegmentKinds.MaintenanceResume => MachineSchedulePlannerLabelIds.MaintenanceResume,
                    ProductionMachineScheduleSegmentKinds.RemainingReplan => MachineSchedulePlannerLabelIds.RemainingReplan,
                    _ => MachineSchedulePlannerLabelIds.Scheduled
                };
                var suffix = string.IsNullOrWhiteSpace(seg.Kind)
                    || seg.Kind == ProductionMachineScheduleSegmentKinds.Original
                    ? string.Empty
                    : $" · {seg.Kind}";
                _draftAppointments.Add(new MachineSchedulePlannerAppointment
                {
                    Id = $"draft-{command.CommandId:N}-{seqCounter}",
                    Subject = baseApp is not null
                        ? $"{seg.WorkOrderNo}{suffix}"
                        : $"{seg.WorkOrderNo}{suffix}",
                    Description = baseApp?.Description ?? $"Draft segment {seg.WorkOrderNo} ({seg.PlannedOutputQty:n4})",
                    Location = seg.SegmentId is long sid ? $"segment:{sid}" : $"operation:{seg.OperationId}",
                    Start = interval.Start,
                    End = interval.End,
                    ResourceId = seg.MachineCode,
                    MachineCode = seg.MachineCode,
                    WorkOrderId = seg.WorkOrderId,
                    OperationId = seg.OperationId,
                    WorkOrderMachineId = seg.WorkOrderMachineId,
                    SegmentId = seg.SegmentId,
                    WorkOrderNo = seg.WorkOrderNo,
                    SegmentKind = seg.Kind,
                    PlannedOutputQty = seg.PlannedOutputQty,
                    OutputUom = baseApp?.OutputUom,
                    IsLate = baseApp?.IsLate ?? false,
                    LabelId = labelId,
                    IsDraft = true
                });
            }
        }

        _pendingCommands.Add(command);
        foreach (var warning in preview.Warnings)
        {
            if (!_warnings.Contains(warning, StringComparer.Ordinal))
                _warnings.Add(warning);
        }

        RecomputeConflicts();
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0)
            return false;
        _redoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_undoStack.Pop());
        LastServerPreview = null;
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0)
            return false;
        _undoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        LastServerPreview = null;
        return true;
    }

    public void ResetAll()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _pendingCommands = [];
        _conflicts = [];
        _warnings = [];
        _draftAppointments = _baseAppointments.Select(CloneAppointment).ToList();
        LastServerPreview = null;
        RecomputeConflicts();
    }

    public void RecomputeConflicts()
    {
        var conflicts = new List<MachineScheduleDraftConflict>();
        var conflictIds = new HashSet<string>(StringComparer.Ordinal);

        var byMachine = _draftAppointments
            .Where(x => !string.IsNullOrWhiteSpace(x.ResourceId))
            .GroupBy(x => NormalizeMachine(x.ResourceId), StringComparer.Ordinal);

        foreach (var group in byMachine)
        {
            var list = group.OrderBy(x => x.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                for (var j = i + 1; j < list.Count; j++)
                {
                    var b = list[j];
                    if (b.Start >= a.End)
                        break;
                    if (a.IsMaintenance && b.IsMaintenance)
                        continue;
                    if (a.WorkOrderMachineId is not null && b.WorkOrderMachineId is not null
                        && a.WorkOrderMachineId == b.WorkOrderMachineId)
                        continue;

                    var overlapStart = a.Start > b.Start ? a.Start : b.Start;
                    var overlapEnd = a.End < b.End ? a.End : b.End;
                    if (overlapEnd <= overlapStart)
                        continue;

                    if (!a.IsMaintenance && !a.IsExternalCapacity)
                        conflictIds.Add(a.Id);
                    if (!b.IsMaintenance && !b.IsExternalCapacity)
                        conflictIds.Add(b.Id);

                    var woA = string.IsNullOrWhiteSpace(a.WorkOrderNo) ? a.Subject : a.WorkOrderNo;
                    var woB = string.IsNullOrWhiteSpace(b.WorkOrderNo) ? b.Subject : b.WorkOrderNo;
                    conflicts.Add(new MachineScheduleDraftConflict
                    {
                        MachineCode = group.Key,
                        AppointmentIdA = a.Id,
                        AppointmentIdB = b.Id,
                        WorkOrderIdA = a.WorkOrderId,
                        WorkOrderIdB = b.WorkOrderId,
                        WorkOrderNoA = woA,
                        WorkOrderNoB = woB,
                        OverlapStart = overlapStart,
                        OverlapEnd = overlapEnd,
                        IsOffFilterConflict = false,
                        IsExternalOrMaintenanceConflict = a.IsExternalCapacity || b.IsExternalCapacity
                            || a.IsMaintenance || b.IsMaintenance,
                        Message = $"{woA} overlaps {woB} on {group.Key} ({overlapStart:HH:mm}–{overlapEnd:HH:mm})."
                    });
                }
            }
        }

        // Also check draft/visible job intervals against hidden off-filter intervals (and external/maintenance hidden records).
        var hiddenByMachine = _hiddenIntervals
            .Where(x => !string.IsNullOrWhiteSpace(x.MachineCode))
            .GroupBy(x => NormalizeMachine(x.MachineCode), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var app in _draftAppointments.Where(x => !x.IsMaintenance && !x.IsExternalCapacity))
        {
            var machineKey = NormalizeMachine(app.ResourceId);
            if (!hiddenByMachine.TryGetValue(machineKey, out var hiddens))
                continue;
            foreach (var hidden in hiddens)
            {
                if (hidden.WorkOrderId is not null && app.WorkOrderId is not null
                    && hidden.WorkOrderId == app.WorkOrderId)
                    continue;
                if (hidden.IsExternal)
                {
                    // External intervals are already represented in visible appointments too; deduplicate by window.
                    var alreadyVisible = _draftAppointments.Any(v => v.IsExternalCapacity
                        && NormalizeMachine(v.ResourceId) == machineKey
                        && v.Start == hidden.Start && v.End == hidden.End);
                    if (alreadyVisible)
                        continue;
                }
                if (app.Start >= hidden.End || app.End <= hidden.Start)
                    continue;
                var overlapStart = app.Start > hidden.Start ? app.Start : hidden.Start;
                var overlapEnd = app.End < hidden.End ? app.End : hidden.End;
                if (overlapEnd <= overlapStart)
                    continue;

                conflictIds.Add(app.Id);
                var woA = string.IsNullOrWhiteSpace(app.WorkOrderNo) ? app.Subject : app.WorkOrderNo;
                conflicts.Add(new MachineScheduleDraftConflict
                {
                    MachineCode = machineKey,
                    AppointmentIdA = app.Id,
                    AppointmentIdB = $"hidden-{hidden.WorkOrderId ?? 0}-{hidden.Start:yyyyMMddHHmm}",
                    WorkOrderIdA = app.WorkOrderId,
                    WorkOrderIdB = hidden.WorkOrderId,
                    WorkOrderNoA = woA,
                    WorkOrderNoB = hidden.WorkOrderNo,
                    OverlapStart = overlapStart,
                    OverlapEnd = overlapEnd,
                    IsOffFilterConflict = !hidden.IsExternal && !hidden.IsMaintenance,
                    IsExternalOrMaintenanceConflict = hidden.IsExternal || hidden.IsMaintenance,
                    Message = $"{woA} overlaps off-filter {hidden.WorkOrderNo} on {machineKey} ({overlapStart:HH:mm}–{overlapEnd:HH:mm})."
                });
            }
        }

        _conflicts = conflicts;
        for (var i = 0; i < _draftAppointments.Count; i++)
        {
            var app = _draftAppointments[i];
            var hasConflict = conflictIds.Contains(app.Id);
            if (app.IsConflict != hasConflict)
                _draftAppointments[i] = CloneWith(app, isConflict: hasConflict);
        }
    }

    private void PushUndoSnapshot() => _undoStack.Push(CaptureSnapshot());

    private MachineScheduleDraftSnapshot CaptureSnapshot() => new()
    {
        PendingCommands = _pendingCommands.ToList(),
        DraftAppointments = _draftAppointments.Select(CloneAppointment).ToList(),
        Conflicts = _conflicts.ToList(),
        Warnings = _warnings.ToList()
    };

    private void RestoreSnapshot(MachineScheduleDraftSnapshot snapshot)
    {
        _pendingCommands = snapshot.PendingCommands.ToList();
        _draftAppointments = snapshot.DraftAppointments.Select(CloneAppointment).ToList();
        _conflicts = snapshot.Conflicts.ToList();
        _warnings = snapshot.Warnings.ToList();
    }

    private static bool MatchesJob(MachineSchedulePlannerAppointment appointment, MachineSchedulePlannerJob job) =>
        appointment.WorkOrderMachineId == job.WorkOrderMachineId
        || appointment.OperationId == job.OperationId
        || string.Equals(appointment.Location, $"operation:{job.OperationId}", StringComparison.Ordinal);

    private static bool IsWorkOrderAppointment(MachineSchedulePlannerAppointment app, HashSet<long> workOrderIds) =>
        app.WorkOrderId is long woId && workOrderIds.Contains(woId);

    private static bool IsMachineAppointment(MachineSchedulePlannerAppointment app, HashSet<long> machineIds) =>
        app.WorkOrderMachineId is long machineId && machineIds.Contains(machineId);

    private static string NormalizeMachine(string? code) =>
        (code ?? string.Empty).Trim().ToUpperInvariant();

    private static MachineSchedulePlannerAppointment CloneAppointment(MachineSchedulePlannerAppointment src) =>
        CloneWith(src);

    private static MachineSchedulePlannerAppointment CloneWith(
        MachineSchedulePlannerAppointment src,
        DateTime? start = null,
        DateTime? end = null,
        bool? isDraft = null,
        bool? isConflict = null) => new()
    {
        Id = src.Id,
        Subject = src.Subject,
        Description = src.Description,
        Location = src.Location,
        Start = start ?? src.Start,
        End = end ?? src.End,
        ResourceId = src.ResourceId,
        LabelId = src.LabelId,
        IsMaintenance = src.IsMaintenance,
        IsExternalCapacity = src.IsExternalCapacity,
        WorkOrderId = src.WorkOrderId,
        OperationId = src.OperationId,
        WorkOrderMachineId = src.WorkOrderMachineId,
        SegmentId = src.SegmentId,
        WorkOrderNo = src.WorkOrderNo,
        MachineCode = src.MachineCode,
        SegmentKind = src.SegmentKind,
        PlannedOutputQty = src.PlannedOutputQty,
        OutputUom = src.OutputUom,
        IsLocked = src.IsLocked,
        IsLate = src.IsLate,
        IsDraft = isDraft ?? src.IsDraft,
        IsConflict = isConflict ?? src.IsConflict
    };
}
