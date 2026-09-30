using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

public interface IPrOperatorService
{
    Task<PlanningServiceResult<IReadOnlyList<PrOperator>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrOperator> rows, IReadOnlyList<string> deletedCodes, CancellationToken ct = default);
}

public interface IPrWorkPrefixService
{
    Task<PlanningServiceResult<IReadOnlyList<PrWorkPefix>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrWorkPefix> rows, IReadOnlyList<string> deletedPrefixes, CancellationToken ct = default);
}

public interface IPrMacSeqService
{
    Task<PlanningServiceResult<IReadOnlyList<PrMacSeq>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrMacSeq> rows, IReadOnlyList<(string MachineCd, string ProcessCd)> deleted, CancellationToken ct = default);
}

public interface IPrPreventiveService
{
    Task<PlanningServiceResult<IReadOnlyList<PrPreventive>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrPreventive> rows, IReadOnlyList<int> deletedUids, CancellationToken ct = default);
    Task<PlanningServiceResult> AddRangeAsync(string machineCd, DateTime fromDate, DateTime toDate, TimeOnly start, TimeOnly end, string? reasonCd, string? remark, CancellationToken ct = default);
}

public sealed class PrOperatorService : IPrOperatorService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrOperatorService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory; _tenant = tenant; _access = access;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrOperator>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningOperator, ct))
            return PlanningServiceResult<IReadOnlyList<PrOperator>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<IReadOnlyList<PrOperator>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrOperators.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode).OrderBy(x => x.Code).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrOperator>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrOperator> rows, IReadOnlyList<string> deletedCodes, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanEditAsync(MenuCodes.PlanningOperator, ct) && !await _access.CanAddAsync(MenuCodes.PlanningOperator, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningOperator);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var code in deletedCodes.Select(PlanningCodeNormalizer.NormalizeCode))
        {
            var e = await db.PrOperators.FirstOrDefaultAsync(x => x.Code == code && x.CompanyCode == write.CompanyCode, ct);
            if (e is not null) db.PrOperators.Remove(e);
        }
        foreach (var row in rows)
        {
            var code = PlanningCodeNormalizer.NormalizeCode(row.Code);
            var existing = await db.PrOperators.FirstOrDefaultAsync(x => x.Code == code && x.CompanyCode == write.CompanyCode, ct);
            if (existing is null)
            {
                db.PrOperators.Add(new PrOperator
                {
                    Code = code,
                    Name = PlanningCodeNormalizer.NormalizeDescription(row.Name) ?? code,
                    Active = row.Active ?? true,
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompanyCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocationCode = write.LocationCode
                });
            }
            else
            {
                existing.Name = PlanningCodeNormalizer.NormalizeDescription(row.Name) ?? existing.Name;
                existing.Active = row.Active ?? existing.Active;
                existing.Updated = DateTime.Now;
                existing.UpdatedUid = write.UserId;
            }
        }
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Operators saved.");
    }
}

public sealed class PrWorkPrefixService : IPrWorkPrefixService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrWorkPrefixService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory; _tenant = tenant; _access = access;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrWorkPefix>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningWorkPrefix, ct))
            return PlanningServiceResult<IReadOnlyList<PrWorkPefix>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<IReadOnlyList<PrWorkPefix>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrWorkPefixes.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode).OrderBy(x => x.Prefix).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrWorkPefix>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrWorkPefix> rows, IReadOnlyList<string> deletedPrefixes, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanEditAsync(MenuCodes.PlanningWorkPrefix, ct) && !await _access.CanAddAsync(MenuCodes.PlanningWorkPrefix, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkPrefix);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var p in deletedPrefixes.Select(PlanningCodeNormalizer.NormalizeCode))
        {
            if (!await _access.CanDeleteAsync(MenuCodes.PlanningWorkPrefix, ct))
                return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkPrefix);
            var e = await db.PrWorkPefixes.FirstOrDefaultAsync(x => x.Prefix == p && x.CompCode == write.CompanyCode, ct);
            if (e is not null) db.PrWorkPefixes.Remove(e);
        }
        foreach (var row in rows)
        {
            var prefix = PlanningCodeNormalizer.NormalizeCode(row.Prefix);
            var existing = await db.PrWorkPefixes.FirstOrDefaultAsync(x => x.Prefix == prefix && x.CompCode == write.CompanyCode, ct);
            if (existing is null)
            {
                if (!await _access.CanAddAsync(MenuCodes.PlanningWorkPrefix, ct))
                    return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkPrefix);
                db.PrWorkPefixes.Add(new PrWorkPefix
                {
                    Prefix = prefix,
                    Description = PlanningCodeNormalizer.NormalizeDescription(row.Description),
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }
            else
            {
                // Correct table update (fixes legacy adapter bug targeting PrWorkCentre)
                existing.Description = PlanningCodeNormalizer.NormalizeDescription(row.Description);
                existing.Updated = DateTime.Now;
                existing.UpdatedUid = write.UserId;
            }
        }
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Prefixes saved.");
    }
}

