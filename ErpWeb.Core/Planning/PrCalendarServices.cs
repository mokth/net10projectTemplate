using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public enum CalendarRegenMode
{
    GenerateMissingOnly = 0,
    RegenerateAll = 1
}

public sealed class CompanyCalendarDayVm
{
    public DateTime Date { get; set; }
    public string DateCd { get; set; } = "W";
}

public sealed class MachineCalendarDayVm
{
    public DateTime Date { get; set; }
    public string DateCd { get; set; } = "W";
    public string? ShfGrpCd { get; set; }
}

public sealed class CalendarSaveResult
{
    public string NewFingerprint { get; init; } = string.Empty;
    public int MachinesUpdated { get; init; }
    public int DaysInserted { get; init; }
    public int DaysUpdated { get; init; }
    public string? Message { get; init; }
}

public interface IPrCalendarService
{
    Task<PlanningServiceResult<(IReadOnlyList<CompanyCalendarDayVm> Days, string Fingerprint)>> LoadYearAsync(int year, CancellationToken ct = default);
    Task<PlanningServiceResult<CalendarSaveResult>> SaveYearAsync(int year, IReadOnlyList<CompanyCalendarDayVm> days, string expectedFingerprint, CalendarRegenMode machineMode, CancellationToken ct = default);
}

public interface IPrShiftCalendarService
{
    Task<PlanningServiceResult<(IReadOnlyList<MachineCalendarDayVm> Days, string Fingerprint)>> LoadYearAsync(string machineCd, int year, CancellationToken ct = default);
    Task<PlanningServiceResult<CalendarSaveResult>> SaveYearAsync(string machineCd, int year, IReadOnlyList<MachineCalendarDayVm> days, string expectedFingerprint, CancellationToken ct = default);
    Task<PlanningServiceResult> CopyYearAsync(string sourceMachineCd, string targetMachineCd, int year, bool overwrite, CancellationToken ct = default);
    Task<PlanningServiceResult<CopyYearToAllResult>> CopyYearToAllAsync(string sourceMachineCd, int year, bool overwrite, CancellationToken ct = default);
}

public interface IPrHolidayService
{
    Task<PlanningServiceResult<IReadOnlyList<PrHoliday>>> ListAsync(int? year, CancellationToken ct = default);
    Task<PlanningServiceResult> UpsertAsync(PrHoliday holiday, CancellationToken ct = default);
    Task<PlanningServiceResult> DeleteAsync(int uid, CancellationToken ct = default);
    Task<bool> IsCalendarificEnabledAsync(CancellationToken ct = default);
    Task<PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>> FetchCalendarificCandidatesAsync(int year, string countryCode = "MY", CancellationToken ct = default);
    Task<PlanningServiceResult> PersistCandidatesAsync(IReadOnlyList<CalendarificHolidayCandidate> candidates, CancellationToken ct = default);
}

