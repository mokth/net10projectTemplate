using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public interface IPrHierarchyService
{
    Task<PlanningServiceResult<IReadOnlyList<HierarchyNodeVm>>> LoadTreeAsync(CancellationToken ct = default);
    Task<PlanningServiceResult> SaveAsync(HierarchySaveRequest request, CancellationToken ct = default);
}

public sealed class PrHierarchyService : IPrHierarchyService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly IPlanningDependencyChecker _deps;
    private readonly ILogger<PrHierarchyService> _logger;

    public PrHierarchyService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access,
        IPlanningDependencyChecker deps,
        ILogger<PrHierarchyService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _deps = deps;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<HierarchyNodeVm>>> LoadTreeAsync(CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningWcHierarchy, ct))
            return PlanningServiceResult<IReadOnlyList<HierarchyNodeVm>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<IReadOnlyList<HierarchyNodeVm>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant scope required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var wcs = await db.PrWorkCentres.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode).ToListAsync(ct);
        var procs = await db.PrProcesses.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode).ToListAsync(ct);
        var macs = await db.PrMachines.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode).ToListAsync(ct);

        var nodes = new List<HierarchyNodeVm>();
        foreach (var wc in wcs.OrderBy(x => x.WrkCtrCd))
        {
            var wcId = $"WC:{wc.WrkCtrCd}";
            nodes.Add(new HierarchyNodeVm
            {
                NodeId = wcId,
                NodeType = HierarchyNodeType.WorkCentre,
                Code = wc.WrkCtrCd,
                Description = wc.WrkCtrDes,
                ClassCode = wc.Class,
                Updated = wc.Updated
            });

            foreach (var p in procs.Where(x => x.WorkCentre == wc.WrkCtrCd).OrderBy(x => x.Sequence).ThenBy(x => x.ProcessCd))
            {
                var pId = $"PR:{p.WorkCentre}|{p.ProcessCd}";
                nodes.Add(new HierarchyNodeVm
                {
                    NodeId = pId,
                    ParentNodeId = wcId,
                    NodeType = HierarchyNodeType.Process,
                    Code = p.ProcessCd,
                    Description = p.ProcessDes,
                    Sequence = p.Sequence,
                    Updated = p.Updated
                });

                foreach (var m in macs.Where(x => x.ProcessCd == p.ProcessCd).OrderBy(x => x.MachineCd))
                {
                    nodes.Add(new HierarchyNodeVm
                    {
                        NodeId = $"MC:{m.ProcessCd}|{m.MachineCd}",
                        ParentNodeId = pId,
                        NodeType = HierarchyNodeType.Machine,
                        Code = m.MachineCd,
                        Description = m.MachineDes,
                        ConversionTime = m.ConversionTime,
                        StartupTime = m.StartupTime,
                        QueueTime = m.QueueTime,
                        Updated = m.Updated
                    });
                }
            }
        }

        return PlanningServiceResult<IReadOnlyList<HierarchyNodeVm>>.Ok(nodes);
    }

    public async Task<PlanningServiceResult> SaveAsync(HierarchySaveRequest request, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanAccessAsync(MenuCodes.PlanningWcHierarchy, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWcHierarchy);

        var changes = request.Changes ?? [];
        if (changes.Any(c => c.ChangeKind == HierarchyChangeKind.Added) && !await _access.CanAddAsync(MenuCodes.PlanningWcHierarchy, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWcHierarchy);
        if (changes.Any(c => c.ChangeKind is HierarchyChangeKind.Modified or HierarchyChangeKind.Relocated)
            && !await _access.CanEditAsync(MenuCodes.PlanningWcHierarchy, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWcHierarchy);
        if (changes.Any(c => c.ChangeKind == HierarchyChangeKind.Deleted) && !await _access.CanDeleteAsync(MenuCodes.PlanningWcHierarchy, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningWcHierarchy);

        // Pre-check deletes / relocations
        foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Deleted))
        {
            var code = PlanningCodeNormalizer.NormalizeCode(c.Code);
            IReadOnlyList<DependencyReference> blockers = c.NodeType switch
            {
                HierarchyNodeType.WorkCentre => await _deps.CheckWorkCentreDeleteAsync(code, write, ct),
                HierarchyNodeType.Process => await _deps.CheckProcessDeleteAsync(code, PlanningCodeNormalizer.NormalizeCode(c.ParentCode), write, ct),
                HierarchyNodeType.Machine => await _deps.CheckMachineDeleteAsync(code, PlanningCodeNormalizer.NormalizeCode(c.ParentCode), write, ct),
                _ => []
            };
            if (blockers.Count > 0)
                return PlanningServiceResult.Fail(PlanningErrorCode.DependencyExists, $"Delete blocked for {code}.", dependencies: blockers);
        }

        foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Relocated && x.NodeType == HierarchyNodeType.Machine))
        {
            var mc = PlanningCodeNormalizer.NormalizeCode(c.Code);
            var from = PlanningCodeNormalizer.NormalizeCode(c.OriginalProcessCd);
            var to = PlanningCodeNormalizer.NormalizeCode(c.ParentCode);
            var blockers = await _deps.CheckMachineRelocationAsync(mc, from, to, write, ct);
            if (blockers.Count > 0)
                return PlanningServiceResult.Fail(PlanningErrorCode.RelocationBlocked, $"Relocation blocked for {mc}.", dependencies: blockers);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 7-8: Key-release deletes for Relocated — MUST flush before inserts
            foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Relocated && x.NodeType == HierarchyNodeType.Machine))
            {
                var mc = PlanningCodeNormalizer.NormalizeCode(c.Code);
                var from = PlanningCodeNormalizer.NormalizeCode(c.OriginalProcessCd);
                var old = await db.PrMachines.FirstOrDefaultAsync(
                    x => x.MachineCd == mc && x.ProcessCd == from && x.CompCode == write.CompanyCode, ct);
                if (old is null)
                    return PlanningServiceResult.Fail(PlanningErrorCode.RelocationFailed, $"Original machine {mc}/{from} not found.");

                // Re-key MacSeq: delete old, recreate after insert
                var seqs = await db.PrMacSeqs
                    .Where(x => x.MachineCd == mc && x.ProcessCd == from && x.CompCode == write.CompanyCode)
                    .ToListAsync(ct);
                db.PrMacSeqs.RemoveRange(seqs);
                db.PrMachines.Remove(old);
            }

            await db.SaveChangesAsync(ct); // physical key release

            // 9-10: Insert parents then machines (Added + Relocated new)
            foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Added && x.NodeType == HierarchyNodeType.WorkCentre))
            {
                var code = PlanningCodeNormalizer.NormalizeCode(c.Code);
                if (await db.PrWorkCentres.AnyAsync(x => x.WrkCtrCd == code && x.CompCode == write.CompanyCode, ct))
                    return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Duplicate work centre {code}.");
                db.PrWorkCentres.Add(new PrWorkCentre
                {
                    WrkCtrCd = code,
                    WrkCtrDes = PlanningCodeNormalizer.NormalizeDescription(c.Description),
                    Class = c.ClassCode,
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }

            foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Added && x.NodeType == HierarchyNodeType.Process))
            {
                var pc = PlanningCodeNormalizer.NormalizeCode(c.Code);
                var wc = PlanningCodeNormalizer.NormalizeCode(c.ParentCode);
                if (await db.PrProcesses.AnyAsync(x => x.ProcessCd == pc && x.WorkCentre == wc && x.CompCode == write.CompanyCode, ct))
                    return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Duplicate process {pc}.");
                db.PrProcesses.Add(new PrProcess
                {
                    ProcessCd = pc,
                    WorkCentre = wc,
                    ProcessDes = PlanningCodeNormalizer.NormalizeDescription(c.Description),
                    Sequence = c.Sequence,
                    Stock = c.Stock ?? false,
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }

            foreach (var c in changes.Where(x =>
                         (x.ChangeKind == HierarchyChangeKind.Added || x.ChangeKind == HierarchyChangeKind.Relocated)
                         && x.NodeType == HierarchyNodeType.Machine))
            {
                var mc = PlanningCodeNormalizer.NormalizeCode(c.Code);
                var pc = PlanningCodeNormalizer.NormalizeCode(c.ParentCode);
                if (await db.PrMachines.AnyAsync(x => x.MachineCd == mc && x.CompCode == write.CompanyCode, ct))
                    return PlanningServiceResult.Fail(PlanningErrorCode.DuplicateCode, $"Machine {mc} already assigned.");

                db.PrMachines.Add(new PrMachine
                {
                    MachineCd = mc,
                    ProcessCd = pc,
                    MachineDes = PlanningCodeNormalizer.NormalizeDescription(c.Description),
                    ConversionTime = c.ConversionTime ?? 0,
                    StartupTime = c.StartupTime ?? 0,
                    QueueTime = c.QueueTime ?? 0,
                    Active = true,
                    Created = DateTime.Now,
                    UserId = write.UserId,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });

                if (c.ChangeKind == HierarchyChangeKind.Relocated)
                {
                    // Preserve MacSeq with new process (recreate with SeqNo 1 default if none)
                    db.PrMacSeqs.Add(new PrMacSeq
                    {
                        MachineCd = mc,
                        ProcessCd = pc,
                        SeqNo = c.Sequence ?? 1,
                        Created = DateTime.Now,
                        UserId = write.UserId,
                        CompCode = write.CompanyCode,
                        BranchCode = write.BranchCode,
                        LocCode = write.LocationCode
                    });
                }
            }

            // 12: Modified
            foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Modified))
            {
                var code = PlanningCodeNormalizer.NormalizeCode(c.Code);
                switch (c.NodeType)
                {
                    case HierarchyNodeType.WorkCentre:
                    {
                        var e = await db.PrWorkCentres.FirstAsync(x => x.WrkCtrCd == code && x.CompCode == write.CompanyCode, ct);
                        if (c.OriginalUpdated is not null && e.Updated != c.OriginalUpdated)
                            return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Work centre {code} changed.");
                        e.WrkCtrDes = PlanningCodeNormalizer.NormalizeDescription(c.Description);
                        e.Class = c.ClassCode;
                        e.Updated = DateTime.Now;
                        e.UpdatedUid = write.UserId;
                        break;
                    }
                    case HierarchyNodeType.Process:
                    {
                        var wc = PlanningCodeNormalizer.NormalizeCode(c.ParentCode);
                        var e = await db.PrProcesses.FirstAsync(x => x.ProcessCd == code && x.WorkCentre == wc && x.CompCode == write.CompanyCode, ct);
                        if (c.OriginalUpdated is not null && e.Updated != c.OriginalUpdated)
                            return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Process {code} changed.");
                        e.ProcessDes = PlanningCodeNormalizer.NormalizeDescription(c.Description);
                        e.Sequence = c.Sequence;
                        e.Updated = DateTime.Now;
                        e.UpdatedUid = write.UserId;
                        break;
                    }
                    case HierarchyNodeType.Machine:
                    {
                        var pc = PlanningCodeNormalizer.NormalizeCode(c.ParentCode);
                        var e = await db.PrMachines.FirstAsync(x => x.MachineCd == code && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct);
                        if (c.OriginalUpdated is not null && e.Updated != c.OriginalUpdated)
                            return PlanningServiceResult.Fail(PlanningErrorCode.ConcurrencyConflict, $"Machine {code} changed.");
                        e.MachineDes = PlanningCodeNormalizer.NormalizeDescription(c.Description);
                        e.ConversionTime = c.ConversionTime ?? e.ConversionTime;
                        e.StartupTime = c.StartupTime ?? e.StartupTime;
                        e.QueueTime = c.QueueTime ?? e.QueueTime;
                        e.Updated = DateTime.Now;
                        e.UpdatedUid = write.UserId;
                        break;
                    }
                }
            }

            // 13: Remaining deletes leaf → root
            foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Deleted && x.NodeType == HierarchyNodeType.Machine))
            {
                var mc = PlanningCodeNormalizer.NormalizeCode(c.Code);
                var pc = PlanningCodeNormalizer.NormalizeCode(c.ParentCode);
                var seqs = await db.PrMacSeqs.Where(x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode).ToListAsync(ct);
                db.PrMacSeqs.RemoveRange(seqs);
                var e = await db.PrMachines.FirstOrDefaultAsync(x => x.MachineCd == mc && x.ProcessCd == pc && x.CompCode == write.CompanyCode, ct);
                if (e is not null) db.PrMachines.Remove(e);
            }
            foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Deleted && x.NodeType == HierarchyNodeType.Process))
            {
                var pc = PlanningCodeNormalizer.NormalizeCode(c.Code);
                var wc = PlanningCodeNormalizer.NormalizeCode(c.ParentCode);
                var e = await db.PrProcesses.FirstOrDefaultAsync(x => x.ProcessCd == pc && x.WorkCentre == wc && x.CompCode == write.CompanyCode, ct);
                if (e is not null) db.PrProcesses.Remove(e);
            }
            foreach (var c in changes.Where(x => x.ChangeKind == HierarchyChangeKind.Deleted && x.NodeType == HierarchyNodeType.WorkCentre))
            {
                var code = PlanningCodeNormalizer.NormalizeCode(c.Code);
                var e = await db.PrWorkCentres.FirstOrDefaultAsync(x => x.WrkCtrCd == code && x.CompCode == write.CompanyCode, ct);
                if (e is not null) db.PrWorkCentres.Remove(e);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("Hierarchy save committed for company {Company}", write.CompanyCode);
            return PlanningServiceResult.Ok("Hierarchy saved.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
