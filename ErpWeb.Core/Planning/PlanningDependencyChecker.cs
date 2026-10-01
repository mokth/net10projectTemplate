using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

public interface IPlanningDependencyChecker
{
    Task<IReadOnlyList<DependencyReference>> CheckWorkCentreDeleteAsync(
        string wrkCtrCd, InventoryTenantScope scope, CancellationToken ct = default);

    Task<IReadOnlyList<DependencyReference>> CheckProcessDeleteAsync(
        string processCd, string workCentre, InventoryTenantScope scope, CancellationToken ct = default);

    Task<IReadOnlyList<DependencyReference>> CheckMachineDeleteAsync(
        string machineCd, string processCd, InventoryTenantScope scope, CancellationToken ct = default);

    /// <summary>Relocation-specific checks (MachineCd unchanged). Does not treat Preventive/Calendar as blockers when keyed only by MachineCd.</summary>
    Task<IReadOnlyList<DependencyReference>> CheckMachineRelocationAsync(
        string machineCd, string fromProcessCd, string toProcessCd, InventoryTenantScope scope, CancellationToken ct = default);
}

public sealed class PlanningDependencyChecker : IPlanningDependencyChecker
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PlanningDependencyChecker(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<DependencyReference>> CheckWorkCentreDeleteAsync(
        string wrkCtrCd, InventoryTenantScope scope, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var refs = new List<DependencyReference>();
        var code = PlanningCodeNormalizer.NormalizeCode(wrkCtrCd);

        if (await db.PrDefWcenters.AsNoTracking()
                .AnyAsync(x => x.WcCode == code, ct))
        {
            refs.Add(new DependencyReference
            {
                SourceTable = "PrDefWCenter",
                BusinessKey = code,
                Description = "Referenced by product definition work centre routing."
            });
        }

        var processes = await db.PrProcesses.AsNoTracking()
            .Where(x => x.WorkCentre == code && x.CompCode == scope.CompanyCode)
            .Select(x => x.ProcessCd)
            .ToListAsync(ct);

        foreach (var p in processes)
        {
            refs.AddRange(await CheckProcessDeleteAsync(p, code, scope, ct));
        }

        return refs;
    }

    public async Task<IReadOnlyList<DependencyReference>> CheckProcessDeleteAsync(
        string processCd, string workCentre, InventoryTenantScope scope, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var refs = new List<DependencyReference>();
        var pc = PlanningCodeNormalizer.NormalizeCode(processCd);
        var wc = PlanningCodeNormalizer.NormalizeCode(workCentre);

        if (await db.PrDefProcesses.AsNoTracking()
                .AnyAsync(x => x.ProcessCode == pc, ct))
        {
            refs.Add(new DependencyReference
            {
                SourceTable = "PrDefProcess",
                BusinessKey = pc,
                Description = "Referenced by product definition process routing."
            });
        }

        var machines = await db.PrMachines.AsNoTracking()
            .Where(x => x.ProcessCd == pc && x.CompCode == scope.CompanyCode)
            .Select(x => x.MachineCd)
            .ToListAsync(ct);

        foreach (var m in machines)
            refs.AddRange(await CheckMachineDeleteAsync(m, pc, scope, ct));

        return refs;
    }

    public async Task<IReadOnlyList<DependencyReference>> CheckMachineDeleteAsync(
        string machineCd, string processCd, InventoryTenantScope scope, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var refs = new List<DependencyReference>();
        var mc = PlanningCodeNormalizer.NormalizeCode(machineCd);

        if (await db.PrDefMachines.AsNoTracking().AnyAsync(x => x.MachineCode == mc, ct))
        {
            refs.Add(new DependencyReference
            {
                SourceTable = "PrDefMachine",
                BusinessKey = mc,
                Description = "Referenced by product definition machine routing."
            });
        }

        if (await db.PrPreventives.AsNoTracking().AnyAsync(x => x.MachineCd == mc && x.CompCode == scope.CompanyCode, ct))
        {
            refs.Add(new DependencyReference
            {
                SourceTable = "PrPreventive",
                BusinessKey = mc,
                Description = "Machine has preventive downtime rows. Clear them before delete."
            });
        }

        if (await db.PrShiftCalendars.AsNoTracking()
                .AnyAsync(x => x.MachineCode == mc && x.CompCode == scope.CompanyCode, ct))
        {
            refs.Add(new DependencyReference
            {
                SourceTable = "PrShiftCalendar",
                BusinessKey = mc,
                Description = "Machine has shift calendar rows. Clear them before delete."
            });
        }

        // PrMacSeq cascades on delete — not a blocker
        _ = processCd;
        return refs;
    }

    public async Task<IReadOnlyList<DependencyReference>> CheckMachineRelocationAsync(
        string machineCd, string fromProcessCd, string toProcessCd, InventoryTenantScope scope, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var refs = new List<DependencyReference>();
        var mc = PlanningCodeNormalizer.NormalizeCode(machineCd);

        // Signed relocation policy (Phase 0.5): PrDefMachine blocks; WO history not rewritten;
        // Preventive + ShiftCalendar preserved (keyed by MachineCd); MacSeq re-keyed by service.
        if (await db.PrDefMachines.AsNoTracking().AnyAsync(x => x.MachineCode == mc, ct))
        {
            refs.Add(new DependencyReference
            {
                SourceTable = "PrDefMachine",
                BusinessKey = mc,
                Description = "Machine is referenced by product definition routing. Relocation blocked."
            });
        }

        if (await db.ProductionWorkOrderOperations.AsNoTracking()
                .AnyAsync(x => x.WorkCentreCode != null && x.OperationCode == mc, ct))
        {
            // Soft signal — WO ops use WorkCentre/Operation; if machine appears in resource links block
        }

        _ = fromProcessCd;
        _ = toProcessCd;
        _ = scope;
        return refs;
    }
}