public sealed class PrCalendarService : IPrCalendarService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly ILogger<PrCalendarService> _logger;

    public PrCalendarService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access, ILogger<PrCalendarService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<(IReadOnlyList<CompanyCalendarDayVm> Days, string Fingerprint)>> LoadYearAsync(int year, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningCompanyCal, ct))
            return PlanningServiceResult<(IReadOnlyList<CompanyCalendarDayVm>, string)>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<(IReadOnlyList<CompanyCalendarDayVm>, string)>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var days = await LoadCompanyDaysAsync(db, scope.CompanyCode, year, ct);
        var fp = CalendarYearFingerprint.ComputeCompany(scope.CompanyCode, year, days);
        return PlanningServiceResult<(IReadOnlyList<CompanyCalendarDayVm>, string)>.Ok((days, fp));
    }

    public async Task<PlanningServiceResult<CalendarSaveResult>> SaveYearAsync(
        int year,
        IReadOnlyList<CompanyCalendarDayVm> days,
        string expectedFingerprint,
        CalendarRegenMode machineMode,
        CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null)
            return PlanningServiceResult<CalendarSaveResult>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        if (!await _access.CanEditAsync(MenuCodes.PlanningCompanyCal, ct))
            return PlanningServiceResult<CalendarSaveResult>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await PlanningCalendarYearLock.AcquireAsync(db, write.CompanyCode, year, ct);

            var persisted = await LoadCompanyDaysAsync(db, write.CompanyCode, year, ct);
            var currentFp = CalendarYearFingerprint.ComputeCompany(write.CompanyCode, year, persisted);
            if (!string.Equals(currentFp, expectedFingerprint, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(ct);
                return PlanningServiceResult<CalendarSaveResult>.Fail(
                    PlanningErrorCode.ConcurrencyConflict,
                    "Company calendar year changed since load.");
            }

            var defaultGrp = await ResolveDefaultShiftGroupAsync(db, write.CompanyCode, ct);
            if (!defaultGrp.Succeeded)
            {
                await tx.RollbackAsync(ct);
                return PlanningServiceResult<CalendarSaveResult>.Fail(defaultGrp.ErrorCode, defaultGrp.Message ?? "No default shift group found.");
            }

            var holidays = await LoadHolidaySetAsync(db, year, ct);
            var rebuilt = CompanyCalendarGenerator.ApplyHolidays(days, holidays);

            var start = new DateTime(year, 1, 1);
            var end = new DateTime(year, 12, 31);
            var existing = await db.PrCalendars
                .Where(x => x.CompCode == write.CompanyCode && x.Dt >= start && x.Dt <= end)
                .ToListAsync(ct);
            db.PrCalendars.RemoveRange(existing);
            await db.SaveChangesAsync(ct);

            foreach (var d in rebuilt)
            {
                db.PrCalendars.Add(new PrCalendar
                {
                    Dt = d.Date.Date,
                    DateCd = CalendarYearFingerprint.NormalizeDateCd(d.DateCd),
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }
            await db.SaveChangesAsync(ct);

            var sync = await SynchronizeMachinesCoreAsync(
                db, write, year, rebuilt, defaultGrp.Value!, machineMode, holidays, ct);

            var newFp = CalendarYearFingerprint.ComputeCompany(write.CompanyCode, year, rebuilt);
            await tx.CommitAsync(ct);
            _logger.LogInformation("Company calendar {Year} saved mode={Mode} company={Company}", year, machineMode, write.CompanyCode);

            return PlanningServiceResult<CalendarSaveResult>.Ok(new CalendarSaveResult
            {
                NewFingerprint = newFp,
                MachinesUpdated = sync.MachinesUpdated,
                DaysInserted = sync.DaysInserted,
                DaysUpdated = sync.DaysUpdated,
                Message = "Company calendar saved."
            }, "Company calendar saved.");
        }
        catch (PlanningCalendarLockException ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult<CalendarSaveResult>.Fail(PlanningErrorCode.ConcurrencyConflict, ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    internal static async Task<List<CompanyCalendarDayVm>> LoadCompanyDaysAsync(
        AppDbContext db, string companyCode, int year, CancellationToken ct)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var rows = await db.PrCalendars.AsNoTracking()
            .Where(x => x.CompCode == companyCode && x.Dt >= start && x.Dt <= end)
            .OrderBy(x => x.Dt)
            .ToListAsync(ct);
        return rows.Select(x => new CompanyCalendarDayVm
        {
            Date = x.Dt.Date,
            DateCd = CalendarYearFingerprint.NormalizeDateCd(x.DateCd)
        }).ToList();
    }

    internal static async Task<HashSet<DateOnly>> LoadHolidaySetAsync(AppDbContext db, int year, CancellationToken ct)
    {
        var rows = await db.PrHolidays.AsNoTracking()
            .Where(x => x.Year == year || (x.DateOff != null && x.DateOff.Value.Year == year))
            .ToListAsync(ct);
        return CalendarShiftWriteGuards.ToHolidaySet(rows);
    }

    internal static async Task<PlanningServiceResult<string>> ResolveDefaultShiftGroupAsync(
        AppDbContext db, string companyCode, CancellationToken ct)
    {
        var rows = await db.PrShiftGroups.AsNoTracking()
            .Where(x => x.CompCode == companyCode)
            .ToListAsync(ct);
        var resolve = PrShiftGroupPlanningService.ResolveDefaultGroupCode(rows);
        if (!resolve.Succeeded)
            return PlanningServiceResult<string>.Fail(resolve.ErrorCode,
                resolve.Message?.Contains("Multiple", StringComparison.OrdinalIgnoreCase) == true
                    ? "Multiple default shift groups found."
                    : "No default shift group found.");

        var members = rows.Where(x => string.Equals(x.ShfGrpCd, resolve.Value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (members.Count == 0)
            return PlanningServiceResult<string>.Fail(PlanningErrorCode.ValidationFailed, "No default shift group found.");

        return PlanningServiceResult<string>.Ok(resolve.Value!);
    }

    /// <summary>Assumes year lock + transaction already held.</summary>
    internal static async Task<(int MachinesUpdated, int DaysInserted, int DaysUpdated)> SynchronizeMachinesCoreAsync(
        AppDbContext db,
        InventoryTenantScope write,
        int year,
        IReadOnlyList<CompanyCalendarDayVm> companyDays,
        string defaultGrp,
        CalendarRegenMode mode,
        IReadOnlySet<DateOnly> holidays,
        CancellationToken ct)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var machines = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == write.CompanyCode)
            .Select(x => x.MachineCd)
            .Distinct()
            .ToListAsync(ct);

        var inserted = 0;
        var updated = 0;
        var machinesUpdated = 0;

        foreach (var mac in machines)
        {
            var macRows = await db.PrShiftCalendars
                .Where(x => x.MachineCode == mac && x.CompCode == write.CompanyCode && x.Dt >= start && x.Dt <= end)
                .ToListAsync(ct);

            if (mode == CalendarRegenMode.RegenerateAll)
            {
                db.PrShiftCalendars.RemoveRange(macRows);
                await db.SaveChangesAsync(ct);
                macRows.Clear();

                foreach (var d in companyDays)
                {
                    var dateCd = CalendarYearFingerprint.NormalizeDateCd(d.DateCd);
                    if (holidays.Contains(DateOnly.FromDateTime(d.Date)))
                        dateCd = "O";
                    db.PrShiftCalendars.Add(new PrShiftCalendar
                    {
                        Dt = d.Date.Date,
                        MachineCode = mac,
                        DateCd = dateCd,
                        ShfGrpCd = dateCd == "O" ? null : defaultGrp,
                        Created = DateTime.Now,
                        UserId = write.UserId,
                        CompCode = write.CompanyCode,
                        BranchCode = write.BranchCode,
                        LocCode = write.LocationCode
                    });
                    inserted++;
                }
                machinesUpdated++;
                await db.SaveChangesAsync(ct);
                continue;
            }

            // GenerateMissingOnly
            var byDate = macRows.ToDictionary(x => x.Dt.Date);
            var touched = false;
            foreach (var d in companyDays)
            {
                var dateOnly = DateOnly.FromDateTime(d.Date);
                var isHoliday = holidays.Contains(dateOnly);

                if (byDate.TryGetValue(d.Date.Date, out var existing))
                {
                    if (isHoliday && (existing.DateCd != "O" || existing.ShfGrpCd is not null))
                    {
                        existing.DateCd = "O";
                        existing.ShfGrpCd = null;
                        existing.Updated = DateTime.Now;
                        existing.UserId = write.UserId;
                        updated++;
                        touched = true;
                    }
                    continue;
                }

                var dateCd = isHoliday ? "O" : CalendarYearFingerprint.NormalizeDateCd(d.DateCd);
                db.PrShiftCalendars.Add(new PrShiftCalendar
                {
                    Dt = d.Date.Date,
                    MachineCode = mac,
                    DateCd = dateCd,
                    ShfGrpCd = dateCd == "O" ? null : defaultGrp,
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
                inserted++;
                touched = true;
            }

            if (touched)
            {
                machinesUpdated++;
                await db.SaveChangesAsync(ct);
            }
        }

        return (machinesUpdated, inserted, updated);
    }
}

public sealed class PrShiftCalendarService : IPrShiftCalendarService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrShiftCalendarService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<PlanningServiceResult<(IReadOnlyList<MachineCalendarDayVm> Days, string Fingerprint)>> LoadYearAsync(string machineCd, int year, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningMacShiftCal, ct))
            return PlanningServiceResult<(IReadOnlyList<MachineCalendarDayVm>, string)>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<(IReadOnlyList<MachineCalendarDayVm>, string)>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        var mac = PlanningCodeNormalizer.NormalizeCode(machineCd);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var days = await LoadMachineDaysAsync(db, scope.CompanyCode, mac, year, ct);
        var fp = CalendarYearFingerprint.Compute(days.Select(d => new CalendarYearFingerprint.CalendarDayRow(d.Date, d.DateCd, d.ShfGrpCd, null)));
        return PlanningServiceResult<(IReadOnlyList<MachineCalendarDayVm>, string)>.Ok((days, fp));
    }

    public async Task<PlanningServiceResult<CalendarSaveResult>> SaveYearAsync(
        string machineCd, int year, IReadOnlyList<MachineCalendarDayVm> days, string expectedFingerprint, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null)
            return PlanningServiceResult<CalendarSaveResult>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        if (!await _access.CanEditAsync(MenuCodes.PlanningMacShiftCal, ct))
            return PlanningServiceResult<CalendarSaveResult>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var mac = PlanningCodeNormalizer.NormalizeCode(machineCd);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await PlanningCalendarYearLock.AcquireAsync(db, write.CompanyCode, year, ct);

            var loaded = await LoadMachineDaysAsync(db, write.CompanyCode, mac, year, ct);
            var currentFp = CalendarYearFingerprint.Compute(loaded.Select(d => new CalendarYearFingerprint.CalendarDayRow(d.Date, d.DateCd, d.ShfGrpCd, null)));
            if (!string.Equals(currentFp, expectedFingerprint, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(ct);
                return PlanningServiceResult<CalendarSaveResult>.Fail(PlanningErrorCode.ConcurrencyConflict, "Machine calendar year changed since load.");
            }

            var holidays = await PrCalendarService.LoadHolidaySetAsync(db, year, ct);
            var prepared = days.Select(d =>
            {
                var vm = new MachineCalendarDayVm { Date = d.Date.Date, DateCd = d.DateCd, ShfGrpCd = d.ShfGrpCd };
                CalendarShiftWriteGuards.ApplyHolidayAndOffInvariant(vm, holidays);
                return vm;
            }).ToList();

            await SaveMachineCalendarCoreAsync(db, write, mac, year, prepared, ct);

            var newFp = CalendarYearFingerprint.Compute(prepared.Select(d => new CalendarYearFingerprint.CalendarDayRow(d.Date, d.DateCd, d.ShfGrpCd, null)));
            await tx.CommitAsync(ct);
            return PlanningServiceResult<CalendarSaveResult>.Ok(new CalendarSaveResult
            {
                NewFingerprint = newFp,
                MachinesUpdated = 1,
                DaysInserted = prepared.Count,
                Message = "Machine calendar saved."
            }, "Machine calendar saved.");
        }
        catch (PlanningCalendarLockException ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult<CalendarSaveResult>.Fail(PlanningErrorCode.ConcurrencyConflict, ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>Assumes year lock + transaction held.</summary>
    internal static async Task SaveMachineCalendarCoreAsync(
        AppDbContext db,
        InventoryTenantScope write,
        string machineCd,
        int year,
        IReadOnlyList<MachineCalendarDayVm> days,
        CancellationToken ct)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var existing = await db.PrShiftCalendars
            .Where(x => x.MachineCode == machineCd && x.CompCode == write.CompanyCode && x.Dt >= start && x.Dt <= end)
            .ToListAsync(ct);
        db.PrShiftCalendars.RemoveRange(existing);
        await db.SaveChangesAsync(ct);

        foreach (var d in days)
        {
            var dateCd = CalendarYearFingerprint.NormalizeDateCd(d.DateCd);
            db.PrShiftCalendars.Add(new PrShiftCalendar
            {
                Dt = d.Date.Date,
                MachineCode = machineCd,
                DateCd = dateCd,
                ShfGrpCd = dateCd == "O" ? null : d.ShfGrpCd,
                Created = DateTime.Now,
                UserId = write.UserId,
                CompCode = write.CompanyCode,
                BranchCode = write.BranchCode,
                LocCode = write.LocationCode
            });
        }
        await db.SaveChangesAsync(ct);
    }

    internal static async Task<List<MachineCalendarDayVm>> LoadMachineDaysAsync(
        AppDbContext db, string companyCode, string machineCd, int year, CancellationToken ct)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var rows = await db.PrShiftCalendars.AsNoTracking()
            .Where(x => x.MachineCode == machineCd && x.CompCode == companyCode && x.Dt >= start && x.Dt <= end)
            .OrderBy(x => x.Dt).ToListAsync(ct);
        return rows.Select(x => new MachineCalendarDayVm
        {
            Date = x.Dt.Date,
            DateCd = CalendarYearFingerprint.NormalizeDateCd(x.DateCd),
            ShfGrpCd = CalendarYearFingerprint.NormalizeDateCd(x.DateCd) == "O" ? null : x.ShfGrpCd
        }).ToList();
    }

    public async Task<PlanningServiceResult> CopyYearAsync(string sourceMachineCd, string targetMachineCd, int year, bool overwrite, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanEditAsync(MenuCodes.PlanningMacShiftCal, ct)) return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMacShiftCal);
        var src = PlanningCodeNormalizer.NormalizeCode(sourceMachineCd);
        var tgt = PlanningCodeNormalizer.NormalizeCode(targetMachineCd);
        if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Source and target machine must differ.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await PlanningCalendarYearLock.AcquireAsync(db, write.CompanyCode, year, ct);
            var holidays = await PrCalendarService.LoadHolidaySetAsync(db, year, ct);
            var result = await CopyYearCoreAsync(db, write, src, tgt, year, overwrite, holidays, ct);
            if (!result.Succeeded)
            {
                await tx.RollbackAsync(ct);
                return result;
            }
            await tx.CommitAsync(ct);
            return result;
        }
        catch (PlanningCalendarLockException ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    internal static async Task<PlanningServiceResult> CopyYearCoreAsync(
        AppDbContext db,
        InventoryTenantScope write,
        string src,
        string tgt,
        int year,
        bool overwrite,
        IReadOnlySet<DateOnly> holidays,
        CancellationToken ct)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var sourceRows = await db.PrShiftCalendars.AsNoTracking()
            .Where(x => x.MachineCode == src && x.CompCode == write.CompanyCode && x.Dt >= start && x.Dt <= end)
            .ToListAsync(ct);
        if (sourceRows.Count == 0)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Source machine has no calendar rows for that year.");

        var targetExisting = await db.PrShiftCalendars
            .Where(x => x.MachineCode == tgt && x.CompCode == write.CompanyCode && x.Dt >= start && x.Dt <= end)
            .ToListAsync(ct);
        if (targetExisting.Count > 0 && !overwrite)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Target machine year already has calendar rows.");

        if (overwrite && targetExisting.Count > 0)
        {
            db.PrShiftCalendars.RemoveRange(targetExisting);
            await db.SaveChangesAsync(ct);
        }

        foreach (var s in sourceRows)
        {
            var dateCd = CalendarYearFingerprint.NormalizeDateCd(s.DateCd);
            if (holidays.Contains(DateOnly.FromDateTime(s.Dt)))
                dateCd = "O";
            db.PrShiftCalendars.Add(new PrShiftCalendar
            {
                Dt = s.Dt.Date,
                MachineCode = tgt,
                DateCd = dateCd,
                ShfGrpCd = dateCd == "O" ? null : s.ShfGrpCd,
                Created = DateTime.Now,
                UserId = write.UserId,
                CompCode = write.CompanyCode,
                BranchCode = write.BranchCode,
                LocCode = write.LocationCode
            });
        }
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Calendar copied.");
    }

    public async Task<PlanningServiceResult<CopyYearToAllResult>> CopyYearToAllAsync(string sourceMachineCd, int year, bool overwrite, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null)
            return PlanningServiceResult<CopyYearToAllResult>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        if (!await _access.CanEditAsync(MenuCodes.PlanningMacShiftCal, ct))
            return PlanningServiceResult<CopyYearToAllResult>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");

        var src = PlanningCodeNormalizer.NormalizeCode(sourceMachineCd);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await PlanningCalendarYearLock.AcquireAsync(db, write.CompanyCode, year, ct);
            var holidays = await PrCalendarService.LoadHolidaySetAsync(db, year, ct);
            var start = new DateTime(year, 1, 1);
            var end = new DateTime(year, 12, 31);

            var sourceRows = await db.PrShiftCalendars.AsNoTracking()
                .Where(x => x.MachineCode == src && x.CompCode == write.CompanyCode && x.Dt >= start && x.Dt <= end)
                .ToListAsync(ct);
            if (sourceRows.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return PlanningServiceResult<CopyYearToAllResult>.Fail(PlanningErrorCode.ValidationFailed, "Source machine has no calendar rows for that year.");
            }

            var machines = await db.PrMachines.AsNoTracking()
                .Where(x => x.CompCode == write.CompanyCode)
                .Select(x => x.MachineCd)
                .Distinct()
                .ToListAsync(ct);

            var results = new List<MachineCopyResult>();
            var skipped = 0;
            var copied = 0;

            foreach (var mac in machines)
            {
                if (string.Equals(mac, src, StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetExisting = await db.PrShiftCalendars
                    .Where(x => x.MachineCode == mac && x.CompCode == write.CompanyCode && x.Dt >= start && x.Dt <= end)
                    .ToListAsync(ct);

                if (targetExisting.Count > 0 && !overwrite)
                {
                    skipped++;
                    results.Add(new MachineCopyResult { MachineCode = mac, Status = "Skipped", Message = "Target year already has rows." });
                    continue;
                }

                if (overwrite && targetExisting.Count > 0)
                {
                    db.PrShiftCalendars.RemoveRange(targetExisting);
                    await db.SaveChangesAsync(ct);
                }

                foreach (var s in sourceRows)
                {
                    var dateCd = CalendarYearFingerprint.NormalizeDateCd(s.DateCd);
                    if (holidays.Contains(DateOnly.FromDateTime(s.Dt)))
                        dateCd = "O";
                    db.PrShiftCalendars.Add(new PrShiftCalendar
                    {
                        Dt = s.Dt.Date,
                        MachineCode = mac,
                        DateCd = dateCd,
                        ShfGrpCd = dateCd == "O" ? null : s.ShfGrpCd,
                        Created = DateTime.Now,
                        UserId = write.UserId,
                        CompCode = write.CompanyCode,
                        BranchCode = write.BranchCode,
                        LocCode = write.LocationCode
                    });
                }
                await db.SaveChangesAsync(ct);
                copied++;
                results.Add(new MachineCopyResult { MachineCode = mac, Status = "Copied" });
            }

            await tx.CommitAsync(ct);
            return PlanningServiceResult<CopyYearToAllResult>.Ok(new CopyYearToAllResult
            {
                Success = true,
                RolledBack = false,
                Copied = copied,
                Skipped = skipped,
                Failed = 0,
                Machines = results
            });
        }
        catch (PlanningCalendarLockException ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult<CopyYearToAllResult>.Fail(PlanningErrorCode.ConcurrencyConflict, ex.Message);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult<CopyYearToAllResult>.Fail(
                PlanningErrorCode.ValidationFailed,
                $"Copy ALL failed and was rolled back: {ex.Message}",
                new CopyYearToAllResult { Success = false, RolledBack = true });
        }
    }
}

