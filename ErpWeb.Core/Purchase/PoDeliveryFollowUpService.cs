using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.Core.Purchase;

/// <summary>Scoped append-only supplier contact log. Writes only PoDeliveryFollowUp.</summary>
public sealed class PoDeliveryFollowUpService : IPoDeliveryFollowUpService
{
    private static readonly HashSet<string> AllowedOutcomes =
        new(PoDeliveryFollowUpOutcomes.All, StringComparer.OrdinalIgnoreCase);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IConfiguration _configuration;
    private readonly ICurrentDateService _dates;

    public PoDeliveryFollowUpService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IConfiguration configuration,
        ICurrentDateService dates)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _configuration = configuration;
        _dates = dates;
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<PoDeliveryFollowUpRow>>> ListAsync(
        string poNo,
        short poRelNo,
        short poLineNo,
        CancellationToken cancellationToken = default)
    {
        var gate = await ResolveAsync(write: false, cancellationToken);
        if (gate.Error is not null)
        {
            return IvMasterOperationResult<IReadOnlyList<PoDeliveryFollowUpRow>>.Fail(gate.ErrorCode, gate.Error);
        }

        if (!ValidKey(poNo))
        {
            return IvMasterOperationResult<IReadOnlyList<PoDeliveryFollowUpRow>>.Fail(
                IvMasterErrorCode.Validation, "A valid PO number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.PoDeliveryFollowUps.AsNoTracking()
            .Where(x => x.CompanyCode == gate.CompanyCode
                        && x.BranchCode == gate.BranchCode
                        && x.PoNo == poNo.Trim()
                        && x.PoRelNo == poRelNo
                        && x.PoLineNo == poLineNo)
            .OrderByDescending(x => x.ContactAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => new PoDeliveryFollowUpRow
            {
                Id = x.Id,
                ContactAtUtc = x.ContactAtUtc,
                Outcome = x.Outcome,
                SupplierStatedDate = x.SupplierStatedDate,
                NextFollowUpDate = x.NextFollowUpDate,
                Note = x.Note,
                CreatedBy = x.CreatedBy,
                CreatedAtUtc = x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<PoDeliveryFollowUpRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<long>> AddAsync(
        PoDeliveryFollowUpRequest request,
        CancellationToken cancellationToken = default)
    {
        var gate = await ResolveAsync(write: true, cancellationToken);
        if (gate.Error is not null)
        {
            return IvMasterOperationResult<long>.Fail(gate.ErrorCode, gate.Error);
        }

        if (request is null || !ValidKey(request.PoNo))
        {
            return Validation("PoNo", "A valid PO number is required.");
        }

        if (!AllowedOutcomes.TryGetValue(request.Outcome?.Trim() ?? string.Empty, out var canonicalOutcome))
        {
            return Validation("Outcome", "Choose No Response, Confirmed, Delayed, or Resolved.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 500)
        {
            return Validation("Note", "Note must be 500 characters or fewer.");
        }

        var today = DateOnly.FromDateTime(_dates.Today);
        var latestAllowedDate = today.AddYears(10);
        if (!DateRangeValid(request.SupplierStatedDate, new DateOnly(2000, 1, 1), latestAllowedDate))
        {
            return Validation("SupplierStatedDate", "Supplier-stated date must be within the supported date range.");
        }

        if (!DateRangeValid(request.NextFollowUpDate, today, latestAllowedDate))
        {
            return Validation("NextFollowUpDate", "Next follow-up must be today or later and within ten years.");
        }

        var userId = gate.UserId?.Trim();
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 10)
        {
            return Validation("CreatedBy", "The current user identifier is missing or exceeds the supported length.");
        }

        var poNo = request.PoNo.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Verify the exact historical revision/line inside the authenticated company and branch.
        // No PO entity is tracked and no existing transaction service is called.
        var targetExists = await db.PoOrderDetails.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == gate.CompanyCode
            && x.BranchCode == gate.BranchCode
            && x.PoNo == poNo
            && x.PoRelNo == request.PoRelNo
            && x.Line == request.PoLineNo,
            cancellationToken);
        if (!targetExists)
        {
            return IvMasterOperationResult<long>.Fail(IvMasterErrorCode.NotFound, "PO line was not found in the current branch.");
        }

        var contactAtUtc = DateTime.UtcNow;
        var entity = new PoDeliveryFollowUp
        {
            CompanyCode = gate.CompanyCode!,
            BranchCode = gate.BranchCode!,
            PoNo = poNo,
            PoRelNo = request.PoRelNo,
            PoLineNo = request.PoLineNo,
            ContactAtUtc = contactAtUtc,
            Outcome = canonicalOutcome,
            SupplierStatedDate = request.SupplierStatedDate,
            NextFollowUpDate = request.NextFollowUpDate,
            Note = note,
            CreatedBy = userId,
            CreatedAtUtc = contactAtUtc
        };

        db.PoDeliveryFollowUps.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return IvMasterOperationResult<long>.Ok(entity.Id);
    }

    private async Task<FollowUpGate> ResolveAsync(bool write, CancellationToken cancellationToken)
    {
        // Tenant first, then feature flag and rights; do not disclose cross-branch existence.
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return FollowUpGate.Fail(IvMasterErrorCode.InvalidScope, "Invalid company or branch context.");
        }

        if (!_configuration.GetValue<bool>("Features:SupplierDeliveryFollowUp"))
        {
            return FollowUpGate.Fail(IvMasterErrorCode.AccessDenied, "Supplier delivery follow-up is not enabled.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseDeliveryFollowUp, PermissionCodes.Access, cancellationToken))
        {
            return FollowUpGate.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (write && !await _accessRights.CanAsync(MenuCodes.PurchaseDeliveryFollowUp, PermissionCodes.Add, cancellationToken))
        {
            return FollowUpGate.Fail(IvMasterErrorCode.AccessDenied, "Not authorized to add a supplier follow-up.");
        }

        return FollowUpGate.Ok(scope.CompanyCode, scope.BranchCode, scope.UserId);
    }

    private static bool ValidKey(string? poNo) => !string.IsNullOrWhiteSpace(poNo) && poNo.Trim().Length <= 30;

    private static bool DateRangeValid(DateOnly? value, DateOnly minimum, DateOnly maximum) =>
        value is null || value.Value >= minimum && value.Value <= maximum;

    private static IvMasterOperationResult<long> Validation(string field, string message) =>
        IvMasterOperationResult<long>.Fail(
            IvMasterErrorCode.Validation,
            message,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message });

    private readonly record struct FollowUpGate(
        string? CompanyCode, string? BranchCode, string? UserId, IvMasterErrorCode ErrorCode, string? Error)
    {
        public static FollowUpGate Ok(string company, string branch, string userId) =>
            new(company, branch, userId, IvMasterErrorCode.None, null);

        public static FollowUpGate Fail(IvMasterErrorCode code, string error) =>
            new(null, null, null, code, error);
    }
}
