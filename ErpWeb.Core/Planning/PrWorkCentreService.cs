using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public sealed class PrWorkCentreEditVm
{
    public string ClientNodeId { get; set; } = string.Empty;
    public string WrkCtrCd { get; set; } = string.Empty;
    public string? WrkCtrDes { get; set; }
    public string? Class { get; set; }
    public DateTime? OriginalUpdated { get; set; }
    public bool IsNew { get; set; }
    public bool IsDeleted { get; set; }
}

public interface IPrWorkCentreService
{
    Task<PlanningServiceResult<IReadOnlyList<PrWorkCentre>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrWorkCentreEditVm> rows, CancellationToken ct = default);
}

public sealed class PrWorkCentreService : IPrWorkCentreService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly IPlanningDependencyChecker _deps;
    private readonly ILogger<PrWorkCentreService> _logger;

    public PrWorkCentreService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access,
        IPlanningDependencyChecker deps,
        ILogger<PrWorkCentreService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _deps = deps;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrWorkCentre>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningWorkCentre, ct))
            return PlanningServiceResult<IReadOnlyList<PrWorkCentre>>.Fail(
                PlanningErrorCode.PermissionDenied, $"Permission denied for {MenuCodes.PlanningWorkCentre}.");

        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<IReadOnlyList<PrWorkCentre>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrWorkCentres.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode)
            .OrderBy(x => x.WrkCtrCd)
            .ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrWorkCentre>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrWorkCentreEditVm> rows, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null)
            return PlanningServiceResult.TenantRequired();

        var needsAdd = rows.Any(r => r.IsNew && !r.IsDeleted);
        var needsEdit = rows.Any(r => !r.IsNew && !r.IsDeleted);
        var needsDel = rows.Any(r => r.IsDeleted);
        if (needsAdd && !await _access.CanAddAsync(MenuCodes.PlanningWorkCentre, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkCentre);
        if (needsEdit && !await _access.CanEditAsync(MenuCodes.PlanningWorkCentre, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkCentre);
        if (needsDel && !await _access.CanDeleteAsync(MenuCodes.PlanningWorkCentre, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkCentre);

        var issues = new List<ValidationIssue>();
        foreach (var row in rows.Where(r => !r.IsDeleted))
        {
            var code = PlanningCodeNormalizer.NormalizeCode(row.WrkCtrCd);
            var err = PlanningInputValidation.ValidateCode(code);
            if (err is not null)
            {
                issues.Add(new ValidationIssue
                {
                    ClientNodeId = row.ClientNodeId,
                    FieldName = nameof(row.WrkCtrCd),
                    BusinessKey = code,
                    Message = err
                });
            }
        }
        if (issues.Count > 0)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Validation failed.", issues);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in rows.Where(r => r.IsDeleted))
            {
                var code = PlanningCodeNormalizer.NormalizeCode(row.WrkCtrCd);
                var blockers = await _deps.CheckWorkCentreDeleteAsync(code, write, ct);
                if (blockers.Count > 0)
                {
                    return PlanningServiceResult.Fail(
                        PlanningErrorCode.DependencyExists,
                        $"Cannot delete work centre {code}.",
                        dependencies: blockers);
                }

                var entity = await db.PrWorkCentres
                    .FirstOrDefaultAsync(x => x.WrkCtrCd == code && x.CompCode == write.CompanyCode, ct);
                if (entity is null)
                    continue;
                if (row.OriginalUpdated is not null && entity.Updated != row.OriginalUpdated)
                    return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Work centre {code} changed since load.");

                // Cascade owned children after machine/process deps cleared
                var processes = await db.PrProcesses
                    .Where(x => x.WorkCentre == code && x.CompCode == write.CompanyCode)
                    .ToListAsync(ct);
                foreach (var p in processes)
                {
                    var macs = await db.PrMachines
                        .Where(x => x.ProcessCd == p.ProcessCd && x.CompCode == write.CompanyCode)
                        .ToListAsync(ct);
                    foreach (var m in macs)
                    {
                        var seqs = await db.PrMacSeqs
                            .Where(x => x.MachineCd == m.MachineCd && x.ProcessCd == m.ProcessCd && x.CompCode == write.CompanyCode)
                            .ToListAsync(ct);
                        db.PrMacSeqs.RemoveRange(seqs);
                        db.PrMachines.Remove(m);
                    }
                    db.PrProcesses.Remove(p);
                }
                db.PrWorkCentres.Remove(entity);
            }

            foreach (var row in rows.Where(r => !r.IsDeleted))
            {
                var code = PlanningCodeNormalizer.NormalizeCode(row.WrkCtrCd);
                var des = PlanningCodeNormalizer.NormalizeDescription(row.WrkCtrDes);
                if (row.IsNew)
                {
                    if (await db.PrWorkCentres.AnyAsync(x => x.WrkCtrCd == code && x.CompCode == write.CompanyCode, ct))
                    {
                        return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Duplicate work centre {code}.",
                            [new ValidationIssue { ClientNodeId = row.ClientNodeId, FieldName = "WrkCtrCd", BusinessKey = code, Message = "Duplicate code.", Code = PlanningErrorCode.DuplicateCode }]);
                    }
                    db.PrWorkCentres.Add(new PrWorkCentre
                    {
                        WrkCtrCd = code,
                        WrkCtrDes = des,
                        Class = PlanningCodeNormalizer.NormalizeCode(row.Class ?? string.Empty) is { Length: > 0 } c ? c : row.Class,
                        Created = DateTime.Now,
                        UserId = write.UserId,
                        CompCode = write.CompanyCode,
                        BranchCode = write.BranchCode,
                        LocCode = write.LocationCode
                    });
                }
                else
                {
                    var entity = await db.PrWorkCentres
                        .FirstOrDefaultAsync(x => x.WrkCtrCd == code && x.CompCode == write.CompanyCode, ct);
                    if (entity is null)
                        return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Work centre {code} not found.");
                    if (row.OriginalUpdated is not null && entity.Updated != row.OriginalUpdated)
                        return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Work centre {code} changed since load.");
                    entity.WrkCtrDes = des;
                    entity.Class = string.IsNullOrWhiteSpace(row.Class) ? entity.Class : PlanningCodeNormalizer.NormalizeCode(row.Class);
                    entity.Updated = DateTime.Now;
                    entity.UpdatedUid = write.UserId;
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("WorkCentre batch saved for company {Company}", write.CompanyCode);
            return PlanningServiceResult.Ok("Item(s) saved!");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, "Duplicate work centre code.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;
}
