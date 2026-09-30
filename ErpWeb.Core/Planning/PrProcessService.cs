using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public sealed class PrProcessEditVm
{
    public string ClientNodeId { get; set; } = string.Empty;
    public string ProcessCd { get; set; } = string.Empty;
    public string WorkCentre { get; set; } = string.Empty;
    public string? ProcessDes { get; set; }
    public int? Sequence { get; set; }
    public bool? Stock { get; set; }
    public DateTime? OriginalUpdated { get; set; }
    public bool IsNew { get; set; }
    public bool IsDeleted { get; set; }
}

public interface IPrProcessService
{
    Task<PlanningServiceResult<IReadOnlyList<PrProcess>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrProcessEditVm> rows, CancellationToken ct = default);
}

public sealed class PrProcessService : IPrProcessService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly IPlanningDependencyChecker _deps;
    private readonly ILogger<PrProcessService> _logger;

    public PrProcessService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access,
        IPlanningDependencyChecker deps,
        ILogger<PrProcessService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _deps = deps;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrProcess>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningWorkProcess, ct))
            return PlanningServiceResult<IReadOnlyList<PrProcess>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<IReadOnlyList<PrProcess>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrProcesses.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode)
            .OrderBy(x => x.WorkCentre).ThenBy(x => x.Sequence).ThenBy(x => x.ProcessCd)
            .ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrProcess>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrProcessEditVm> rows, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        if (rows.Any(r => r.IsNew && !r.IsDeleted) && !await _access.CanAddAsync(MenuCodes.PlanningWorkProcess, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkProcess);
        if (rows.Any(r => !r.IsNew && !r.IsDeleted) && !await _access.CanEditAsync(MenuCodes.PlanningWorkProcess, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkProcess);
        if (rows.Any(r => r.IsDeleted) && !await _access.CanDeleteAsync(MenuCodes.PlanningWorkProcess, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkProcess);

        var issues = new List<ValidationIssue>();
        foreach (var row in rows.Where(r => !r.IsDeleted))
        {
            var pc = PlanningCodeNormalizer.NormalizeCode(row.ProcessCd);
            var wc = PlanningCodeNormalizer.NormalizeCode(row.WorkCentre);
            var e1 = PlanningInputValidation.ValidateCode(pc);
            var e2 = PlanningInputValidation.ValidateCode(wc);
            if (e1 is not null)
                issues.Add(new ValidationIssue { ClientNodeId = row.ClientNodeId, FieldName = "ProcessCd", BusinessKey = pc, Message = e1 });
            if (e2 is not null)
                issues.Add(new ValidationIssue { ClientNodeId = row.ClientNodeId, FieldName = "WorkCentre", BusinessKey = wc, Message = e2 });
        }
        if (issues.Count > 0)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Validation failed.", issues);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in rows.Where(r => r.IsDeleted))
            {
                var pc = PlanningCodeNormalizer.NormalizeCode(row.ProcessCd);
                var wc = PlanningCodeNormalizer.NormalizeCode(row.WorkCentre);
                var blockers = await _deps.CheckProcessDeleteAsync(pc, wc, write, ct);
                if (blockers.Count > 0)
                    return PlanningServiceResult.Fail(PlanningErrorCode.DependencyExists, $"Cannot delete process {pc}.", dependencies: blockers);

                var entity = await db.PrProcesses.FirstOrDefaultAsync(
                    x => x.ProcessCd == pc && x.WorkCentre == wc && x.CompCode == write.CompanyCode, ct);
                if (entity is null) continue;
                if (row.OriginalUpdated is not null && entity.Updated != row.OriginalUpdated)
                    return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Process {pc} changed since load.");

                var macs = await db.PrMachines
                    .Where(x => x.ProcessCd == pc && x.CompCode == write.CompanyCode).ToListAsync(ct);
                foreach (var m in macs)
                {
                    var seqs = await db.PrMacSeqs
                        .Where(x => x.MachineCd == m.MachineCd && x.ProcessCd == m.ProcessCd && x.CompCode == write.CompanyCode)
                        .ToListAsync(ct);
                    db.PrMacSeqs.RemoveRange(seqs);
                    db.PrMachines.Remove(m);
                }
                db.PrProcesses.Remove(entity);
            }

            foreach (var row in rows.Where(r => !r.IsDeleted))
            {
                var pc = PlanningCodeNormalizer.NormalizeCode(row.ProcessCd);
                var wc = PlanningCodeNormalizer.NormalizeCode(row.WorkCentre);
                if (!await db.PrWorkCentres.AnyAsync(x => x.WrkCtrCd == wc && x.CompCode == write.CompanyCode, ct))
                    return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Work centre {wc} not found.",
                        [new ValidationIssue { ClientNodeId = row.ClientNodeId, FieldName = "WorkCentre", BusinessKey = wc, Message = "Work centre not found." }]);

                if (row.IsNew)
                {
                    if (await db.PrProcesses.AnyAsync(x => x.ProcessCd == pc && x.WorkCentre == wc && x.CompCode == write.CompanyCode, ct))
                        return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Duplicate process {pc} under {wc}.",
                            [new ValidationIssue { ClientNodeId = row.ClientNodeId, FieldName = "ProcessCd", BusinessKey = pc, Code = PlanningErrorCode.DuplicateCode, Message = "Duplicate process." }]);

                    db.PrProcesses.Add(new PrProcess
                    {
                        ProcessCd = pc,
                        WorkCentre = wc,
                        ProcessDes = PlanningCodeNormalizer.NormalizeDescription(row.ProcessDes),
                        Sequence = row.Sequence,
                        Stock = row.Stock ?? false,
                        Created = DateTime.Now,
                        UserId = write.UserId,
                        CompCode = write.CompanyCode,
                        BranchCode = write.BranchCode,
                        LocCode = write.LocationCode
                    });
                }
                else
                {
                    var entity = await db.PrProcesses.FirstOrDefaultAsync(
                        x => x.ProcessCd == pc && x.WorkCentre == wc && x.CompCode == write.CompanyCode, ct);
                    if (entity is null)
                        return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Process {pc} not found.");
                    if (row.OriginalUpdated is not null && entity.Updated != row.OriginalUpdated)
                        return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Process {pc} changed since load.");
                    entity.ProcessDes = PlanningCodeNormalizer.NormalizeDescription(row.ProcessDes);
                    entity.Sequence = row.Sequence;
                    if (row.Stock is not null) entity.Stock = row.Stock;
                    entity.Updated = DateTime.Now;
                    entity.UpdatedUid = write.UserId;
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("Process batch saved for company {Company}", write.CompanyCode);
            return PlanningServiceResult.Ok("Item(s) saved!");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
                                           || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, "Duplicate process key.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
