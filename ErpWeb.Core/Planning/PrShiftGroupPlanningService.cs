using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

/// <summary>Reusable shift/group definition — no date-anchored WorkSlots.</summary>
public sealed class ShiftGroupDefinition
{
    public string ShiftGroupCode { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyList<ShiftDefinition> Shifts { get; init; } = [];
}

public sealed class ShiftDefinition
{
    public string ShiftCd { get; init; } = string.Empty;
    public string? ShiftDes { get; init; }
    public TimeOnly Start { get; init; }
    public TimeOnly End { get; init; }
    public int GrossMinutes { get; init; }
    public int BreakMinutes { get; init; }
    public int NetMinutes { get; init; }
    public IReadOnlyList<ShiftTimeCalculator.BreakPair> Breaks { get; init; } = [];
}

/// <summary>Date-specific planning info with WorkSlots anchored to <see cref="Date"/>.</summary>
public sealed class ShiftGroupInfo
{
    public DateOnly Date { get; init; }
    public string ShiftGroupCode { get; init; } = string.Empty;
    public int ShiftCount { get; init; }
    public int TotalWorkingMinPerDay { get; init; }
    public int TotalBreakMinPerDay { get; init; }
    /// <summary>Legacy formula 1440/count — NOT production capacity. Prefer WorkSlots/NetMinutes.</summary>
    public int? NominalDaySegmentMinutes { get; init; }
    public IReadOnlyList<ShiftDayInfo> Shifts { get; init; } = [];
}

public sealed class ShiftDayInfo
{
    public string ShiftCd { get; init; } = string.Empty;
    public DateTime StartDateTime { get; init; }
    public DateTime EndDateTime { get; init; }
    public int GrossMinutes { get; init; }
    public int BreakMinutes { get; init; }
    public int NetMinutes { get; init; }
    public IReadOnlyList<WorkSlot> WorkSlots { get; init; } = [];
}

public sealed class WorkSlot
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
}

public interface IPrShiftGroupPlanningService
{
    Task<PlanningServiceResult<ShiftGroupDefinition>> GetDefaultGroupAsync(CancellationToken ct = default);
    Task<PlanningServiceResult<ShiftGroupDefinition>> GetGroupByCodeAsync(string shfGrpCd, CancellationToken ct = default);

    /// <param name="shiftStartDate">Calendar date the shift begins. Post-midnight WorkSlots use date+1.</param>
    /// <param name="shfGrpCd">Null/empty/whitespace resolves the deterministic default group.</param>
    Task<PlanningServiceResult<ShiftGroupInfo>> GetShiftGroupInfoAsync(
        DateOnly shiftStartDate,
        string? shfGrpCd,
        CancellationToken ct = default);
}

