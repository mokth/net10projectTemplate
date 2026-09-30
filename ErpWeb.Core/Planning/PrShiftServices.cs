using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public sealed class PrShiftEditVm
{
    public string ShiftCd { get; set; } = string.Empty;
    public string? ShiftDes { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public TimeOnly? OtStart { get; set; }
    public ShiftTimeCalculator.BreakPair Break1 { get; set; }
    public ShiftTimeCalculator.BreakPair Break2 { get; set; }
    public ShiftTimeCalculator.BreakPair Break3 { get; set; }
    public ShiftTimeCalculator.BreakPair Break4 { get; set; }
    public ShiftTimeCalculator.BreakPair Break5 { get; set; }
    public string? OverrideMrpPlan { get; set; }
    public DateTime? OriginalUpdated { get; set; }
    public bool IsNew { get; set; }
}

public sealed class PrShiftGroupEditVm
{
    public string ShfGrpCd { get; set; } = string.Empty;
    public string? ShfGrpDes { get; set; }
    public IReadOnlyList<string> SelectedShiftCds { get; set; } = [];
    public bool DefaultGrp { get; set; }
    public string? ShiftColor { get; set; }
    public bool IsNew { get; set; }
}

public interface IPrShiftService
{
    Task<PlanningServiceResult<IReadOnlyList<PrShift>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult<PrShiftEditVm>> GetAsync(string shiftCd, CancellationToken ct = default);
    Task<PlanningServiceResult> SaveAsync(PrShiftEditVm vm, CancellationToken ct = default);
    Task<PlanningServiceResult> DeleteAsync(string shiftCd, CancellationToken ct = default);
}

public interface IPrShiftGroupService
{
    Task<PlanningServiceResult<IReadOnlyList<PrShiftGroup>>> ListGroupsAsync(CancellationToken ct = default);
    Task<PlanningServiceResult<PrShiftGroupEditVm>> GetAsync(string shfGrpCd, CancellationToken ct = default);
    Task<PlanningServiceResult> SaveAsync(PrShiftGroupEditVm vm, CancellationToken ct = default);
    Task<PlanningServiceResult> DeleteAsync(string shfGrpCd, CancellationToken ct = default);
}

public sealed class PrShiftService : IPrShiftService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrShiftService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrShift>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningShift, ct))
            return PlanningServiceResult<IReadOnlyList<PrShift>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<IReadOnlyList<PrShift>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrShifts.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode).OrderBy(x => x.StartTm).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrShift>>.Ok(rows);
    }

    public async Task<PlanningServiceResult<PrShiftEditVm>> GetAsync(string shiftCd, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningShift, ct))
            return PlanningServiceResult<PrShiftEditVm>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<PrShiftEditVm>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        var code = PlanningCodeNormalizer.NormalizeCode(shiftCd);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var e = await db.PrShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ShiftCd == code && x.CompCode == scope.CompanyCode, ct);
        if (e is null) return PlanningServiceResult<PrShiftEditVm>.Fail(PlanningErrorCode.ValidationFailed, "Shift not found.");

        // Case A: null unused. Case C legacy midnight sentinel treated as unused for load compatibility.
        static ShiftTimeCalculator.BreakPair Map(DateTime? f, DateTime? t) =>
            ShiftTimeCalculator.FromLegacyPair(f, t, treatMidnightAsUnused: true);

        var vm = new PrShiftEditVm
        {
            ShiftCd = e.ShiftCd,
            ShiftDes = e.ShiftDes,
            Start = ShiftTimeCalculator.FromLegacyDateTime(e.StartTm) ?? default,
            End = ShiftTimeCalculator.FromLegacyDateTime(e.EndTm) ?? default,
            OtStart = ShiftTimeCalculator.FromLegacyDateTime(e.OtStartTime),
            Break1 = Map(e.BreakTm1From, e.BreakTm1To),
            Break2 = Map(e.BreakTm2From, e.BreakTm2To),
            Break3 = Map(e.BreakTm3From, e.BreakTm3To),
            Break4 = Map(e.BreakTm4From, e.BreakTm4To),
            Break5 = Map(e.BreakTm5From, e.BreakTm5To),
            OverrideMrpPlan = e.OverrideMrpPlan,
            OriginalUpdated = e.Updated,
            IsNew = false
        };
        return PlanningServiceResult<PrShiftEditVm>.Ok(vm);
    }

    public async Task<PlanningServiceResult> SaveAsync(PrShiftEditVm vm, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (vm.IsNew && !await _access.CanAddAsync(MenuCodes.PlanningShift, ct)) return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningShift);
        if (!vm.IsNew && !await _access.CanEditAsync(MenuCodes.PlanningShift, ct)) return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningShift);

        var code = PlanningCodeNormalizer.NormalizeCode(vm.ShiftCd);
        var err = PlanningInputValidation.ValidateCode(code);
        if (err is not null) return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, err);

        var breaks = new[] { vm.Break1, vm.Break2, vm.Break3, vm.Break4, vm.Break5 };
        for (var i = 0; i < breaks.Length; i++)
        {
            var be = ShiftTimeCalculator.ValidateBreakPair(breaks[i], i + 1);
            if (be is not null) return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, be);
        }

        int net;
        try { net = ShiftTimeCalculator.ComputeNetMinutes(vm.Start, vm.End, breaks); }
        catch (ArgumentException ex) { return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, ex.Message); }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            PrShift entity;
            if (vm.IsNew)
            {
                if (await db.PrShifts.AnyAsync(x => x.ShiftCd == code && x.CompCode == write.CompanyCode, ct))
                    return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Duplicate shift {code}.");
                entity = new PrShift { ShiftCd = code, Created = DateTime.Now, UserId = write.UserId, CompCode = write.CompanyCode, BranchCode = write.BranchCode, LocCode = write.LocationCode };
                db.PrShifts.Add(entity);
            }
            else
            {
                entity = await db.PrShifts.FirstAsync(x => x.ShiftCd == code && x.CompCode == write.CompanyCode, ct);
                if (vm.OriginalUpdated is not null && entity.Updated != vm.OriginalUpdated)
                    return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, "Shift changed since load.");
                entity.Updated = DateTime.Now;
                entity.UpdatedUid = write.UserId;
            }

            entity.ShiftDes = PlanningCodeNormalizer.NormalizeDescription(vm.ShiftDes);
            entity.StartTm = ShiftTimeCalculator.ToLegacyDateTime(vm.Start);
            entity.EndTm = ShiftTimeCalculator.ToLegacyDateTime(vm.End);
            entity.OtStartTime = ShiftTimeCalculator.ToLegacyDateTime(vm.OtStart) ?? DateTime.Today;
            // Case A persistence: unused => null. Note: if SQL non-nullable, EF may need midnight sentinel — Case C restriction documented.
            entity.BreakTm1From = ShiftTimeCalculator.ToLegacyDateTime(vm.Break1.Start);
            entity.BreakTm1To = ShiftTimeCalculator.ToLegacyDateTime(vm.Break1.End);
            entity.BreakTm2From = ShiftTimeCalculator.ToLegacyDateTime(vm.Break2.Start);
            entity.BreakTm2To = ShiftTimeCalculator.ToLegacyDateTime(vm.Break2.End);
            entity.BreakTm3From = ShiftTimeCalculator.ToLegacyDateTime(vm.Break3.Start);
            entity.BreakTm3To = ShiftTimeCalculator.ToLegacyDateTime(vm.Break3.End);
            entity.BreakTm4From = ShiftTimeCalculator.ToLegacyDateTime(vm.Break4.Start);
            entity.BreakTm4To = ShiftTimeCalculator.ToLegacyDateTime(vm.Break4.End);
            entity.BreakTm5From = ShiftTimeCalculator.ToLegacyDateTime(vm.Break5.Start);
            entity.BreakTm5To = ShiftTimeCalculator.ToLegacyDateTime(vm.Break5.End);
            entity.OverrideMrpPlan = vm.OverrideMrpPlan;
            entity.TotalTime = ShiftTimeCalculator.EncodeTotalTime(net);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PlanningServiceResult.Ok("Shift saved.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<PlanningServiceResult> DeleteAsync(string shiftCd, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanDeleteAsync(MenuCodes.PlanningShift, ct)) return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningShift);
        var code = PlanningCodeNormalizer.NormalizeCode(shiftCd);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.PrShiftGroups.AnyAsync(x => x.ShiftCd == code && x.CompCode == write.CompanyCode, ct))
            return PlanningServiceResult.Fail(PlanningErrorCode.DependencyExists, "Shift is used by a shift group.");
        var e = await db.PrShifts.FirstOrDefaultAsync(x => x.ShiftCd == code && x.CompCode == write.CompanyCode, ct);
        if (e is null) return PlanningServiceResult.Ok();
        db.PrShifts.Remove(e);
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Shift deleted.");
    }
}

