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
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        if (ready.ShouldSkipAllQaQueries)
        {
            return QaResult.OkPolicy(DefaultPolicy(), QaReadinessMessages.NotInstalled);
        }

        if (ready.ProbeFailed || !ready.CanQueryPolicy)
        {
            return QaResult.Fail(ready.UserMessage ?? QaReadinessMessages.Incomplete);
        }

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
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        if (ready.ShouldSkipAllQaQueries)
        {
            return QaResult.Fail(QaReadinessMessages.NotInstalled);
        }

        if (ready.ProbeFailed || !ready.CanQueryPolicy)
        {
            return QaResult.Fail(ready.UserMessage ?? QaReadinessMessages.Incomplete);
        }

        var published = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        var enableError = RejectNewEnablement(published, command);
        if (enableError is not null)
        {
            return QaResult.Fail(enableError);
        }

        if (WouldTurnOffGate(published, command)
            && await HasStockObligationsAsync(db, scope.CompanyCode, ready, cancellationToken))
        {
            return QaResult.Fail(QaReadinessMessages.CannotTurnOffWithObligations);
        }

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

        published.IncomingInspectionEnabled = KeepExistingOnly(published.IncomingInspectionEnabled, command.IncomingInspectionEnabled);
        published.FinalFgInspectionEnabled = KeepExistingOnly(published.FinalFgInspectionEnabled, command.FinalFgInspectionEnabled);
        published.LogicalHoldEnabled = KeepExistingOnly(published.LogicalHoldEnabled, command.LogicalHoldEnabled);
        published.PhysicalHoldEnabled = KeepExistingOnly(published.PhysicalHoldEnabled, command.PhysicalHoldEnabled);
        published.NonGoodEnabled = KeepExistingOnly(published.NonGoodEnabled, command.NonGoodEnabled);
        published.Enabled = published.IncomingInspectionEnabled
            || published.FinalFgInspectionEnabled
            || published.LogicalHoldEnabled
            || published.PhysicalHoldEnabled
            || published.NonGoodEnabled;
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
        CancellationToken cancellationToken)
    {
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        if (ready.ShouldSkipAllQaQueries)
        {
            return null;
        }

        if (ready.ProbeFailed || !ready.CanQueryPolicy)
        {
            throw new InvalidOperationException(ready.UserMessage ?? QaReadinessMessages.Incomplete);
        }

        return await db.QaPolicies
            .Where(x => x.CompanyCode == companyCode && x.IsPublished)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

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

    internal static string? RejectNewEnablement(QaPolicy? published, QaPolicySaveCommand command)
    {
        if (command.IncomingInspectionEnabled && published?.IncomingInspectionEnabled != true)
        {
            return QaReadinessMessages.FrozenEnablement("incoming inspection");
        }

        if (command.FinalFgInspectionEnabled && published?.FinalFgInspectionEnabled != true)
        {
            return QaReadinessMessages.FrozenEnablement("finished-goods inspection");
        }

        if (command.LogicalHoldEnabled && published?.LogicalHoldEnabled != true)
        {
            return QaReadinessMessages.FrozenEnablement("logical hold");
        }

        if (command.PhysicalHoldEnabled && published?.PhysicalHoldEnabled != true)
        {
            return QaReadinessMessages.FrozenEnablement("physical hold");
        }

        if (command.NonGoodEnabled && published?.NonGoodEnabled != true)
        {
            return QaReadinessMessages.FrozenEnablement("non-good Daily Production");
        }

        return null;
    }

    private static bool WouldTurnOffGate(QaPolicy? published, QaPolicySaveCommand command) =>
        published is not null
        && ((published.IncomingInspectionEnabled && !command.IncomingInspectionEnabled)
            || (published.FinalFgInspectionEnabled && !command.FinalFgInspectionEnabled)
            || (published.LogicalHoldEnabled && !command.LogicalHoldEnabled)
            || (published.PhysicalHoldEnabled && !command.PhysicalHoldEnabled)
            || (published.NonGoodEnabled && !command.NonGoodEnabled));

    private static bool KeepExistingOnly(bool current, bool requested) => current && requested;

    private static async Task<bool> HasStockObligationsAsync(
        AppDbContext db,
        string companyCode,
        QaSchemaReadinessResult ready,
        CancellationToken cancellationToken)
    {
        if (ready.Incoming.IsReady
            && await db.QaReceiptAllocations.AnyAsync(
                x => x.CompanyCode == companyCode && x.Status == QaReceiptAllocationStatuses.Posted,
                cancellationToken))
        {
            return true;
        }

        if (ready.FinalFg.IsReady
            && await db.QaFgAllocations.AnyAsync(
                x => x.CompanyCode == companyCode && x.Status == QaReceiptAllocationStatuses.Posted,
                cancellationToken))
        {
            return true;
        }

        if (ready.NonGood.IsReady
            && await db.QaNonGoodAllocations.AnyAsync(
                x => x.CompanyCode == companyCode && x.Status == QaReceiptAllocationStatuses.Posted,
                cancellationToken))
        {
            return true;
        }

        if (ready.Logical.IsReady
            && await db.QaLogicalHolds.AnyAsync(
                x => x.CompanyCode == companyCode && x.Status == QaLogicalHoldStatuses.Active,
                cancellationToken))
        {
            return true;
        }

        if (ready.Physical.IsReady
            && (await db.QaPhysicalHolds.AnyAsync(
                    x => x.CompanyCode == companyCode && x.Status == QaLogicalHoldStatuses.Active,
                    cancellationToken)
                || await db.QaPhysicalAllocations.AnyAsync(
                    x => x.CompanyCode == companyCode && x.Status == QaReceiptAllocationStatuses.Posted,
                    cancellationToken)))
        {
            return true;
        }

        return false;
    }

    private static string TruncateUser(string userId) =>
        userId.Length <= 20 ? userId : userId[..20];
}
