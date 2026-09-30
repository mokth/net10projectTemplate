namespace ErpWeb.Core.Production;

/// <summary>Half-open [Start, End) wall-clock interval in plant local time (Unspecified).</summary>
public readonly record struct TimeInterval(DateTime Start, DateTime End)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    public bool IsEmpty => End <= Start;
}

public enum ScheduleDirection
{
    Forward = 0,
    Backward = 1
}

public sealed class CalendarDayRow
{
    public DateOnly OperationalDate { get; init; }
    public string DateCd { get; init; } = "W";
    public string? ShfGrpCd { get; init; }
}

public sealed class ShiftSourceDefinition
{
    public string ShiftCd { get; init; } = string.Empty;
    public TimeOnly Start { get; init; }
    public TimeOnly End { get; init; }
    public IReadOnlyList<ErpWeb.Core.Planning.ShiftTimeCalculator.BreakPair> Breaks { get; init; } = [];
}

public sealed class ShiftGroupSource
{
    public string ShiftGroupCode { get; init; } = string.Empty;
    public IReadOnlyList<ShiftSourceDefinition> Shifts { get; init; } = [];
}

public sealed class PreventiveWindow
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
}

/// <summary>Preloaded in-memory schedule data for one machine (pure scheduler input).</summary>
public sealed class ScheduleMachineData
{
    public string MachineCode { get; init; } = string.Empty;
    public IReadOnlyDictionary<DateOnly, CalendarDayRow> CalendarByDate { get; init; }
        = new Dictionary<DateOnly, CalendarDayRow>();
    public IReadOnlyDictionary<string, ShiftGroupSource> ShiftGroups { get; init; }
        = new Dictionary<string, ShiftGroupSource>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<PreventiveWindow> PreventiveWindows { get; init; } = [];

    public IReadOnlyDictionary<DateOnly, IReadOnlyList<TimeInterval>> OwnedWorkingByOperationalDate { get; init; }
        = new Dictionary<DateOnly, IReadOnlyList<TimeInterval>>();
    public IReadOnlyList<TimeInterval> GlobalWorkingIntervals { get; init; } = [];
    public IReadOnlyList<TimeInterval> GlobalUsableIntervals { get; init; } = [];
    public IReadOnlyDictionary<DateOnly, IReadOnlyList<TimeInterval>> OwnedUsableByOperationalDate { get; init; }
        = new Dictionary<DateOnly, IReadOnlyList<TimeInterval>>();
}

public sealed class ScheduleMachineResult
{
    public DateTime ActualStart { get; init; }
    public DateTime ActualCompletion { get; init; }
    public TimeSpan ProductiveDuration { get; init; }
    public IReadOnlyList<TimeInterval> ConsumedIntervals { get; init; } = [];
}

public sealed class ScheduleFailureException : Exception
{
    public ScheduleFailureException(string message) : base(message) { }
}

