using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public sealed class CompletePreventiveRequest
{
    public DateTime ActualStart { get; set; }
    public DateTime ActualEnd { get; set; }
    public string? ActionTaken { get; set; }
    public decimal PartsCost { get; set; }
    public decimal LabourCost { get; set; }
    public decimal OtherCost { get; set; }
}

public sealed class PrMachineMaintenanceFilter
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? MachineCd { get; set; }
    public string? MaintenanceType { get; set; }
    public string? Status { get; set; }
    public string? ReasonCd { get; set; }
}

public interface IPrMachineMaintenanceService
{
    Task<PlanningServiceResult<IReadOnlyList<PrMacMaintenance>>> ListAsync(PrMachineMaintenanceFilter filter, CancellationToken ct = default);
    Task<PlanningServiceResult<PrMacMaintenance>> GetAsync(int id, CancellationToken ct = default);
    Task<PlanningServiceResult<int>> CreateAsync(PrMacMaintenance request, CancellationToken ct = default);
    Task<PlanningServiceResult> UpdateAsync(PrMacMaintenance request, CancellationToken ct = default);
    Task<PlanningServiceResult> CompleteAsync(int id, CancellationToken ct = default);
    Task<PlanningServiceResult> CancelAsync(int id, CancellationToken ct = default);
    Task<PlanningServiceResult<int>> CompletePreventiveAsync(int preventiveUid, CompletePreventiveRequest request, CancellationToken ct = default);
}