public sealed class PrShiftGroupService : IPrShiftGroupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly ILogger<PrShiftGroupService> _logger;

    public PrShiftGroupService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access, ILogger<PrShiftGroupService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrShiftGroup>>> ListGroupsAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningShiftGroup, ct))
            return PlanningServiceResult<IReadOnlyList<PrShiftGroup>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<IReadOnlyList<PrShiftGroup>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrShiftGroups.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode).OrderBy(x => x.ShfGrpCd).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrShiftGroup>>.Ok(rows);
    }

    public async Task<PlanningServiceResult<PrShiftGroupEditVm>> GetAsync(string shfGrpCd, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningShiftGroup, ct))
            return PlanningServiceResult<PrShiftGroupEditVm>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<PrShiftGroupEditVm>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        var code = PlanningCodeNormalizer.NormalizeCode(shfGrpCd);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrShiftGroups.AsNoTracking().Where(x => x.ShfGrpCd == code && x.CompCode == scope.CompanyCode).ToListAsync(ct);
        if (rows.Count == 0) return PlanningServiceResult<PrShiftGroupEditVm>.Fail(PlanningErrorCode.ValidationFailed, "Shift group not found.");
        var first = rows[0];
        return PlanningServiceResult<PrShiftGroupEditVm>.Ok(new PrShiftGroupEditVm
        {
            ShfGrpCd = first.ShfGrpCd,
            ShfGrpDes = first.ShfGrpDes,
            SelectedShiftCds = rows.Select(x => x.ShiftCd).ToList(),
            DefaultGrp = first.DefaultGrp == true,
            ShiftColor = first.ShiftColor,
            IsNew = false
        });
    }

    public async Task<PlanningServiceResult> SaveAsync(PrShiftGroupEditVm vm, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (vm.IsNew && !await _access.CanAddAsync(MenuCodes.PlanningShiftGroup, ct)) return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningShiftGroup);
        if (!vm.IsNew && !await _access.CanEditAsync(MenuCodes.PlanningShiftGroup, ct)) return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningShiftGroup);

        var grp = PlanningCodeNormalizer.NormalizeCode(vm.ShfGrpCd);
        var err = PlanningInputValidation.ValidateCode(grp);
        if (err is not null) return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, err);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var selected = vm.SelectedShiftCds.Select(PlanningCodeNormalizer.NormalizeCode).Distinct().ToList();
        var shifts = await db.PrShifts.AsNoTracking()
            .Where(x => selected.Contains(x.ShiftCd) && x.CompCode == write.CompanyCode)
            .ToListAsync(ct);
        if (shifts.Count != selected.Count)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "One or more shifts were not found.");

        var intervals = new List<ShiftGroupValidator.ShiftInterval>();
        foreach (var s in shifts)
        {
            var start = ShiftTimeCalculator.FromLegacyDateTime(s.StartTm) ?? default;
            var end = ShiftTimeCalculator.FromLegacyDateTime(s.EndTm) ?? default;
            var breaks = new[]
            {
                ShiftTimeCalculator.FromLegacyPair(s.BreakTm1From, s.BreakTm1To, true),
                ShiftTimeCalculator.FromLegacyPair(s.BreakTm2From, s.BreakTm2To, true),
                ShiftTimeCalculator.FromLegacyPair(s.BreakTm3From, s.BreakTm3To, true),
                ShiftTimeCalculator.FromLegacyPair(s.BreakTm4From, s.BreakTm4To, true),
                ShiftTimeCalculator.FromLegacyPair(s.BreakTm5From, s.BreakTm5To, true)
            };
            int net;
            try { net = ShiftTimeCalculator.ComputeNetMinutes(start, end, breaks); }
            catch (ArgumentException ex) { return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, ex.Message); }
            intervals.Add(new ShiftGroupValidator.ShiftInterval(s.ShiftCd, start, end, net));
        }

        var validation = ShiftGroupValidator.Validate(intervals);
        if (!validation.Succeeded) return validation;

        var intervalByShift = intervals.ToDictionary(x => x.ShiftCd, StringComparer.OrdinalIgnoreCase);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (vm.DefaultGrp)
            {
                // Tenant-scoped clear of other defaults
                var others = await db.PrShiftGroups
                    .Where(x => x.CompCode == write.CompanyCode && x.DefaultGrp == true && x.ShfGrpCd != grp)
                    .ToListAsync(ct);
                foreach (var o in others) o.DefaultGrp = false;
            }

            var existing = await db.PrShiftGroups.Where(x => x.ShfGrpCd == grp && x.CompCode == write.CompanyCode).ToListAsync(ct);
            if (vm.IsNew && existing.Count > 0)
                return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Shift group {grp} already exists.");

            var keep = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            foreach (var row in existing.Where(x => !keep.Contains(x.ShiftCd)))
                db.PrShiftGroups.Remove(row);

            foreach (var shiftCd in selected)
            {
                var perShiftMinutes = intervalByShift[shiftCd].NetMinutes;
                var row = existing.FirstOrDefault(x => x.ShiftCd == shiftCd);
                if (row is null)
                {
                    db.PrShiftGroups.Add(new PrShiftGroup
                    {
                        ShfGrpCd = grp,
                        ShfGrpDes = PlanningCodeNormalizer.NormalizeDescription(vm.ShfGrpDes) ?? grp,
                        ShiftCd = shiftCd,
                        TotalTime = perShiftMinutes,
                        DefaultGrp = vm.DefaultGrp,
                        ShiftColor = vm.ShiftColor,
                        Created = DateTime.Now,
                        UserId = write.UserId,
                        CompCode = write.CompanyCode,
                        BranchCode = write.BranchCode,
                        LocCode = write.LocationCode
                    });
                }
                else
                {
                    row.ShfGrpDes = PlanningCodeNormalizer.NormalizeDescription(vm.ShfGrpDes) ?? row.ShfGrpDes;
                    row.TotalTime = perShiftMinutes;
                    row.DefaultGrp = vm.DefaultGrp;
                    row.ShiftColor = vm.ShiftColor;
                    row.Updated = DateTime.Now;
                    row.UpdatedUid = write.UserId;
                }
            }

            await db.SaveChangesAsync(ct);

            // Auto-default if only one group remains
            var groupCodes = await db.PrShiftGroups.Where(x => x.CompCode == write.CompanyCode)
                .Select(x => x.ShfGrpCd).Distinct().ToListAsync(ct);
            if (groupCodes.Count == 1)
            {
                var only = groupCodes[0];
                foreach (var r in await db.PrShiftGroups.Where(x => x.ShfGrpCd == only && x.CompCode == write.CompanyCode).ToListAsync(ct))
                    r.DefaultGrp = true;
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation("ShiftGroup {Group} saved for {Company}", grp, write.CompanyCode);
            return PlanningServiceResult.Ok("Shift group saved.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<PlanningServiceResult> DeleteAsync(string shfGrpCd, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanDeleteAsync(MenuCodes.PlanningShiftGroup, ct)) return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningShiftGroup);
        var code = PlanningCodeNormalizer.NormalizeCode(shfGrpCd);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await db.PrShiftCalendars.AnyAsync(x => x.ShfGrpCd == code && x.CompCode == write.CompanyCode, ct))
            return PlanningServiceResult.Fail(PlanningErrorCode.DependencyExists, "Shift group is used on machine calendars.");
        var rows = await db.PrShiftGroups.Where(x => x.ShfGrpCd == code && x.CompCode == write.CompanyCode).ToListAsync(ct);
        db.PrShiftGroups.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Shift group deleted.");
    }
}