/// <summary>
/// Pure interval-based machine calendar scheduler (no I/O).
/// Breaks applied per source shift; preventive after global working union;
/// OwnedUsable = OwnedWorking ∩ GlobalUsable.
/// </summary>
public static class ProductionCalendarScheduler
{
    public static ScheduleMachineData BuildScheduleData(
        string machineCode,
        IReadOnlyDictionary<DateOnly, CalendarDayRow> calendarByDate,
        IReadOnlyDictionary<string, ShiftGroupSource> shiftGroups,
        IReadOnlyList<PreventiveWindow> preventiveWindows)
    {
        var ownedWorking = new Dictionary<DateOnly, List<TimeInterval>>();
        var allWorking = new List<TimeInterval>();

        foreach (var kv in calendarByDate.OrderBy(x => x.Key))
        {
            var date = kv.Key;
            var row = kv.Value;
            if (!string.Equals(row.DateCd, "W", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(row.ShfGrpCd)
                || !shiftGroups.TryGetValue(row.ShfGrpCd, out var group)
                || group.Shifts.Count == 0)
            {
                throw new ScheduleFailureException(
                    $"Machine {machineCode} calendar on {date:yyyy-MM-dd} is marked Work but has no valid shift group.");
            }

            var dayFragments = new List<TimeInterval>();
            foreach (var shift in group.Shifts)
            {
                foreach (var frag in BuildShiftWorkingFragments(date, shift))
                {
                    dayFragments.Add(frag);
                    allWorking.Add(frag);
                }
            }

            if (dayFragments.Count > 0)
                ownedWorking[date] = dayFragments;
        }

        var globalWorking = UnionIntervals(allWorking);
        var preventive = UnionIntervals(preventiveWindows.Select(p => new TimeInterval(p.Start, p.End)));
        var globalUsable = SubtractIntervals(globalWorking, preventive);

        var ownedUsable = new Dictionary<DateOnly, IReadOnlyList<TimeInterval>>();
        foreach (var (date, working) in ownedWorking)
            ownedUsable[date] = IntersectIntervals(working, globalUsable);

        return new ScheduleMachineData
        {
            MachineCode = machineCode,
            CalendarByDate = calendarByDate,
            ShiftGroups = shiftGroups,
            PreventiveWindows = preventiveWindows,
            OwnedWorkingByOperationalDate = ownedWorking.ToDictionary(
                x => x.Key, x => (IReadOnlyList<TimeInterval>)x.Value),
            GlobalWorkingIntervals = globalWorking,
            GlobalUsableIntervals = globalUsable,
            OwnedUsableByOperationalDate = ownedUsable
        };
    }

    public static DateTime ResolveInitialForwardAnchor(
        ScheduleMachineData data, DateOnly startOperationalDate, int maxHorizonYears = 2)
    {
        var limit = startOperationalDate.AddYears(maxHorizonYears);
        for (var d = startOperationalDate; d <= limit; d = d.AddDays(1))
        {
            EnsureOperationalDatePresent(data, d);
            if (data.OwnedUsableByOperationalDate.TryGetValue(d, out var frags) && frags.Count > 0)
                return frags.OrderBy(x => x.Start).First().Start;
        }
        throw new ScheduleFailureException(
            $"No usable capacity on or after {startOperationalDate:yyyy-MM-dd} for machine {data.MachineCode}.");
    }

    public static DateTime ResolveInitialBackwardAnchor(
        ScheduleMachineData data, DateOnly completionOperationalDate, int maxHorizonYears = 2)
    {
        var limit = completionOperationalDate.AddYears(-maxHorizonYears);
        for (var d = completionOperationalDate; d >= limit; d = d.AddDays(-1))
        {
            EnsureOperationalDatePresent(data, d);
            if (data.OwnedUsableByOperationalDate.TryGetValue(d, out var frags) && frags.Count > 0)
                return frags.OrderBy(x => x.End).Last().End;
        }
        throw new ScheduleFailureException(
            $"No usable capacity on or before {completionOperationalDate:yyyy-MM-dd} for machine {data.MachineCode}.");
    }

    public static ScheduleMachineResult ScheduleMachine(
        ScheduleMachineData data,
        DateTime anchor,
        TimeSpan requiredDuration,
        ScheduleDirection direction)
    {
        if (requiredDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requiredDuration));

        if (requiredDuration == TimeSpan.Zero)
        {
            var snapped = SnapToUsable(data.GlobalUsableIntervals, anchor, direction);
            return new ScheduleMachineResult
            {
                ActualStart = snapped,
                ActualCompletion = snapped,
                ProductiveDuration = TimeSpan.Zero,
                ConsumedIntervals = []
            };
        }

        return direction == ScheduleDirection.Forward
            ? ScheduleForward(data, anchor, requiredDuration)
            : ScheduleBackward(data, anchor, requiredDuration);
    }

    private static ScheduleMachineResult ScheduleForward(
        ScheduleMachineData data, DateTime anchor, TimeSpan required)
    {
        var remaining = required;
        var cursor = SnapToUsable(data.GlobalUsableIntervals, anchor, ScheduleDirection.Forward);
        var consumed = new List<TimeInterval>();
        DateTime? start = null;
        DateTime completion = cursor;

        // Walk global usable intervals from cursor
        var intervals = data.GlobalUsableIntervals.Where(i => i.End > cursor).OrderBy(i => i.Start).ToList();
        if (intervals.Count == 0)
            throw new ScheduleFailureException($"No usable capacity after {anchor:yyyy-MM-dd HH:mm} for machine {data.MachineCode}.");

        // Validate operational dates as we enter them for calendar completeness beyond owned spill
        foreach (var interval in intervals)
        {
            var from = interval.Start < cursor ? cursor : interval.Start;
            if (from >= interval.End)
                continue;

            // When walking into a new operational day beyond prior owned spill, require calendar row
            ValidateOperationalDatesCovering(data, from, interval.End);

            var take = interval.End - from;
            if (take >= remaining)
            {
                var end = from + remaining;
                start ??= from;
                consumed.Add(new TimeInterval(from, end));
                completion = end;
                remaining = TimeSpan.Zero;
                break;
            }

            start ??= from;
            consumed.Add(new TimeInterval(from, interval.End));
            remaining -= take;
            completion = interval.End;
            cursor = interval.End;
        }

        if (remaining > TimeSpan.Zero)
            throw new ScheduleFailureException(
                $"Scheduling horizon exceeded for machine {data.MachineCode} (remaining {remaining.TotalMinutes:0.##} minutes).");

        return new ScheduleMachineResult
        {
            ActualStart = start ?? cursor,
            ActualCompletion = completion,
            ProductiveDuration = required,
            ConsumedIntervals = consumed
        };
    }

