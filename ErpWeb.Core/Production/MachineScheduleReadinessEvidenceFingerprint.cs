using System.Security.Cryptography;
using System.Text.Json;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// Builds a deterministic fingerprint from every persisted source that can change machine
/// productive capacity. It is recomputed during readiness checks so a calendar, shift, holiday,
/// machine-master, or planned preventive change invalidates the stored preflight certificate.
/// </summary>
internal static class MachineScheduleReadinessEvidenceFingerprint
{
    public const string SchemaContractVersion = "allocation-v1";
    public const string ReleaseContractVersion = "release-reservation-v1";

    public static async Task<string> ComputeCalendarSourceAsync(
        AppDbContext db,
        string companyCode,
        CancellationToken cancellationToken)
    {
        var company = PlanningCodeNormalizer.NormalizeCode(companyCode);
        var machineCalendar = await db.PrShiftCalendars.AsNoTracking()
            .Where(x => x.CompCode == company)
            .OrderBy(x => x.MachineCode).ThenBy(x => x.Dt)
            .Select(x => new { x.MachineCode, x.Dt, x.DateCd, x.ShfGrpCd, x.BranchCode, x.LocCode })
            .ToListAsync(cancellationToken);

        var calendarTemplate = await db.PrCalendars.AsNoTracking()
            .Where(x => x.CompCode == company)
            .OrderBy(x => x.Dt)
            .Select(x => new { x.Dt, x.DateCd, x.BranchCode, x.LocCode })
            .ToListAsync(cancellationToken);

        var shiftGroups = await db.PrShiftGroups.AsNoTracking()
            .Where(x => x.CompCode == company)
            .OrderBy(x => x.ShfGrpCd).ThenBy(x => x.ShiftCd)
            .Select(x => new { x.ShfGrpCd, x.ShiftCd, x.TotalTime, x.DefaultGrp, x.BranchCode, x.LocCode })
            .ToListAsync(cancellationToken);

        var shifts = await db.PrShifts.AsNoTracking()
            .Where(x => x.CompCode == company)
            .OrderBy(x => x.ShiftCd)
            .Select(x => new
            {
                x.ShiftCd,
                x.StartTm,
                x.EndTm,
                x.BreakStorageVersion,
                x.BreakTm1From,
                x.BreakTm1To,
                x.BreakTm2From,
                x.BreakTm2To,
                x.BreakTm3From,
                x.BreakTm3To,
                x.BreakTm4From,
                x.BreakTm4To,
                x.BreakTm5From,
                x.BreakTm5To,
                x.BranchCode,
                x.LocCode,
                x.TotalTime
            })
            .ToListAsync(cancellationToken);

        var shiftBreaks = await db.PrShiftBreaks.AsNoTracking()
            .Where(x => x.CompCode == company)
            .OrderBy(x => x.ShiftCd).ThenBy(x => x.BreakSeq)
            .Select(x => new { x.ShiftCd, x.BreakSeq, x.BreakFrom, x.BreakTo })
            .ToListAsync(cancellationToken);

        var holidays = await db.PrHolidays.AsNoTracking()
            .OrderBy(x => x.DateOff).ThenBy(x => x.Uid)
            .Select(x => new { x.DateOff, x.Year, x.Description })
            .ToListAsync(cancellationToken);

        var machines = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == company)
            .OrderBy(x => x.MachineCd)
            .Select(x => new
            {
                x.MachineCd,
                x.ProcessCd,
                x.Active,
                x.ConversionTime,
                x.StartupTime,
                x.QueueTime,
                x.BranchCode,
                x.LocCode
            })
            .ToListAsync(cancellationToken);

        var preventives = await db.PrPreventives.AsNoTracking()
            .Where(x => x.CompCode == company)
            .OrderBy(x => x.MachineCd).ThenBy(x => x.DownDt).ThenBy(x => x.Uid)
            .Select(x => new
            {
                x.MachineCd,
                x.DownDt,
                x.StartTm,
                x.EndTm,
                x.Status,
                x.BranchCode,
                x.LocCode
            })
            .ToListAsync(cancellationToken);

        var payload = new
        {
            FingerprintVersion = "calendar-source-v1",
            Company = company,
            MachineCalendar = machineCalendar,
            CalendarTemplate = calendarTemplate,
            ShiftGroups = shiftGroups,
            Shifts = shifts,
            ShiftBreaks = shiftBreaks,
            Holidays = holidays,
            Machines = machines,
            Preventives = preventives
        };

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }
}
