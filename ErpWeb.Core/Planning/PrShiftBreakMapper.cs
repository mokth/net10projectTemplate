using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Core.Planning;

public static class PrShiftBreakMapper
{
    public const int MaxBreaks = 5;

    public static IReadOnlyList<ShiftTimeCalculator.BreakPair> ResolveBreaks(
        PrShift shift,
        IReadOnlyList<PrShiftBreak> childRows)
    {
        return shift.BreakStorageVersion >= 1
            ? FromChildRows(childRows)
            : FromWideColumns(shift);
    }

    public static IReadOnlyList<ShiftTimeCalculator.BreakPair> FromWideColumns(PrShift shift)
    {
        // Return only used pairs so UI/consumers never materialize five blank slots.
        // Unused = NULL/NULL or classic 00:00–00:00 sentinel (FromLegacyPair).
        ShiftTimeCalculator.BreakPair[] slots =
        [
            ShiftTimeCalculator.FromLegacyPair(shift.BreakTm1From, shift.BreakTm1To, treatMidnightAsUnused: true),
            ShiftTimeCalculator.FromLegacyPair(shift.BreakTm2From, shift.BreakTm2To, treatMidnightAsUnused: true),
            ShiftTimeCalculator.FromLegacyPair(shift.BreakTm3From, shift.BreakTm3To, treatMidnightAsUnused: true),
            ShiftTimeCalculator.FromLegacyPair(shift.BreakTm4From, shift.BreakTm4To, treatMidnightAsUnused: true),
            ShiftTimeCalculator.FromLegacyPair(shift.BreakTm5From, shift.BreakTm5To, treatMidnightAsUnused: true)
        ];
        return slots.Where(x => x.Start is not null || x.End is not null).ToList();
    }

    public static IReadOnlyList<ShiftTimeCalculator.BreakPair> FromChildRows(IReadOnlyList<PrShiftBreak> childRows)
    {
        if (childRows.Count > MaxBreaks)
            throw new ArgumentException($"A shift can have at most {MaxBreaks} breaks.");

        var result = new List<ShiftTimeCalculator.BreakPair>(childRows.Count);
        byte? previousSeq = null;
        foreach (var row in childRows.OrderBy(x => x.BreakSeq))
        {
            if (row.BreakSeq is < 1 or > MaxBreaks)
                throw new ArgumentException($"Break sequence {row.BreakSeq} is outside 1..{MaxBreaks}.");
            if (previousSeq == row.BreakSeq)
                throw new ArgumentException($"Duplicate break sequence {row.BreakSeq}.");
            previousSeq = row.BreakSeq;
            result.Add(new ShiftTimeCalculator.BreakPair(
                ShiftTimeCalculator.FromLegacyDateTime(row.BreakFrom),
                ShiftTimeCalculator.FromLegacyDateTime(row.BreakTo)));
        }

        return result;
    }

    public static IReadOnlyList<ShiftTimeCalculator.BreakPair> CanonicalizeForShift(
        TimeOnly start,
        TimeOnly end,
        IEnumerable<ShiftTimeCalculator.BreakPair> breaks)
    {
        var pairs = breaks.ToList();
        if (pairs.Count > MaxBreaks)
            throw new ArgumentException($"A shift can have at most {MaxBreaks} breaks.");

        for (var i = 0; i < pairs.Count; i++)
        {
            var err = ShiftTimeCalculator.ValidateBreakPair(pairs[i], i + 1);
            if (err is not null)
                throw new ArgumentException(err);
        }

        var used = pairs.Where(x => x.Start is not null || x.End is not null).ToList();
        var segments = ShiftTimeCalculator.ExpandBreakSegments(start, end, used);
        _ = ShiftTimeCalculator.ComputeNetMinutes(start, end, used);

        return segments
            .Select(x => new ShiftTimeCalculator.BreakPair(MinutesToTime(x.Start), MinutesToTime(x.EndExclusive)))
            .ToList();
    }

    public static void PackToWideColumns(PrShift shift, IReadOnlyList<ShiftTimeCalculator.BreakPair> canonicalBreaks)
    {
        if (canonicalBreaks.Count > MaxBreaks)
            throw new ArgumentException($"A shift can have at most {MaxBreaks} breaks.");

        DateTime From(int index) => index < canonicalBreaks.Count
            ? ShiftTimeCalculator.ToLegacyDateTime(canonicalBreaks[index].Start) ?? DateTime.Today
            : DateTime.Today;
        DateTime To(int index) => index < canonicalBreaks.Count
            ? ShiftTimeCalculator.ToLegacyDateTime(canonicalBreaks[index].End) ?? DateTime.Today
            : DateTime.Today;

        shift.BreakTm1From = From(0);
        shift.BreakTm1To = To(0);
        shift.BreakTm2From = From(1);
        shift.BreakTm2To = To(1);
        shift.BreakTm3From = From(2);
        shift.BreakTm3To = To(2);
        shift.BreakTm4From = From(3);
        shift.BreakTm4To = To(3);
        shift.BreakTm5From = From(4);
        shift.BreakTm5To = To(4);
    }

    public static IReadOnlyList<PrShiftBreak> ToChildRows(
        PrShift shift,
        IReadOnlyList<ShiftTimeCalculator.BreakPair> canonicalBreaks,
        string userId)
    {
        if (string.IsNullOrWhiteSpace(shift.CompCode))
            throw new ArgumentException("Shift company code is required.");
        if (canonicalBreaks.Count > MaxBreaks)
            throw new ArgumentException($"A shift can have at most {MaxBreaks} breaks.");

        var created = DateTime.Now;
        return canonicalBreaks.Select((pair, index) => new PrShiftBreak
        {
            CompCode = shift.CompCode!,
            ShiftCd = shift.ShiftCd,
            BreakSeq = (byte)(index + 1),
            BreakFrom = ShiftTimeCalculator.ToLegacyDateTime(pair.Start) ?? DateTime.Today,
            BreakTo = ShiftTimeCalculator.ToLegacyDateTime(pair.End) ?? DateTime.Today,
            Created = created,
            UserId = userId
        }).ToList();
    }

    private static TimeOnly MinutesToTime(int minutes)
    {
        var normalized = minutes % 1440;
        return TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(normalized));
    }
}
