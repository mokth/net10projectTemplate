namespace ErpWeb.Core.Planning;

/// <summary>
/// Pure in-memory company calendar year generation.
/// Precedence: weekly pattern → overrides → holidays LAST → Off invariant.
/// </summary>
public static class CompanyCalendarGenerator
{
    public sealed class GenerateRequest
    {
        public int Year { get; init; }
        public IReadOnlySet<DayOfWeek> WeeklyOffDays { get; init; } = new HashSet<DayOfWeek>();
        public IReadOnlySet<DateOnly> HolidayDates { get; init; } = new HashSet<DateOnly>();
        /// <summary>Applied before holidays. Holiday always wins to Off.</summary>
        public IReadOnlyDictionary<DateOnly, string>? DayOverrides { get; init; }
    }

    public static IReadOnlyList<CompanyCalendarDayVm> Generate(GenerateRequest request)
    {
        var daysInYear = DateTime.IsLeapYear(request.Year) ? 366 : 365;
        var days = new List<CompanyCalendarDayVm>(daysInYear);
        var offs = request.WeeklyOffDays;
        var holidays = request.HolidayDates;
        var overrides = request.DayOverrides;

        for (var i = 0; i < daysInYear; i++)
        {
            var dt = new DateTime(request.Year, 1, 1).AddDays(i);
            var dateOnly = DateOnly.FromDateTime(dt);
            var code = offs.Contains(dt.DayOfWeek) ? "O" : "W";

            if (overrides is not null && overrides.TryGetValue(dateOnly, out var o)
                && (o == "W" || o == "O"))
            {
                code = o;
            }

            if (holidays.Contains(dateOnly))
                code = "O";

            days.Add(new CompanyCalendarDayVm { Date = dt.Date, DateCd = code });
        }

        return days;
    }

    /// <summary>Re-apply holidays LAST onto an existing day list (Save path).</summary>
    public static List<CompanyCalendarDayVm> ApplyHolidays(
        IEnumerable<CompanyCalendarDayVm> days,
        IReadOnlySet<DateOnly> holidayDates)
    {
        return days.Select(d =>
        {
            var code = d.DateCd == "O" ? "O" : "W";
            if (holidayDates.Contains(DateOnly.FromDateTime(d.Date)))
                code = "O";
            return new CompanyCalendarDayVm { Date = d.Date.Date, DateCd = code };
        }).ToList();
    }
}
