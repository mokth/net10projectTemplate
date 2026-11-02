using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Quality;

public sealed class QaPolicyService : IQaPolicyService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public QaPolicyService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<QaResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.QualitySettings, PermissionCodes.Access, cancellationToken)
            && !await _accessRights.CanAsync(MenuCodes.QualityWorkbench, PermissionCodes.Access, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var policy = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        return QaResult.OkPolicy(policy is null ? DefaultPolicy() : Map(policy));
    }

    public async Task<QaResult> SaveAsync(QaPolicySaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryWriteScope() ?? _tenant.TryCompanyScope();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.QualitySettings, PermissionCodes.Edit, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var published = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        if (published is null)
        {
            published = new QaPolicy
            {
                CompanyCode = scope.CompanyCode,
                Version = 1,
                IsPublished = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = TruncateUser(scope.UserId)
            };
            db.QaPolicies.Add(published);
        }
        else if (command.RowVersion is { Length: > 0 }
                 && published.RowVersion is { Length: > 0 }
                 && !published.RowVersion.SequenceEqual(command.RowVersion))
        {
            return QaResult.Fail("This policy was changed by someone else. Reload and try again.");
        }

        published.Enabled = command.IncomingInspectionEnabled
            || command.FinalFgInspectionEnabled
            || command.LogicalHoldEnabled
            || command.PhysicalHoldEnabled
            || command.NonGoodEnabled;
        published.IncomingInspectionEnabled = command.IncomingInspectionEnabled;
        published.FinalFgInspectionEnabled = command.FinalFgInspectionEnabled;
        published.LogicalHoldEnabled = command.LogicalHoldEnabled;
        published.PhysicalHoldEnabled = command.PhysicalHoldEnabled;
        published.NonGoodEnabled = command.NonGoodEnabled;
        published.InspectorApproverSeparation = command.InspectorApproverSeparation;
        published.IsPublished = true;
        published.ModifiedDate = DateTime.UtcNow;
        published.ModifiedBy = TruncateUser(scope.UserId);

        await db.SaveChangesAsync(cancellationToken);
        return QaResult.OkPolicy(Map(published));
    }

    internal static async Task<QaPolicy?> LoadPublishedAsync(
        AppDbContext db,
        string companyCode,
        CancellationToken cancellationToken) =>
        await db.QaPolicies
            .Where(x => x.CompanyCode == companyCode && x.IsPublished)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public static QaPolicyDto DefaultPolicy() => new()
    {
        Version = 0,
        Enabled = false,
        IncomingInspectionEnabled = false,
        FinalFgInspectionEnabled = false,
        LogicalHoldEnabled = false,
        PhysicalHoldEnabled = false,
        NonGoodEnabled = false,
        InspectorApproverSeparation = true,
        IsPublished = false
    };

    internal static QaPolicyDto Map(QaPolicy policy) => new()
    {
        Version = policy.Version,
        Enabled = policy.IncomingInspectionEnabled
            || policy.FinalFgInspectionEnabled
            || policy.LogicalHoldEnabled
            || policy.PhysicalHoldEnabled
            || policy.NonGoodEnabled,
        IncomingInspectionEnabled = policy.IncomingInspectionEnabled,
        FinalFgInspectionEnabled = policy.FinalFgInspectionEnabled,
        LogicalHoldEnabled = policy.LogicalHoldEnabled,
        PhysicalHoldEnabled = policy.PhysicalHoldEnabled,
        NonGoodEnabled = policy.NonGoodEnabled,
        InspectorApproverSeparation = policy.InspectorApproverSeparation,
        IsPublished = policy.IsPublished,
        RowVersion = policy.RowVersion
    };

    private static string TruncateUser(string userId) =>
        userId.Length <= 20 ? userId : userId[..20];
}