    private static ScheduleMachineResult ScheduleBackward(
        ScheduleMachineData data, DateTime anchor, TimeSpan required)
    {
        var remaining = required;
        var cursor = SnapToUsable(data.GlobalUsableIntervals, anchor, ScheduleDirection.Backward);
        var consumed = new List<TimeInterval>();
        DateTime? completion = null;
        DateTime start = cursor;

        var intervals = data.GlobalUsableIntervals.Where(i => i.Start < cursor).OrderByDescending(i => i.End).ToList();
        if (intervals.Count == 0)
            throw new ScheduleFailureException($"No usable capacity before {anchor:yyyy-MM-dd HH:mm} for machine {data.MachineCode}.");

        foreach (var interval in intervals)
        {
            var to = interval.End > cursor ? cursor : interval.End;
            if (to <= interval.Start)
                continue;

            ValidateOperationalDatesCovering(data, interval.Start, to);

            var take = to - interval.Start;
            if (take >= remaining)
            {
                var from = to - remaining;
                completion ??= to;
                consumed.Insert(0, new TimeInterval(from, to));
                start = from;
                remaining = TimeSpan.Zero;
                break;
            }

            completion ??= to;
            consumed.Insert(0, new TimeInterval(interval.Start, to));
            remaining -= take;
            start = interval.Start;
            cursor = interval.Start;
        }

        if (remaining > TimeSpan.Zero)
            throw new ScheduleFailureException(
                $"Scheduling horizon exceeded for machine {data.MachineCode} (remaining {remaining.TotalMinutes:0.##} minutes).");

        return new ScheduleMachineResult
        {
            ActualStart = start,
            ActualCompletion = completion ?? cursor,
            ProductiveDuration = required,
            ConsumedIntervals = consumed
        };
    }

    private static void ValidateOperationalDatesCovering(ScheduleMachineData data, DateTime from, DateTime to)
    {
        // Only require calendar rows for operational dates that own intervals intersecting [from,to)
        // Rule A: civil midnight spill alone is OK if covered by prior owned overnight.
        // We require a row when we need to evaluate a date as operational — i.e. when walking
        // into capacity that would be owned by that date OR when date is referenced in calendar map.
        // Practical rule: for each civil date that starts a GlobalWorking fragment intersecting the span,
        // ensure calendar row exists for that operational owner. Simpler approach used here:
        // when GlobalUsable walk needs capacity after exhausting all OwnedUsable for dates before D,
        // the next operational date D must exist.
        // During build, all Work days in calendarByDate already validated. Missing days aren't in map.
        // When schedule needs an operational date not in calendarByDate → fail.
        var owners = data.OwnedWorkingByOperationalDate.Keys
            .Where(d =>
            {
                if (!data.OwnedWorkingByOperationalDate.TryGetValue(d, out var frags))
                    return false;
                return frags.Any(f => f.Start < to && f.End > from);
            })
            .ToList();

        foreach (var d in owners)
            EnsureOperationalDatePresent(data, d);
    }

    public static void EnsureOperationalDatePresent(ScheduleMachineData data, DateOnly date)
    {
        if (!data.CalendarByDate.ContainsKey(date))
            throw new ScheduleFailureException(
                $"Machine {data.MachineCode} calendar is not defined for {date:yyyy-MM-dd}.");
    }