public sealed class PrMacSeqService : IPrMacSeqService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrMacSeqService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory; _tenant = tenant; _access = access;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrMacSeq>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningMacSeq, ct))
            return PlanningServiceResult<IReadOnlyList<PrMacSeq>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null) return PlanningServiceResult<IReadOnlyList<PrMacSeq>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrMacSeqs.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode).OrderBy(x => x.ProcessCd).ThenBy(x => x.SeqNo).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrMacSeq>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrMacSeq> rows, IReadOnlyList<(string MachineCd, string ProcessCd)> deleted, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanEditAsync(MenuCodes.PlanningMacSeq, ct) && !await _access.CanAddAsync(MenuCodes.PlanningMacSeq, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMacSeq);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var (m, p) in deleted)
        {
            if (!await _access.CanDeleteAsync(MenuCodes.PlanningMacSeq, ct))
                return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMacSeq);
            var mc = PlanningCodeNormalizer.NormalizeCode(m);
            var pc = PlanningCodeNormalizer.NormalizeCode(p);
            var e = await db.PrMacSeqs.FirstOrDefaultAsync(x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct);
            if (e is not null) db.PrMacSeqs.Remove(e);
        }

        var seqByProcess = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var mc = PlanningCodeNormalizer.NormalizeCode(row.MachineCd);
            var pc = PlanningCodeNormalizer.NormalizeCode(row.ProcessCd);
            if (mc.Length == 0 || pc.Length == 0)
                return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Machine and Process are required.");

            var machineOk = await db.PrMachines.AsNoTracking()
                .AnyAsync(x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct);
            if (!machineOk)
                return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Machine {mc}/{pc} not found in tenant masters.");

            if (!seqByProcess.TryGetValue(pc, out var seqSet))
            {
                seqSet = new HashSet<int>();
                seqByProcess[pc] = seqSet;
            }
            if (!seqSet.Add(row.SeqNo ?? 0))
                return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Duplicate SeqNo {row.SeqNo} for process {pc}.");

            var existing = await db.PrMacSeqs.FirstOrDefaultAsync(x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct);
            if (existing is null)
            {
                if (!await _access.CanAddAsync(MenuCodes.PlanningMacSeq, ct))
                    return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMacSeq);
                db.PrMacSeqs.Add(new PrMacSeq
                {
                    MachineCd = mc,
                    ProcessCd = pc,
                    SeqNo = row.SeqNo,
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }
            else
            {
                existing.SeqNo = row.SeqNo;
                existing.Updated = DateTime.Now;
                existing.UpdatedUid = write.UserId;
            }
        }
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Machine sequences saved.");
    }
}

public sealed class PrPreventiveService : IPrPreventiveService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private const int MaxRangeDays = 366;

    public PrPreventiveService(IDbContextFactory<AppDbContext> dbFactory, IInventoryTenantContext tenant, IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory; _tenant = tenant; _access = access;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrPreventive>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningMacPreventive, ct))
            return PlanningServiceResult<IReadOnlyList<PrPreventive>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrPreventives.AsNoTracking().OrderByDescending(x => x.DownDt).Take(2000).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrPreventive>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrPreventive> rows, IReadOnlyList<int> deletedUids, CancellationToken ct = default)
    {
        if (!await _access.CanEditAsync(MenuCodes.PlanningMacPreventive, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMacPreventive);
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var uid in deletedUids)
        {
            var e = await db.PrPreventives.FirstOrDefaultAsync(x => x.Uid == uid, ct);
            if (e is not null) db.PrPreventives.Remove(e);
        }
        foreach (var row in rows)
        {
            var mac = PlanningCodeNormalizer.NormalizeCode(row.MachineCd);
            var day = row.DownDt.Date;
            if (row.Uid == 0)
            {
                if (await db.PrPreventives.AnyAsync(x => x.MachineCd == mac && x.DownDt == day, ct))
                    return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Preventive already exists for {mac} on {day:yyyy-MM-dd}.");
                db.PrPreventives.Add(new PrPreventive
                {
                    MachineCd = mac,
                    DownDt = day,
                    StartTm = row.StartTm,
                    EndTm = row.EndTm,
                    ReasonCd = row.ReasonCd,
                    Remark = row.Remark,
                    Created = DateTime.Now,
                    UserId = write.UserId
                });
            }
            else
            {
                var e = await db.PrPreventives.FirstOrDefaultAsync(x => x.Uid == row.Uid, ct);
                if (e is null) continue;
                var clash = await db.PrPreventives.AnyAsync(x => x.Uid != row.Uid && x.MachineCd == mac && x.DownDt == day, ct);
                if (clash)
                    return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Preventive already exists for {mac} on {day:yyyy-MM-dd}.");
                e.MachineCd = mac;
                e.DownDt = day;
                e.StartTm = row.StartTm;
                e.EndTm = row.EndTm;
                e.ReasonCd = row.ReasonCd;
                e.Remark = row.Remark;
                e.Updated = DateTime.Now;
            }
        }
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Preventive rows saved.");
    }

    public async Task<PlanningServiceResult> AddRangeAsync(string machineCd, DateTime fromDate, DateTime toDate, TimeOnly start, TimeOnly end, string? reasonCd, string? remark, CancellationToken ct = default)
    {
        if (!await _access.CanAddAsync(MenuCodes.PlanningMacPreventive, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMacPreventive);
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        var mac = PlanningCodeNormalizer.NormalizeCode(machineCd);
        var from = fromDate.Date;
        var to = toDate.Date;
        if (to < from) return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "End date before start date.");
        if ((to - from).TotalDays + 1 > MaxRangeDays)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Range cannot exceed {MaxRangeDays} days.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (await db.PrPreventives.AnyAsync(x => x.MachineCd == mac && x.DownDt == d, ct))
                return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Preventive already exists for {mac} on {d:yyyy-MM-dd}.");
            db.PrPreventives.Add(new PrPreventive
            {
                MachineCd = mac,
                DownDt = d,
                StartTm = d.Add(start.ToTimeSpan()),
                EndTm = d.Add(end.ToTimeSpan()),
                ReasonCd = reasonCd,
                Remark = remark,
                Created = DateTime.Now,
                UserId = write.UserId
            });
        }
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Preventive range added.");
    }
}
