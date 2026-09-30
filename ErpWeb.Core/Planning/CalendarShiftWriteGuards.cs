using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Core.Planning;

/// <summary>Shared holiday + Off invariant enforcement for all PrShiftCalendar writes.</summary>
public static class CalendarShiftWriteGuards
{
    public static void ApplyHolidayAndOffInvariant(
        MachineCalendarDayVm day,
        IReadOnlySet<DateOnly> holidayDates)
    {
        if (holidayDates.Contains(DateOnly.FromDateTime(day.Date)))
            day.DateCd = "O";

        NormalizeOffInvariant(day);
    }

    public static void ApplyHolidayAndOffInvariant(
        PrShiftCalendar row,
        IReadOnlySet<DateOnly> holidayDates)
    {
        if (holidayDates.Contains(DateOnly.FromDateTime(row.Dt)))
            row.DateCd = "O";

        if (!string.Equals(row.DateCd, "W", StringComparison.OrdinalIgnoreCase))
        {
            row.DateCd = "O";
            row.ShfGrpCd = null;
        }
    }

    public static void NormalizeOffInvariant(MachineCalendarDayVm day)
    {
        if (!string.Equals(day.DateCd, "W", StringComparison.OrdinalIgnoreCase))
        {
            day.DateCd = "O";
            day.ShfGrpCd = null;
        }
    }

    public static HashSet<DateOnly> ToHolidaySet(IEnumerable<PrHoliday> holidays)
    {
        var set = new HashSet<DateOnly>();
        foreach (var h in holidays)
        {
            if (h.DateOff is DateTime d)
                set.Add(DateOnly.FromDateTime(d));
        }
        return set;
    }
}
