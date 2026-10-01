using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

public interface IPrMaintenanceReasonService
{
    Task<PlanningServiceResult<IReadOnlyList<PrMaintenanceReason>>> ListAsync(bool activeOnly = false, CancellationToken ct = default);
    Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrMaintenanceReason> rows, IReadOnlyList<string> deletedCodes, CancellationToken ct = default);
}

public sealed class PrMaintenanceReasonService : IPrMaintenanceReasonService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;

    public PrMaintenanceReasonService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrMaintenanceReason>>> ListAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningMaintenanceReason, ct))
            return PlanningServiceResult<IReadOnlyList<PrMaintenanceReason>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<IReadOnlyList<PrMaintenanceReason>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.PrMaintenanceReasons.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode);
        if (activeOnly)
            q = q.Where(x => x.Active);
        var rows = await q.OrderBy(x => x.ReasonCd).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrMaintenanceReason>>.Ok(rows);
    }

    public async Task<PlanningServiceResult> SaveBatchAsync(IReadOnlyList<PrMaintenanceReason> rows, IReadOnlyList<string> deletedCodes, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        var hasNew = false;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (deletedCodes.Count > 0 && !await _access.CanDeleteAsync(MenuCodes.PlanningMaintenanceReason, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMaintenanceReason);

        foreach (var code in deletedCodes)
        {
            var cd = PlanningCodeNormalizer.NormalizeCode(code);
            var e = await db.PrMaintenanceReasons.FirstOrDefaultAsync(
                x => x.ReasonCd == cd && x.CompCode == write.CompanyCode, ct);
            if (e is not null) db.PrMaintenanceReasons.Remove(e);
        }

        foreach (var row in rows)
        {
            var cd = PlanningCodeNormalizer.NormalizeCode(row.ReasonCd);
            var err = PlanningInputValidation.ValidateCode(cd);
            if (err is not null)
                return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, err);
            if (string.IsNullOrWhiteSpace(row.Description))
                return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Description is required.");
            var reasonType = (row.ReasonType ?? string.Empty).Trim().ToUpperInvariant();
            if (!PrMaintenanceReasonTypes.IsKnown(reasonType))
                return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Invalid ReasonType.");

            var existing = await db.PrMaintenanceReasons.FirstOrDefaultAsync(
                x => x.ReasonCd == cd && x.CompCode == write.CompanyCode, ct);
            if (existing is null)
            {
                hasNew = true;
                if (!await _access.CanAddAsync(MenuCodes.PlanningMaintenanceReason, ct))
                    return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMaintenanceReason);
                db.PrMaintenanceReasons.Add(new PrMaintenanceReason
                {
                    ReasonCd = cd,
                    Description = PlanningCodeNormalizer.NormalizeDescription(row.Description) ?? row.Description,
                    ReasonType = reasonType,
                    Active = row.Active,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode,
                    Created = DateTime.Now,
                    UserId = write.UserId
                });
            }
            else
            {
                if (!await _access.CanEditAsync(MenuCodes.PlanningMaintenanceReason, ct))
                    return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMaintenanceReason);
                existing.Description = PlanningCodeNormalizer.NormalizeDescription(row.Description) ?? row.Description;
                existing.ReasonType = reasonType;
                existing.Active = row.Active;
                existing.Updated = DateTime.Now;
                existing.UpdatedUid = write.UserId;
            }
        }

        _ = hasNew;
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Maintenance reasons saved.");
    }
}