public sealed class PrMachineMaintenanceService : IPrMachineMaintenanceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly ILogger<PrMachineMaintenanceService> _logger;

    public PrMachineMaintenanceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access,
        ILogger<PrMachineMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<IReadOnlyList<PrMacMaintenance>>> ListAsync(PrMachineMaintenanceFilter filter, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningMachineMaintenance, ct))
            return PlanningServiceResult<IReadOnlyList<PrMacMaintenance>>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<IReadOnlyList<PrMacMaintenance>>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.PrMacMaintenances.AsNoTracking().Where(x => x.CompCode == scope.CompanyCode);
        if (filter.FromDate is not null)
            q = q.Where(x => x.TrxDate >= filter.FromDate.Value.Date);
        if (filter.ToDate is not null)
        {
            var to = filter.ToDate.Value.Date.AddDays(1);
            q = q.Where(x => x.TrxDate < to);
        }
        if (!string.IsNullOrWhiteSpace(filter.MachineCd))
        {
            var mac = PlanningCodeNormalizer.NormalizeCode(filter.MachineCd);
            q = q.Where(x => x.MacCode == mac);
        }
        if (!string.IsNullOrWhiteSpace(filter.MaintenanceType))
            q = q.Where(x => x.MType == filter.MaintenanceType);
        if (!string.IsNullOrWhiteSpace(filter.Status))
            q = q.Where(x => x.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.ReasonCd))
        {
            var rc = PlanningCodeNormalizer.NormalizeCode(filter.ReasonCd);
            q = q.Where(x => x.ReasonCd == rc);
        }

        var rows = await q.OrderByDescending(x => x.TrxDate).ThenByDescending(x => x.Id).Take(2000).ToListAsync(ct);
        return PlanningServiceResult<IReadOnlyList<PrMacMaintenance>>.Ok(rows);
    }

    public async Task<PlanningServiceResult<PrMacMaintenance>> GetAsync(int id, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningMachineMaintenance, ct))
            return PlanningServiceResult<PrMacMaintenance>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
            return PlanningServiceResult<PrMacMaintenance>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.PrMacMaintenances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.CompCode == scope.CompanyCode, ct);
        if (row is null)
            return PlanningServiceResult<PrMacMaintenance>.Fail(PlanningErrorCode.ValidationFailed, "Maintenance not found.");
        return PlanningServiceResult<PrMacMaintenance>.Ok(row);
    }

    public async Task<PlanningServiceResult<int>> CreateAsync(PrMacMaintenance request, CancellationToken ct = default)
    {
        if (!await _access.CanAddAsync(MenuCodes.PlanningMachineMaintenance, ct))
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        var write = _tenant.TryWriteScope();
        if (write is null)
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        var err = ValidateForStatus(request, requireActiveMachine: true);
        if (err is not null)
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, err);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var mac = PlanningCodeNormalizer.NormalizeCode(request.MacCode);
        var machineOk = await db.PrMachines.AsNoTracking()
            .AnyAsync(x => x.MachineCd == mac && x.CompCode == write.CompanyCode && x.Active, ct);
        if (!machineOk)
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, $"Active machine {mac} not found.");

        if (!string.IsNullOrWhiteSpace(request.ReasonCd))
        {
            var rc = PlanningCodeNormalizer.NormalizeCode(request.ReasonCd);
            if (!await db.PrMaintenanceReasons.AsNoTracking().AnyAsync(x => x.CompCode == write.CompanyCode && x.ReasonCd == rc && x.Active, ct))
                return PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, $"Reason {rc} is not active.");
            request.ReasonCd = rc;
        }

        var entity = new PrMacMaintenance
        {
            TrxDate = request.TrxDate?.Date ?? DateTime.Today,
            MacCode = mac,
            Description = request.Description,
            ActionTaken = request.ActionTaken,
            Status = NormalizeStatus(request.Status) ?? PrMaintenanceStatuses.Open,
            ReportBy = request.ReportBy ?? write.UserId,
            ActionBy = request.ActionBy,
            ActionOn = request.ActionOn,
            RefCode = request.RefCode,
            MType = NormalizeType(request.MType)!,
            Name = request.Name,
            StartDateTime = request.StartDateTime,
            EndDateTime = request.EndDateTime,
            ReasonCd = request.ReasonCd,
            PartsCost = request.PartsCost,
            LabourCost = request.LabourCost,
            OtherCost = request.OtherCost,
            Remark = request.Remark,
            CompCode = write.CompanyCode,
            BranchCode = write.BranchCode,
            LocCode = write.LocationCode
        };

        db.PrMacMaintenances.Add(entity);
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult<int>.Ok(entity.Id);
    }

    public async Task<PlanningServiceResult> UpdateAsync(PrMacMaintenance request, CancellationToken ct = default)
    {
        if (!await _access.CanEditAsync(MenuCodes.PlanningMachineMaintenance, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMachineMaintenance);
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        var err = ValidateForStatus(request, requireActiveMachine: false);
        if (err is not null)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, err);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PrMacMaintenances.FirstOrDefaultAsync(
            x => x.Id == request.Id && x.CompCode == write.CompanyCode, ct);
        if (entity is null)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Maintenance not found.");

        var mac = PlanningCodeNormalizer.NormalizeCode(request.MacCode);
        if (!await db.PrMachines.AsNoTracking().AnyAsync(x => x.MachineCd == mac && x.CompCode == write.CompanyCode, ct))
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Machine {mac} not found.");

        if (!string.IsNullOrWhiteSpace(request.ReasonCd))
        {
            var rc = PlanningCodeNormalizer.NormalizeCode(request.ReasonCd);
            if (!await db.PrMaintenanceReasons.AsNoTracking().AnyAsync(x => x.CompCode == write.CompanyCode && x.ReasonCd == rc && x.Active, ct))
                return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, $"Reason {rc} is not active.");
            entity.ReasonCd = rc;
        }
        else entity.ReasonCd = null;

        entity.TrxDate = request.TrxDate?.Date;
        entity.MacCode = mac;
        entity.Description = request.Description;
        entity.ActionTaken = request.ActionTaken;
        entity.Status = NormalizeStatus(request.Status) ?? entity.Status;
        entity.ReportBy = request.ReportBy;
        entity.ActionBy = request.ActionBy;
        entity.ActionOn = request.ActionOn;
        entity.MType = NormalizeType(request.MType) ?? entity.MType;
        entity.Name = request.Name;
        entity.StartDateTime = request.StartDateTime;
        entity.EndDateTime = request.EndDateTime;
        entity.PartsCost = request.PartsCost;
        entity.LabourCost = request.LabourCost;
        entity.OtherCost = request.OtherCost;
        entity.Remark = request.Remark;

        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Maintenance updated.");
    }

    public async Task<PlanningServiceResult> CompleteAsync(int id, CancellationToken ct = default)
    {
        if (!await _access.CanEditAsync(MenuCodes.PlanningMachineMaintenance, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMachineMaintenance);
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PrMacMaintenances.FirstOrDefaultAsync(
            x => x.Id == id && x.CompCode == write.CompanyCode, ct);
        if (entity is null)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Maintenance not found.");
        if (string.Equals(entity.Status, PrMaintenanceStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Cancelled maintenance cannot be completed.");

        entity.Status = PrMaintenanceStatuses.Completed;
        entity.ActionBy ??= write.UserId;
        entity.ActionOn ??= DateTime.Now;
        var err = ValidateForStatus(entity, requireActiveMachine: false);
        if (err is not null)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, err);

        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Maintenance completed.");
    }

    public async Task<PlanningServiceResult> CancelAsync(int id, CancellationToken ct = default)
    {
        if (!await _access.CanEditAsync(MenuCodes.PlanningMachineMaintenance, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningMachineMaintenance);
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PrMacMaintenances.FirstOrDefaultAsync(
            x => x.Id == id && x.CompCode == write.CompanyCode, ct);
        if (entity is null)
            return PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Maintenance not found.");

        entity.Status = PrMaintenanceStatuses.Cancelled;
        await db.SaveChangesAsync(ct);
        return PlanningServiceResult.Ok("Maintenance cancelled.");
    }

    public async Task<PlanningServiceResult<int>> CompletePreventiveAsync(int preventiveUid, CompletePreventiveRequest request, CancellationToken ct = default)
    {
        if (!await _access.CanEditAsync(MenuCodes.PlanningMacPreventive, ct)
            && !await _access.CanEditAsync(MenuCodes.PlanningMachineMaintenance, ct)
            && !await _access.CanAddAsync(MenuCodes.PlanningMachineMaintenance, ct))
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");

        var write = _tenant.TryWriteScope();
        if (write is null)
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.TenantScopeError, "Tenant required.");

        if (request.ActualEnd < request.ActualStart)
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, "Actual End must be on or after Actual Start.");
        if (request.PartsCost < 0 || request.LabourCost < 0 || request.OtherCost < 0)
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, "Costs cannot be negative.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var preventive = await db.PrPreventives.FirstOrDefaultAsync(
                x => x.Uid == preventiveUid && x.CompCode == write.CompanyCode, ct);
            if (preventive is null)
                return PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, "Preventive not found.");

            if (string.Equals(preventive.Status, PreventiveStatuses.Completed, StringComparison.OrdinalIgnoreCase))
            {
                var existing = await db.PrMacMaintenances.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.PreventiveUid == preventiveUid && x.CompCode == write.CompanyCode, ct);
                await tx.CommitAsync(ct);
                return existing is null
                    ? PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, "Preventive already completed.")
                    : PlanningServiceResult<int>.Ok(existing.Id, "Already completed.");
            }

            if (!string.Equals(preventive.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase))
                return PlanningServiceResult<int>.Fail(PlanningErrorCode.ValidationFailed, "Only PLANNED preventive rows can be completed.");

            if (await db.PrMacMaintenances.AnyAsync(x => x.PreventiveUid == preventiveUid, ct))
                return PlanningServiceResult<int>.Fail(PlanningErrorCode.DuplicateCode, "Maintenance history already linked to this preventive.");

            var history = new PrMacMaintenance
            {
                TrxDate = preventive.DownDt.Date,
                MacCode = preventive.MachineCd,
                Description = preventive.Remark ?? preventive.ReasonCd ?? "Preventive maintenance",
                ActionTaken = request.ActionTaken,
                Status = PrMaintenanceStatuses.Completed,
                ReportBy = write.UserId,
                ActionBy = write.UserId,
                ActionOn = DateTime.Now,
                MType = PrMaintenanceTypes.Preventive,
                ReasonCd = preventive.ReasonCd,
                StartDateTime = request.ActualStart,
                EndDateTime = request.ActualEnd,
                PartsCost = request.PartsCost,
                LabourCost = request.LabourCost,
                OtherCost = request.OtherCost,
                PreventiveUid = preventive.Uid,
                CompCode = write.CompanyCode,
                BranchCode = write.BranchCode,
                LocCode = write.LocationCode
            };

            db.PrMacMaintenances.Add(history);
            preventive.Status = PreventiveStatuses.Completed;
            preventive.CompletedOn = DateTime.Now;
            preventive.CompletedBy = write.UserId;
            preventive.Updated = DateTime.Now;
            preventive.UserId = write.UserId;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("Completed preventive {Uid} -> maintenance {Id} for company {Company}",
                preventiveUid, history.Id, write.CompanyCode);
            return PlanningServiceResult<int>.Ok(history.Id, "Preventive completed.");
        }
        catch (DbUpdateException ex) when (
            ex.InnerException?.Message.Contains("UX_PrMacMaintenance_PreventiveUid", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            await tx.RollbackAsync(ct);
            await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
            var existing = await db2.PrMacMaintenances.AsNoTracking()
                .FirstOrDefaultAsync(x => x.PreventiveUid == preventiveUid && x.CompCode == write.CompanyCode, ct);
            if (existing is not null)
                return PlanningServiceResult<int>.Ok(existing.Id, "Already completed.");
            return PlanningServiceResult<int>.Fail(PlanningErrorCode.ConcurrencyConflict,
                "Preventive was completed by another user. Refresh and try again.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static string? NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var t = type.Trim().ToUpperInvariant();
        return PrMaintenanceTypes.IsKnown(t) ? t : null;
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var s = status.Trim().ToUpperInvariant();
        return PrMaintenanceStatuses.IsKnown(s) ? s : null;
    }

    private static string? ValidateForStatus(PrMacMaintenance row, bool requireActiveMachine)
    {
        _ = requireActiveMachine;
        var mac = PlanningCodeNormalizer.NormalizeCode(row.MacCode);
        if (string.IsNullOrWhiteSpace(mac))
            return "Machine is required.";
        var type = NormalizeType(row.MType);
        if (type is null)
            return "Maintenance type is required (PREVENTIVE/BREAKDOWN/REPAIR/SERVICE/OTHER).";
        row.MType = type;

        if (string.IsNullOrWhiteSpace(row.Description))
            return "Description / Problem is required.";
        if (row.PartsCost < 0 || row.LabourCost < 0 || row.OtherCost < 0)
            return "Costs cannot be negative.";

        var status = NormalizeStatus(row.Status) ?? PrMaintenanceStatuses.Open;
        row.Status = status;

        if (string.Equals(status, PrMaintenanceStatuses.InProgress, StringComparison.OrdinalIgnoreCase)
            && row.StartDateTime is null)
            return "Start Date/Time is required for IN_PROGRESS.";

        if (string.Equals(status, PrMaintenanceStatuses.Completed, StringComparison.OrdinalIgnoreCase))
        {
            if (row.StartDateTime is null || row.EndDateTime is null)
                return "Start and End Date/Time are required for COMPLETED.";
            if (row.EndDateTime <= row.StartDateTime)
                return "End Date/Time must be after Start Date/Time.";
            if (string.IsNullOrWhiteSpace(row.ActionBy))
                return "Action By is required for COMPLETED.";
        }

        if (row.TrxDate is null)
            return "Transaction date is required.";

        return null;
    }
}
