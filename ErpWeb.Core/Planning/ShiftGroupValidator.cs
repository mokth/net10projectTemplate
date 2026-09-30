namespace ErpWeb.Core.Planning;

public static class ShiftGroupValidator
{
    public const int MaxShiftsPerGroup = 2;

    public readonly record struct ShiftInterval(string ShiftCd, TimeOnly Start, TimeOnly End, int NetMinutes);

    /// <summary>
    /// Half-open circular intervals on [0,1440). Overnight expands to two segments.
    /// Touching endpoints are allowed; overlap is not.
    /// Exact 1440-minute coverage is allowed; totals above 1440 are rejected as safety.
    /// </summary>
    public static PlanningServiceResult Validate(
        IReadOnlyList<ShiftInterval> selected,
        bool requireAtLeastOne = true)
    {
        if (requireAtLeastOne && selected.Count == 0)
        {
            return PlanningServiceResult.Fail(
                PlanningErrorCode.ValidationFailed,
                "Select at least one shift.");
        }

        if (selected.Count > MaxShiftsPerGroup)
        {
            return PlanningServiceResult.Fail(
                PlanningErrorCode.ValidationFailed,
                $"Select at most {MaxShiftsPerGroup} shifts.",
                [new ValidationIssue
                {
                    Code = PlanningErrorCode.ValidationFailed,
                    FieldName = "Shifts",
                    Message = $"A shift group may contain at most {MaxShiftsPerGroup} shifts."
                }]);
        }

        foreach (var s in selected)
        {
            if (s.Start == s.End)
            {
                return PlanningServiceResult.Fail(
                    PlanningErrorCode.ValidationFailed,
                    $"Shift {s.ShiftCd}: start and end times must differ.",
                    [new ValidationIssue
                    {
                        Code = PlanningErrorCode.ValidationFailed,
                        FieldName = "Shifts",
                        BusinessKey = s.ShiftCd,
                        Message = "Start and end times must differ."
                    }]);
            }
        }

        var total = selected.Sum(s => s.NetMinutes);
        if (total > 1440)
        {
            return PlanningServiceResult.Fail(
                PlanningErrorCode.ValidationFailed,
                "Selected time exceeds 24 hours. Please choose a time range within a single day.",
                [new ValidationIssue
                {
                    Code = PlanningErrorCode.ValidationFailed,
                    FieldName = "TotalTime",
                    Message = "Total selected shift minutes must not exceed 1440."
                }]);
        }

        var segments = new List<(string Cd, int Start, int End)>();
        foreach (var s in selected)
        {
            var startMin = ShiftTimeCalculator.ToMinutes(s.Start);
            var endMin = ShiftTimeCalculator.ToMinutes(s.End);
            if (endMin < startMin)
            {
                // Overnight: split into [start, 1440) + [0, end)
                segments.Add((s.ShiftCd, startMin, 1440));
                segments.Add((s.ShiftCd, 0, endMin));
            }
            else
            {
                segments.Add((s.ShiftCd, startMin, endMin));
            }
        }

        segments.Sort((x, y) => x.Start.CompareTo(y.Start));
        for (var i = 1; i < segments.Count; i++)
        {
            // [start, end) — equal boundary is OK
            if (segments[i].Start < segments[i - 1].End)
            {
                return PlanningServiceResult.Fail(
                    PlanningErrorCode.ValidationFailed,
                    $"Shifts {segments[i - 1].Cd} and {segments[i].Cd} overlap.",
                    [new ValidationIssue
                    {
                        Code = PlanningErrorCode.ValidationFailed,
                        FieldName = "Shifts",
                        BusinessKey = $"{segments[i - 1].Cd}/{segments[i].Cd}",
                        Message = "Selected shifts overlap."
                    }]);
            }
        }

        return PlanningServiceResult.Ok();
    }
}