public sealed class PrShiftGroupPlanningService : IPrShiftGroupPlanningService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrShiftGroupPlanningService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<PlanningServiceResult<ShiftGroupDefinition>> GetDefaultGroupAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningShiftGroup, ct)
            && !await _access.CanAccessAsync(MenuCodes.PlanningMacShiftCal, ct)
            && !await _access.CanAccessAsync(MenuCodes.PlanningCompanyCal, ct))
        {
            return PlanningServiceResult<ShiftGroupDefinition>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        }

        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<ShiftGroupDefinition>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrShiftGroups.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode)
            .ToListAsync(ct);

        var resolve = ResolveDefaultGroupCode(rows);
        if (!resolve.Succeeded)
            return PlanningServiceResult<ShiftGroupDefinition>.Fail(resolve.ErrorCode, resolve.Message ?? "Default shift group not found.");

        return await BuildDefinitionAsync(db, scope.CompanyCode, resolve.Value!, ct);
    }

    public async Task<PlanningServiceResult<ShiftGroupDefinition>> GetGroupByCodeAsync(string shfGrpCd, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningShiftGroup, ct)
            && !await _access.CanAccessAsync(MenuCodes.PlanningMacShiftCal, ct)
            && !await _access.CanAccessAsync(MenuCodes.PlanningCompanyCal, ct))
        {
            return PlanningServiceResult<ShiftGroupDefinition>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        }

        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<ShiftGroupDefinition>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        var code = PlanningCodeNormalizer.NormalizeCode(shfGrpCd);
        if (string.IsNullOrWhiteSpace(code))
            return PlanningServiceResult<ShiftGroupDefinition>.Fail(PlanningErrorCode.ValidationFailed, "Shift group code is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await BuildDefinitionAsync(db, scope.CompanyCode, code, ct);
    }

    public async Task<PlanningServiceResult<ShiftGroupInfo>> GetShiftGroupInfoAsync(
        DateOnly shiftStartDate,
        string? shfGrpCd,
        CancellationToken ct = default)
    {
        PlanningServiceResult<ShiftGroupDefinition> defResult;
        if (string.IsNullOrWhiteSpace(shfGrpCd))
            defResult = await GetDefaultGroupAsync(ct);
        else
            defResult = await GetGroupByCodeAsync(shfGrpCd, ct);

        if (!defResult.Succeeded || defResult.Value is null)
            return PlanningServiceResult<ShiftGroupInfo>.Fail(defResult.ErrorCode, defResult.Message ?? "Shift group not found.");

        return PlanningServiceResult<ShiftGroupInfo>.Ok(BuildInfo(defResult.Value, shiftStartDate));
    }

    /// <summary>
    /// Deterministic default-group resolution from denormalized PrShiftGroup rows.
    /// </summary>
    public static PlanningServiceResult<string> ResolveDefaultGroupCode(IReadOnlyList<PrShiftGroup> rows)
    {
        var groups = rows
            .GroupBy(x => x.ShfGrpCd, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Code = g.Key,
                IsDefault = g.Any(x => x.DefaultGrp == true)
            })
            .ToList();

        if (groups.Count == 0)
            return PlanningServiceResult<string>.Fail(PlanningErrorCode.ValidationFailed, "No shift groups are configured.");

        if (groups.Count == 1)
            return PlanningServiceResult<string>.Ok(groups[0].Code);

        var defaults = groups.Where(g => g.IsDefault).ToList();
        if (defaults.Count == 0)
            return PlanningServiceResult<string>.Fail(PlanningErrorCode.ValidationFailed, "No default shift group is configured.");

        if (defaults.Count > 1)
            return PlanningServiceResult<string>.Fail(PlanningErrorCode.ValidationFailed, "Multiple default shift groups found. Data integrity error.");

        return PlanningServiceResult<string>.Ok(defaults[0].Code);
    }

    public static ShiftGroupInfo BuildInfo(ShiftGroupDefinition definition, DateOnly shiftStartDate)
    {
        var dayShifts = new List<ShiftDayInfo>();
        foreach (var s in definition.Shifts.OrderBy(x => x.Start))
        {
            var startDt = shiftStartDate.ToDateTime(s.Start);
            var endDt = s.End < s.Start
                ? shiftStartDate.AddDays(1).ToDateTime(s.End)
                : shiftStartDate.ToDateTime(s.End);

            var breakSegments = ShiftTimeCalculator.ExpandBreakSegments(s.Start, s.End, s.Breaks);
            var workSlots = BuildWorkSlots(shiftStartDate, s.Start, s.End, breakSegments);

            dayShifts.Add(new ShiftDayInfo
            {
                ShiftCd = s.ShiftCd,
                StartDateTime = startDt,
                EndDateTime = endDt,
                GrossMinutes = s.GrossMinutes,
                BreakMinutes = s.BreakMinutes,
                NetMinutes = s.NetMinutes,
                WorkSlots = workSlots
            });
        }

        var count = dayShifts.Count;
        return new ShiftGroupInfo
        {
            Date = shiftStartDate,
            ShiftGroupCode = definition.ShiftGroupCode,
            ShiftCount = count,
            TotalWorkingMinPerDay = dayShifts.Sum(x => x.NetMinutes),
            TotalBreakMinPerDay = dayShifts.Sum(x => x.BreakMinutes),
            // NOT capacity — documented legacy formula only
            NominalDaySegmentMinutes = count > 0 ? 1440 / count : null,
            Shifts = dayShifts
        };
    }

    private static IReadOnlyList<WorkSlot> BuildWorkSlots(
        DateOnly shiftStartDate,
        TimeOnly shiftStart,
        TimeOnly shiftEnd,
        IReadOnlyList<(int Start, int EndExclusive)> breakSegments)
    {
        var (shiftS, shiftE) = ShiftTimeCalculator.NormalizeShiftSpan(shiftStart, shiftEnd);
        var cursor = shiftS;
        var slots = new List<WorkSlot>();

        foreach (var br in breakSegments)
        {
            if (cursor < br.Start)
                slots.Add(ToSlot(shiftStartDate, cursor, br.Start));
            cursor = br.EndExclusive;
        }

        if (cursor < shiftE)
            slots.Add(ToSlot(shiftStartDate, cursor, shiftE));

        return slots;
    }

    private static WorkSlot ToSlot(DateOnly shiftStartDate, int startMin, int endMin)
    {
        return new WorkSlot
        {
            Start = MinutesToDateTime(shiftStartDate, startMin),
            End = MinutesToDateTime(shiftStartDate, endMin)
        };
    }

    private static DateTime MinutesToDateTime(DateOnly shiftStartDate, int minutesFromStartDay)
    {
        var days = minutesFromStartDay / 1440;
        var rem = minutesFromStartDay % 1440;
        var d = shiftStartDate.AddDays(days);
        return d.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(rem)));
    }

    private async Task<PlanningServiceResult<ShiftGroupDefinition>> BuildDefinitionAsync(
        AppDbContext db,
        string companyCode,
        string shfGrpCd,
        CancellationToken ct)
    {
        var members = await db.PrShiftGroups.AsNoTracking()
            .Where(x => x.ShfGrpCd == shfGrpCd && x.CompCode == companyCode)
            .ToListAsync(ct);
        if (members.Count == 0)
            return PlanningServiceResult<ShiftGroupDefinition>.Fail(PlanningErrorCode.ValidationFailed, $"Shift group {shfGrpCd} not found.");

        var shiftCds = members.Select(x => x.ShiftCd).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shifts = await db.PrShifts.AsNoTracking()
            .Where(x => shiftCds.Contains(x.ShiftCd) && x.CompCode == companyCode)
            .ToListAsync(ct);

        var defs = new List<ShiftDefinition>();
        foreach (var e in shifts.OrderBy(x => x.StartTm))
        {
            var start = ShiftTimeCalculator.FromLegacyDateTime(e.StartTm) ?? default;
            var end = ShiftTimeCalculator.FromLegacyDateTime(e.EndTm) ?? default;
            var breaks = new[]
            {
                ShiftTimeCalculator.FromLegacyPair(e.BreakTm1From, e.BreakTm1To, true),
                ShiftTimeCalculator.FromLegacyPair(e.BreakTm2From, e.BreakTm2To, true),
                ShiftTimeCalculator.FromLegacyPair(e.BreakTm3From, e.BreakTm3To, true),
                ShiftTimeCalculator.FromLegacyPair(e.BreakTm4From, e.BreakTm4To, true),
                ShiftTimeCalculator.FromLegacyPair(e.BreakTm5From, e.BreakTm5To, true)
            };
            int gross, net;
            try
            {
                gross = ShiftTimeCalculator.ComputeGrossMinutes(start, end);
                net = ShiftTimeCalculator.ComputeNetMinutes(start, end, breaks);
            }
            catch (ArgumentException ex)
            {
                return PlanningServiceResult<ShiftGroupDefinition>.Fail(PlanningErrorCode.ValidationFailed, ex.Message);
            }

            defs.Add(new ShiftDefinition
            {
                ShiftCd = e.ShiftCd,
                ShiftDes = e.ShiftDes,
                Start = start,
                End = end,
                GrossMinutes = gross,
                BreakMinutes = gross - net,
                NetMinutes = net,
                Breaks = breaks
            });
        }

        var first = members[0];
        return PlanningServiceResult<ShiftGroupDefinition>.Ok(new ShiftGroupDefinition
        {
            ShiftGroupCode = first.ShfGrpCd,
            Description = first.ShfGrpDes,
            IsDefault = members.Any(x => x.DefaultGrp == true),
            Shifts = defs
        });
    }
}
