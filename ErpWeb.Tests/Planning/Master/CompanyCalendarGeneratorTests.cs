using ErpWeb.Core.Planning;

namespace ErpWeb.Tests.Planning.Master;
public class CompanyCalendarGeneratorTests
{
    [Fact]
    public void U1_SatSun_offs_2026()
    {
        var days = CompanyCalendarGenerator.Generate(new CompanyCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            WeeklyOffDays = new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }
        });
        Assert.Equal(365, days.Count);
        Assert.All(days.Where(d => d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday),
            d => Assert.Equal("O", d.DateCd));
        Assert.Contains(days, d => d.Date.DayOfWeek == DayOfWeek.Monday && d.DateCd == "W");
    }

    [Fact]
    public void U2_leap_year_2024()
    {
        var days = CompanyCalendarGenerator.Generate(new CompanyCalendarGenerator.GenerateRequest
        {
            Year = 2024,
            WeeklyOffDays = new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }
        });
        Assert.Equal(366, days.Count);
        Assert.Contains(days, d => d.Date == new DateTime(2024, 2, 29));
    }

    [Fact]
    public void U3_holiday_on_weekday()
    {
        var days = CompanyCalendarGenerator.Generate(new CompanyCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            WeeklyOffDays = new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday },
            HolidayDates = new HashSet<DateOnly> { new(2026, 5, 1) }
        });
        Assert.Equal("O", days.Single(d => d.Date == new DateTime(2026, 5, 1)).DateCd);
    }

    [Fact]
    public void U5_work_override_on_holiday_holiday_wins()
    {
        var days = CompanyCalendarGenerator.Generate(new CompanyCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            WeeklyOffDays = new HashSet<DayOfWeek>(),
            HolidayDates = new HashSet<DateOnly> { new(2026, 5, 1) },
            DayOverrides = new Dictionary<DateOnly, string> { [new(2026, 5, 1)] = "W" }
        });
        Assert.Equal("O", days.Single(d => d.Date == new DateTime(2026, 5, 1)).DateCd);
    }

    [Fact]
    public void U6_empty_offs_seven_day_week()
    {
        var days = CompanyCalendarGenerator.Generate(new CompanyCalendarGenerator.GenerateRequest
        {
            Year = 2026,
            WeeklyOffDays = new HashSet<DayOfWeek>()
        });
        Assert.All(days, d => Assert.Equal("W", d.DateCd));
    }

    [Fact]
    public void Empty_year_fingerprint_deterministic()
    {
        var a = CalendarYearFingerprint.EmptyCompany("ABC", 2026);
        var b = CalendarYearFingerprint.ComputeCompany("abc", 2026, Array.Empty<CompanyCalendarDayVm>());
        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_changes_when_day_changes()
    {
        var d1 = new[] { new CompanyCalendarDayVm { Date = new DateTime(2026, 1, 1), DateCd = "W" } };
        var d2 = new[] { new CompanyCalendarDayVm { Date = new DateTime(2026, 1, 1), DateCd = "O" } };
        Assert.NotEqual(
            CalendarYearFingerprint.ComputeCompany("ABC", 2026, d1),
            CalendarYearFingerprint.ComputeCompany("ABC", 2026, d2));
    }

    [Fact]
    public void Holiday_guard_forces_machine_day_off()
    {
        var day = new MachineCalendarDayVm
        {
            Date = new DateTime(2026, 5, 1),
            DateCd = "W",
            ShfGrpCd = "G1"
        };
        CalendarShiftWriteGuards.ApplyHolidayAndOffInvariant(day, new HashSet<DateOnly> { new(2026, 5, 1) });
        Assert.Equal("O", day.DateCd);
        Assert.Null(day.ShfGrpCd);
    }
}
