using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public sealed class PrMachineEditVm
{
    public string ClientNodeId { get; set; } = string.Empty;
    public string MachineCd { get; set; } = string.Empty;
    public string ProcessCd { get; set; } = string.Empty;
    public string? MachineDes { get; set; }
    public double? ConversionTime { get; set; }
    public double? StartupTime { get; set; }
    public double? QueueTime { get; set; }
    public DateTime? OriginalUpdated { get; set; }
    public bool IsNew { get; set; }
    public bool IsDeleted { get; set; }
}

public interface IPrMachineService
{
    Task<PlanningServiceResult<IReadOnlyList<PrMachine>>> ListAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrMachineEditVm> rows, CancellationToken ct = default);
}

public sealed class PrMachineService : IPrMachineService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly IPlanningDependencyChecker _deps;
    private readonly ILogger<PrMachineService> _logger;

    public PrMachineService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access,
        IPlanningDependencyChecker deps,
        ILogger<PrMachineService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _deps = deps;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrMachine>>> ListAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningWorkMachine, ct))
            return PlanningServiceResult<IReadOnlyList<PrMachine>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<IReadOnlyList<PrMachine>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode)
            .OrderBy(x => x.ProcessCd).ThenBy(x => x.MachineCd)
            .ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrMachine>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrMachineEditVm> rows, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        if (rows.Any(r => r.IsNew && !r.IsDeleted) && !await _access.CanAddAsync(MenuCodes.PlanningWorkMachine, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkMachine);
        if (rows.Any(r => !r.IsNew && !r.IsDeleted) && !await _access.CanEditAsync(MenuCodes.PlanningWorkMachine, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkMachine);
        if (rows.Any(r => r.IsDeleted) && !await _access.CanDeleteAsync(MenuCodes.PlanningWorkMachine, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWorkMachine);

        foreach (var row in rows.Where(r => !r.IsDeleted))
        {
            var mc = PlanningCodeNormalizer.NormalizeCode(row.MachineCd);
            var err = PlanningInputValidation.ValidateCode(mc);
            if (err is not null)
                return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, err,
                    [new ValidationIssue { ClientNodeId = row.ClientNodeId, FieldName = "MachineCd", BusinessKey = mc, Message = err }]);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in rows.Where(r => r.IsDeleted))
            {
                var mc = PlanningCodeNormalizer.NormalizeCode(row.MachineCd);
                var pc = PlanningCodeNormalizer.NormalizeCode(row.ProcessCd);
                var blockers = await _deps.CheckMachineDeleteAsync(mc, pc, write, ct);
                if (blockers.Count > 0)
                    return PlanningServiceResult.Fail(PlanningErrorCode.DependencyExists, $"Cannot delete machine {mc}.", dependencies: blockers);

                var entity = await db.PrMachines.FirstOrDefaultAsync(
                    x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct);
                if (entity is null) continue;
                if (row.OriginalUpdated is not null && entity.Updated != row.OriginalUpdated)
                    return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Machine {mc} changed since load.");

                var seqs = await db.PrMacSeqs
                    .Where(x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode)
                    .ToListAsync(ct);
                db.PrMacSeqs.RemoveRange(seqs);
                db.PrMachines.Remove(entity);
            }

            foreach (var row in rows.Where(r => !r.IsDeleted))
            {
                var mc = PlanningCodeNormalizer.NormalizeCode(row.MachineCd);
                var pc = PlanningCodeNormalizer.NormalizeCode(row.ProcessCd);

                if (!await db.PrProcesses.AnyAsync(x => x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct))
                    return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Process {pc} not found.");

                if (row.IsNew)
                {
                    if (await db.PrMachines.AnyAsync(x => x.MachineCd == mc && x.CompCode == write.CompanyCode, ct))
                        return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode,
                            "Each machine can only be assigned one process code.",
                            [new ValidationIssue { ClientNodeId = row.ClientNodeId, FieldName = "MachineCd", BusinessKey = mc, Code = PlanningErrorCode.DuplicateCode, Message = "Machine already exists." }]);

                    if (await db.PrMachines.AnyAsync(x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct))
                        return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, "Duplicate machine/process pair.");

                    db.PrMachines.Add(new PrMachine
                    {
                        MachineCd = mc,
                        ProcessCd = pc,
                        MachineDes = PlanningCodeNormalizer.NormalizeDescription(row.MachineDes),
                        ConversionTime = row.ConversionTime ?? 0,
                        StartupTime = row.StartupTime ?? 0,
                        QueueTime = row.QueueTime ?? 0,
                        Created = DateTime.Now,
                        UserId = write.UserId,
                        CompCode = write.CompanyCode,
                        BranchCode = write.BranchCode,
                        LocCode = write.LocationCode
                    });
                }
                else
                {
                    // ProcessCd is immutable on update — use hierarchy Relocated for process change
                    var entity = await db.PrMachines.FirstOrDefaultAsync(
                        x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct);
                    if (entity is null)
                        return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Machine {mc} not found.");
                    if (row.OriginalUpdated is not null && entity.Updated != row.OriginalUpdated)
                        return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Machine {mc} changed since load.");

                    entity.MachineDes = PlanningCodeNormalizer.NormalizeDescription(row.MachineDes);
                    if (row.ConversionTime is not null) entity.ConversionTime = row.ConversionTime;
                    if (row.StartupTime is not null) entity.StartupTime = row.StartupTime;
                    if (row.QueueTime is not null) entity.QueueTime = row.QueueTime;
                    entity.Updated = DateTime.Now;
                    entity.UpdatedUid = write.UserId;
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("Machine batch saved for company {Company}", write.CompanyCode);
            return PlanningServiceResult.Ok("Item(s) saved!");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
                                           || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            await tx.RollbackAsync(ct);
            return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, "Duplicate machine code.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