    private static bool IsOffOrHoliday(ScheduleMachineData data, DateOnly d) =>
        data.CalendarByDate.TryGetValue(d, out var row)
        && !string.Equals(row.DateCd, "W", StringComparison.OrdinalIgnoreCase);

    public static DateTime SnapToUsable(
        IReadOnlyList<TimeInterval> usable,
        DateTime anchor,
        ScheduleDirection direction)
    {
        if (usable.Count == 0)
            throw new ScheduleFailureException("No usable intervals available.");

        if (direction == ScheduleDirection.Forward)
        {
            foreach (var i in usable.OrderBy(x => x.Start))
            {
                if (anchor <= i.Start)
                    return i.Start;
                if (anchor < i.End)
                    return anchor;
            }
            throw new ScheduleFailureException($"Anchor {anchor:yyyy-MM-dd HH:mm} is after all usable intervals.");
        }

        foreach (var i in usable.OrderByDescending(x => x.End))
        {
            if (anchor >= i.End)
                return i.End;
            if (anchor > i.Start)
                return anchor;
        }
        throw new ScheduleFailureException($"Anchor {anchor:yyyy-MM-dd HH:mm} is before all usable intervals.");
    }

    /// <summary>Rule A: shift owned by operational start date; overnight may spill past midnight.</summary>
    public static IReadOnlyList<TimeInterval> BuildShiftWorkingFragments(
        DateOnly operationalDate,
        ShiftSourceDefinition shift)
    {
        var (shiftS, shiftE) = ErpWeb.Core.Planning.ShiftTimeCalculator.NormalizeShiftSpan(shift.Start, shift.End);
        var breakSegments = ErpWeb.Core.Planning.ShiftTimeCalculator.ExpandBreakSegments(
            shift.Start, shift.End, shift.Breaks);

        static DateTime At(DateOnly d, int minutesFromMidnight)
        {
            var days = minutesFromMidnight / 1440;
            var rem = minutesFromMidnight % 1440;
            return d.AddDays(days).ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(rem)), DateTimeKind.Unspecified);
        }

        var working = new List<TimeInterval>();
        var cursor = shiftS;
        foreach (var b in breakSegments.OrderBy(x => x.Start))
        {
            if (cursor < b.Start)
                working.Add(new TimeInterval(At(operationalDate, cursor), At(operationalDate, b.Start)));
            cursor = Math.Max(cursor, b.EndExclusive);
        }
        if (cursor < shiftE)
            working.Add(new TimeInterval(At(operationalDate, cursor), At(operationalDate, shiftE)));

        return working.Where(w => !w.IsEmpty).ToList();
    }

    public static List<TimeInterval> UnionIntervals(IEnumerable<TimeInterval> intervals)
    {
        var ordered = intervals.Where(i => !i.IsEmpty).OrderBy(i => i.Start).ThenBy(i => i.End).ToList();
        if (ordered.Count == 0) return [];

        var result = new List<TimeInterval>();
        var cur = ordered[0];
        for (var i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.Start <= cur.End)
            {
                if (next.End > cur.End)
                    cur = new TimeInterval(cur.Start, next.End);
            }
            else
            {
                result.Add(cur);
                cur = next;
            }
        }
        result.Add(cur);
        return result;
    }

    public static List<TimeInterval> SubtractIntervals(
        IReadOnlyList<TimeInterval> source,
        IReadOnlyList<TimeInterval> subtract)
    {
        var result = source.ToList();
        foreach (var s in subtract.Where(x => !x.IsEmpty))
        {
            var next = new List<TimeInterval>();
            foreach (var r in result)
            {
                if (s.End <= r.Start || s.Start >= r.End)
                {
                    next.Add(r);
                    continue;
                }
                if (s.Start > r.Start)
                    next.Add(new TimeInterval(r.Start, s.Start));
                if (s.End < r.End)
                    next.Add(new TimeInterval(s.End, r.End));
            }
            result = next;
        }
        return result.Where(x => !x.IsEmpty).ToList();
    }

    public static List<TimeInterval> IntersectIntervals(
        IReadOnlyList<TimeInterval> a,
        IReadOnlyList<TimeInterval> b)
    {
        var result = new List<TimeInterval>();
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                var start = x.Start > y.Start ? x.Start : y.Start;
                var end = x.End < y.End ? x.End : y.End;
                if (start < end)
                    result.Add(new TimeInterval(start, end));
            }
        }
        return UnionIntervals(result);
    }
}
