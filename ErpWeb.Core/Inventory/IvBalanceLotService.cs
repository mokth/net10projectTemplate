using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Balance-by-Lot inquiry service. Tenant scope is resolved FIRST from
/// <see cref="IInventoryTenantContext.TryBranchScope"/> (fail-closed), then ACCESS is checked before
/// any data is read. The value column is computed here after materialisation and cleared when
/// <c>ICurrentUserService.CanViewPrice</c> is false — the UI merely hides the column; it is never the
/// enforcement point.
/// </summary>
public sealed class IvBalanceLotService : IIvBalanceLotService
{
    private readonly IIvStockCommonRepository _common;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _currentDate;
    private readonly ICurrentUserService _currentUser;

    public IvBalanceLotService(
        IIvStockCommonRepository common,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService currentDate,
        ICurrentUserService currentUser)
    {
        _common = common;
        _tenant = tenant;
        _accessRights = accessRights;
        _currentDate = currentDate;
        _currentUser = currentUser;
    }

    public async Task<IvMasterOperationResult<IvBalanceLotPage>> SearchAsync(
        IvBalanceLotQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMasterOperationResult<IvBalanceLotPage>.Fail(IvMasterErrorCode.InvalidScope, context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryBalanceLot, PermissionCodes.Access, cancellationToken))
        {
            return IvMasterOperationResult<IvBalanceLotPage>.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new IvBalanceLotQuery();
        var (rows, total) = await _common.SearchBalanceLotPagedAsync(
            context.CompanyCode!, context.BranchCode!, query, cancellationToken);
        ApplyValueMasking(rows);

        return IvMasterOperationResult<IvBalanceLotPage>.Ok(new IvBalanceLotPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<IvBalanceLotSummary>> GetSummaryAsync(
        IvBalanceLotQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMasterOperationResult<IvBalanceLotSummary>.Fail(IvMasterErrorCode.InvalidScope, context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryBalanceLot, PermissionCodes.Access, cancellationToken))
        {
            return IvMasterOperationResult<IvBalanceLotSummary>.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new IvBalanceLotQuery();
        var summary = await _common.SummariseBalanceLotAsync(
            context.CompanyCode!, context.BranchCode!, query, _currentDate.Today, cancellationToken);

        summary.TotalValue = _currentUser.CanViewPrice
            ? IvQty.Round(summary.TotalValue ?? 0m)
            : null;

        return IvMasterOperationResult<IvBalanceLotSummary>.Ok(summary);
    }

    public async Task<IvMasterOperationResult<IvBalanceLotPage>> ExportRowsAsync(
        IvBalanceLotQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return IvMasterOperationResult<IvBalanceLotPage>.Fail(IvMasterErrorCode.InvalidScope, context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryBalanceLot, PermissionCodes.Access, cancellationToken))
        {
            return IvMasterOperationResult<IvBalanceLotPage>.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new IvBalanceLotQuery();
        var total = await _common.CountBalanceLotAsync(
            context.CompanyCode!, context.BranchCode!, query, cancellationToken);
        var rows = await _common.ListBalanceLotForExportAsync(
            context.CompanyCode!, context.BranchCode!, query, cancellationToken);
        ApplyValueMasking(rows);

        return IvMasterOperationResult<IvBalanceLotPage>.Ok(new IvBalanceLotPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    private void ApplyValueMasking(IReadOnlyList<IvBalanceLotRow> rows)
    {
        foreach (var row in rows)
        {
            if (_currentUser.CanViewPrice)
            {
                row.Value = IvQty.Round(row.StdQty * (row.UnitPrice ?? 0m));
            }
            else
            {
                row.Value = null;
            }
        }
    }

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return UserContext.Fail("Invalid company or branch context.");
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode!, scope.UserId);
    }

    private readonly record struct UserContext(string? CompanyCode, string? BranchCode, string? Error)
    {
        public static UserContext Ok(string companyCode, string branchCode, string userId) =>
            new(companyCode, branchCode, null);

        public static UserContext Fail(string error) =>
            new(null, null, error);
    }
}
