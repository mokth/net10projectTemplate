namespace ErpWeb.Core.Planning;

/// <summary>
/// Shift duration math using minute timelines. Domain breaks are nullable TimeOnly pairs.
/// Persistence Case A maps unused breaks to null; legacy DateTime columns use time-of-day only.
/// </summary>
public static class ShiftTimeCalculator
{
    public readonly record struct BreakPair(TimeOnly? Start, TimeOnly? End);

    public static int ToMinutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>
    /// Normalizes a shift span to half-open [start, endExclusive) on an extended minute timeline.
    /// Overnight when End &lt; Start (adds 1440). Start == End is invalid.
    /// </summary>
    public static (int Start, int EndExclusive) NormalizeShiftSpan(TimeOnly start, TimeOnly end)
    {
        var s = ToMinutes(start);
        var e = ToMinutes(end);
        if (e == s)
            throw new ArgumentException("Shift start and end times must differ.");
        if (e < s)
            e += 1440;
        return (s, e);
    }

    /// <summary>Validates break pair: both null unused; both set used; one null invalid.</summary>
    public static string? ValidateBreakPair(BreakPair pair, int index)
    {
        if (pair.Start is null && pair.End is null)
            return null;
        if (pair.Start is null || pair.End is null)
            return $"Break {index}: both start and end are required.";
        if (pair.Start == pair.End)
            return $"Break {index}: start and end times must differ.";
        return null;
    }

    public static IReadOnlyList<(int Start, int EndExclusive)> ExpandBreakSegments(
        TimeOnly shiftStart,
        TimeOnly shiftEnd,
        IEnumerable<BreakPair> breaks)
    {
        var (shiftS, shiftE) = NormalizeShiftSpan(shiftStart, shiftEnd);
        var overnight = ToMinutes(shiftEnd) < ToMinutes(shiftStart);
        var segments = new List<(int, int)>();

        var i = 0;
        foreach (var b in breaks)
        {
            i++;
            if (b.Start is null && b.End is null)
                continue;
            if (b.Start is null || b.End is null)
                throw new ArgumentException($"Break {i} is incomplete.");
            if (b.Start == b.End)
                throw new ArgumentException($"Break {i}: start and end times must differ.");

            var bs = ToMinutes(b.Start.Value);
            var be = ToMinutes(b.End.Value);
            if (be < bs)
            {
                if (!overnight)
                    throw new ArgumentException($"Break {i} crosses midnight but shift does not.");
                be += 1440;
            }
            else if (be == bs)
            {
                throw new ArgumentException($"Break {i}: start and end times must differ.");
            }

            // Overnight shifts occupy [shiftS, shiftE) on an extended timeline.
            // Clock times after midnight (e.g. 00:00–00:30) fall in the second-day
            // portion and must be shifted by +1440 so they land inside the span.
            if (overnight && bs < shiftS)
            {
                bs += 1440;
                be += 1440;
            }

            if (bs < shiftS || be > shiftE)
                throw new ArgumentException($"Break {i} is outside the shift span.");

            segments.Add((bs, be));
        }

        segments.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        for (var n = 1; n < segments.Count; n++)
        {
            if (segments[n].Item1 < segments[n - 1].Item2)
                throw new ArgumentException("Breaks overlap.");
        }

        return segments;
    }

    public static int ComputeNetMinutes(TimeOnly start, TimeOnly end, IEnumerable<BreakPair> breaks)
    {
        var (s, e) = NormalizeShiftSpan(start, end);
        var breakMinutes = ExpandBreakSegments(start, end, breaks).Sum(x => x.EndExclusive - x.Start);
        var net = e - s - breakMinutes;
        if (net <= 0)
            throw new ArgumentException("Net shift minutes must be greater than zero.");
        return net;
    }

    public static int ComputeGrossMinutes(TimeOnly start, TimeOnly end)
    {
        var (s, e) = NormalizeShiftSpan(start, end);
        return e - s;
    }

    /// <summary>Legacy float encoding: hours + minutes/100 (e.g. 8h30m => 8.30).</summary>
    public static double EncodeTotalTime(int totalMinutes)
    {
        if (totalMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours + (minutes / 100.0);
    }

    public static int DecodeTotalTime(double totalTime)
    {
        var hours = (int)Math.Floor(totalTime);
        var minutes = (int)Math.Round((totalTime - hours) * 100);
        if (minutes is < 0 or > 59)
            minutes = Math.Clamp(minutes, 0, 59);
        return hours * 60 + minutes;
    }

    public static DateTime? ToLegacyDateTime(TimeOnly? t) =>
        t is null ? null : DateTime.Today.Add(t.Value.ToTimeSpan());

    public static TimeOnly? FromLegacyDateTime(DateTime? dt)
    {
        if (dt is null)
            return null;
        return TimeOnly.FromDateTime(dt.Value);
    }

    /// <summary>
    /// Case A persistence: unused break stored as null.
    /// Case C sentinel (00:00 means unused) is NOT used in domain — see Phase 0 Case decision.
    /// </summary>
    public static BreakPair FromLegacyPair(DateTime? from, DateTime? to, bool treatMidnightAsUnused)
    {
        if (from is null && to is null)
            return default;
        if (treatMidnightAsUnused &&
            from is not null && to is not null &&
            from.Value.TimeOfDay == TimeSpan.Zero &&
            to.Value.TimeOfDay == TimeSpan.Zero)
            return default;
        return new BreakPair(FromLegacyDateTime(from), FromLegacyDateTime(to));
    }
}
