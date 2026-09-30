namespace ErpWeb.Core.Planning;

/// <summary>
/// Pure in-memory machine calendar year generation (draft until Save).
/// </summary>
public static class MachineCalendarGenerator
{
    public sealed class GenerateRequest
    {
        public int Year { get; init; }
        public IReadOnlySet<DayOfWeek> OffWeekdays { get; init; } = new HashSet<DayOfWeek>();
        public IReadOnlySet<DayOfWeek> WorkWeekdays { get; init; } = new HashSet<DayOfWeek>();
        /// <summary>Required when WorkWeekdays is non-empty.</summary>
        public string? SelectedShiftGroupCd { get; init; }
        /// <summary>Used only for fallback weekdays (neither Work nor Off).</summary>
        public string? DefaultShiftGroupCd { get; init; }
        public IReadOnlySet<DateOnly> HolidayDates { get; init; } = new HashSet<DateOnly>();
    }

    public static PlanningServiceResult ValidateRequest(GenerateRequest request)
    {
        if (request.OffWeekdays.Overlaps(request.WorkWeekdays))
        {
            return PlanningServiceResult.Fail(
                PlanningErrorCode.ValidationFailed,
                "A weekday cannot be both Work and Off.");
        }

        if (request.WorkWeekdays.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(request.SelectedShiftGroupCd))
            {
                return PlanningServiceResult.Fail(
                    PlanningErrorCode.ValidationFailed,
                    "A shift group is required when explicit Work weekdays are selected.");
            }
        }

        var requiresDefault = RequiresDefaultGroup(request.OffWeekdays, request.WorkWeekdays);
        if (requiresDefault && string.IsNullOrWhiteSpace(request.DefaultShiftGroupCd))
        {
            return PlanningServiceResult.Fail(
                PlanningErrorCode.ValidationFailed,
                "Default shift group is required for weekdays that are neither Work nor Off.");
        }

        return PlanningServiceResult.Ok();
    }

    public static bool RequiresDefaultGroup(IReadOnlySet<DayOfWeek> off, IReadOnlySet<DayOfWeek> work)
    {
        foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
        {
            if (!off.Contains(d) && !work.Contains(d))
                return true;
        }
        return false;
    }

    public static PlanningServiceResult<IReadOnlyList<MachineCalendarDayVm>> Generate(GenerateRequest request)
    {
        var validation = ValidateRequest(request);
        if (!validation.Succeeded)
            return PlanningServiceResult<IReadOnlyList<MachineCalendarDayVm>>.Fail(validation.ErrorCode, validation.Message ?? "Invalid generate request.");

        var daysInYear = DateTime.IsLeapYear(request.Year) ? 366 : 365;
        var days = new List<MachineCalendarDayVm>(daysInYear);
        for (var i = 0; i < daysInYear; i++)
        {
            var dt = new DateTime(request.Year, 1, 1).AddDays(i);
            var dateOnly = DateOnly.FromDateTime(dt);
            string dateCd;
            string? shfGrp;

            if (request.OffWeekdays.Contains(dt.DayOfWeek))
            {
                dateCd = "O";
                shfGrp = null;
            }
            else if (request.WorkWeekdays.Contains(dt.DayOfWeek))
            {
                dateCd = "W";
                shfGrp = PlanningCodeNormalizer.NormalizeCode(request.SelectedShiftGroupCd!);
            }
            else
            {
                dateCd = "W";
                shfGrp = PlanningCodeNormalizer.NormalizeCode(request.DefaultShiftGroupCd!);
            }

            if (request.HolidayDates.Contains(dateOnly))
            {
                dateCd = "O";
                shfGrp = null;
            }

            // Off invariant: DateCd == O => ShfGrpCd == null
            if (dateCd == "O")
                shfGrp = null;

            days.Add(new MachineCalendarDayVm
            {
                Date = dt.Date,
                DateCd = dateCd,
                ShfGrpCd = shfGrp
            });
        }

        return PlanningServiceResult<IReadOnlyList<MachineCalendarDayVm>>.Ok(days);
    }
}

public sealed class MachineCopyResult
{
    public string MachineCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty; // Copied | Skipped | Failed
    public string? Message { get; init; }
}

public sealed class CopyYearToAllResult
{
    public bool Success { get; init; }
    public bool RolledBack { get; init; }
    public int Copied { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public string? FailedMachineCode { get; init; }
    public IReadOnlyList<MachineCopyResult> Machines { get; init; } = [];
}