public sealed class PrHolidayService : IPrHolidayService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly ICalendarificHolidayClient _calendarific;

    public PrHolidayService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access,
        ICalendarificHolidayClient calendarific)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _calendarific = calendarific;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrHoliday>>> ListAsync(int? year, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningCompanyCal, ct))
            return PlanningServiceResult<IReadOnlyList<PrHoliday>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.PrHolidays.AsNoTracking().AsQueryable();
        if (year is not null) q = q.Where(x => x.Year == year || (x.DateOff != null && x.DateOff.Value.Year == year));
        var rows = await q.OrderBy(x => x.DateOff).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrHoliday>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> UpsertAsync(PrHoliday holiday, CancellationToken ct = default)
    {
        if (!await _access.CanEditAsync(MenuCodes.PlanningCompanyCal, ct) && !await _access.CanAddAsync(MenuCodes.PlanningCompanyCal, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningCompanyCal);

        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var years = new List<int>();
            if (holiday.Uid != 0)
            {
                var existing = await db.PrHolidays.AsNoTracking().FirstOrDefaultAsync(x => x.Uid == holiday.Uid, ct);
                if (existing?.DateOff is DateTime oldDt)
                    years.Add(oldDt.Year);
            }
            var newYear = holiday.DateOff?.Year ?? holiday.Year ?? DateTime.Today.Year;
            years.Add(newYear);
            await PlanningCalendarYearLock.AcquireYearsAsync(db, write.CompanyCode, years, ct);

            if (holiday.Uid == 0)
                db.PrHolidays.Add(holiday);
            else
                db.PrHolidays.Update(holiday);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PlanningServiceResult.Ok("Holiday saved.");
        }
        catch (PlanningCalendarLockException ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<PlanningServiceResult> DeleteAsync(int uid, CancellationToken ct = default)
    {
        if (!await _access.CanDeleteAsync(MenuCodes.PlanningCompanyCal, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningCompanyCal);

        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var e = await db.PrHolidays.FirstOrDefaultAsync(x => x.Uid == uid, ct);
            if (e is null)
            {
                await tx.CommitAsync(ct);
                return PlanningServiceResult.Ok("Holiday deleted.");
            }

            var year = e.DateOff?.Year ?? e.Year ?? DateTime.Today.Year;
            await PlanningCalendarYearLock.AcquireAsync(db, write.CompanyCode, year, ct);
            db.PrHolidays.Remove(e);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PlanningServiceResult.Ok("Holiday deleted.");
        }
        catch (PlanningCalendarLockException ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public Task<bool> IsCalendarificEnabledAsync(CancellationToken ct = default) =>
        Task.FromResult(_calendarific.IsEnabled);

    public async Task<PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>> FetchCalendarificCandidatesAsync(
        int year, string countryCode = "MY", CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningCompanyCal, ct))
            return PlanningServiceResult<IReadOnlyList<CalendarificHolidayCandidate>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        return await _calendarific.FetchAsync(year, countryCode, ct: ct);
    }

    public async Task<PlanningServiceResult> PersistCandidatesAsync(IReadOnlyList<CalendarificHolidayCandidate> candidates, CancellationToken ct = default)
    {
        if (!await _access.CanAddAsync(MenuCodes.PlanningCompanyCal, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningCompanyCal);

        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var years = candidates.Select(c => c.Year).Distinct();
            await PlanningCalendarYearLock.AcquireYearsAsync(db, write.CompanyCode, years, ct);

            foreach (var c in candidates)
            {
                var exists = await db.PrHolidays.AnyAsync(x => x.DateOff == c.DateOff.Date, ct);
                if (exists) continue;
                db.PrHolidays.Add(new PrHoliday
                {
                    DateOff = c.DateOff.Date,
                    Description = c.Description,
                    Year = c.Year
                });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PlanningServiceResult.Ok("Holidays persisted.");
        }
        catch (PlanningCalendarLockException ex)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, ex.Message);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
