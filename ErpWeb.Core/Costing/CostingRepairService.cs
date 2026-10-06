using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Costing;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class CostingRepairService : ICostingRepairService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly ICostingRepairPlanner _planner;
    private readonly IEnumerable<ICostingRepairAdapter> _adapters;

    public CostingRepairService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICostingRepairPlanner planner,
        IEnumerable<ICostingRepairAdapter> adapters)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _planner = planner;
        _adapters = adapters;
    }

    public async Task<CostingRepairExecutionResult> ExecuteReverseAsync(
        long stockPostingId,
        string previewHash,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.RepairCost, cancellationToken))
            return new CostingRepairExecutionResult { Message = "Not authorized to repair cost.", Status = "DENIED" };
        if (string.IsNullOrWhiteSpace(reason))
            return new CostingRepairExecutionResult { Message = "A repair reason is required.", Status = "DENIED" };

        var plan = await _planner.PlanAsync(stockPostingId, cancellationToken);
        if (!string.Equals(plan.PreviewHash, previewHash, StringComparison.Ordinal))
            return new CostingRepairExecutionResult { Stale = true, Status = "REPLAN_REQUIRED", Message = "The preview is stale. Build a new plan before executing." };
        if (!plan.CanRepair || plan.Steps.Count != 1)
            return new CostingRepairExecutionResult { Status = "PREVIEW_ONLY", Message = plan.BlockingReason ?? "This chain cannot be repaired." };

        var step = plan.Steps[0];
        var menu = MenuFor(step.OwnerType);
        if (menu is null || !await _access.CanAsync(menu, PermissionCodes.Rollback, cancellationToken))
            return new CostingRepairExecutionResult { Status = "DENIED", Message = "REPAIR_COST does not replace the source document ROLLBACK permission." };

        var adapter = _adapters.SingleOrDefault(x => x.OwnerTypes.Contains(step.OwnerType));
        if (adapter is null)
            return new CostingRepairExecutionResult { Status = "PREVIEW_ONLY", Message = "No adapter is registered for the resolved owner." };

        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return new CostingRepairExecutionResult { Status = "DENIED", Message = "A trusted company and branch are required." };

        var requestId = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var repairCase = new CostingRepairCase
        {
            RepairRequestId = requestId,
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode,
            RootStockPostingId = stockPostingId,
            Strategy = plan.Strategy,
            PreviewHash = plan.PreviewHash,
            Status = "STARTED",
            RequestedBy = scope.UserId,
            RequestedAtUtc = now,
            StartedAtUtc = now,
            Reason = reason.Trim()
        };
        repairCase.Events.Add(Event(repairCase, 1, "STEP_PREPARED", step, scope.UserId, now));
        db.CostingRepairCases.Add(repairCase);
        await db.SaveChangesAsync(cancellationToken);

        CostingRepairStepResult result;
        try
        {
            result = await adapter.ReverseAsync(
                new CostingRepairNode(stockPostingId, step.PhysicalSourceDocumentType, step.PhysicalSourceDocumentId),
                new CostingRepairOwner(step.OwnerType, step.OwnerDocumentNo, step.PhysicalSourceDocumentType, step.PhysicalSourceDocumentId, stockPostingId),
                new CostingRepairExecutionContext(requestId, step.StableStepId, "POSTED", reason.Trim()),
                cancellationToken);
        }
        catch (Exception ex)
        {
            await AppendAsync(db, repairCase, 2, "STEP_FAILED", step, scope.UserId, "NEEDS_ATTENTION", ex.Message, cancellationToken);
            return new CostingRepairExecutionResult
            {
                RepairRequestId = requestId,
                Status = "NEEDS_ATTENTION",
                Message = "The module call failed after the step was prepared. Earlier committed work, if any, was not rolled back."
            };
        }

        if (result.Stale)
        {
            await AppendAsync(db, repairCase, 2, "STEP_FAILED", step, scope.UserId, "REPLAN_REQUIRED", result.Message, cancellationToken);
            return new CostingRepairExecutionResult
            {
                Stale = true,
                RepairRequestId = requestId,
                Status = "REPLAN_REQUIRED",
                Message = "The document changed after the preview. No module action was executed."
            };
        }

        if (!result.Succeeded)
        {
            await AppendAsync(db, repairCase, 2, "STEP_FAILED", step, scope.UserId, "NEEDS_ATTENTION", result.Message, cancellationToken);
            return new CostingRepairExecutionResult
            {
                RepairRequestId = requestId,
                Status = "NEEDS_ATTENTION",
                Message = result.Message ?? "The module rejected the repair step."
            };
        }

        await AppendAsync(db, repairCase, 2, "STEP_COMPLETED", step, scope.UserId, "COMPLETED", result.Message, cancellationToken);
        return new CostingRepairExecutionResult
        {
            Succeeded = true,
            RepairRequestId = requestId,
            Status = "COMPLETED",
            Message = "The reversal committed. Rerun costing health before treating the item as clean."
        };
    }

    private static async Task AppendAsync(
        AppDbContext db,
        CostingRepairCase repairCase,
        int sequence,
        string eventType,
        CostingRepairStep step,
        string userId,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        repairCase.Status = status;
        repairCase.CompletedAtUtc = DateTime.UtcNow;
        repairCase.Events.Add(Event(repairCase, sequence, eventType, step, userId, DateTime.UtcNow, message));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static CostingRepairAuditEvent Event(
        CostingRepairCase repairCase,
        int sequence,
        string eventType,
        CostingRepairStep step,
        string userId,
        DateTime createdAt,
        string? message = null) =>
        new()
        {
            RepairCase = repairCase,
            Sequence = sequence,
            EventType = eventType,
            StepNo = 1,
            StableStepId = step.StableStepId,
            Module = step.AdapterName,
            DocumentType = step.OwnerType,
            DocumentNo = step.OwnerDocumentNo,
            DesiredAction = step.Action,
            BeforeStatus = "POSTED",
            EvidenceJson = message is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new { message }),
            CreatedAtUtc = createdAt,
            CreatedBy = userId
        };

    private static string? MenuFor(string ownerType) => ownerType switch
    {
        CostingRepairOwnerTypes.MiscReceipt => MenuCodes.InventoryMiscReceipt,
        CostingRepairOwnerTypes.MiscIssue => MenuCodes.InventoryMiscIssue,
        CostingRepairOwnerTypes.Scrap => MenuCodes.InventoryScrap,
        CostingRepairOwnerTypes.Transfer => MenuCodes.InventoryStockTransfer,
        CostingRepairOwnerTypes.Adjustment => MenuCodes.InventoryStockAdjustment,
        CostingRepairOwnerTypes.CustomerReturn => MenuCodes.InventoryStockReturn,
        CostingRepairOwnerTypes.VendorReturn => MenuCodes.InventoryVendorReturn,
        CostingRepairOwnerTypes.PurchaseGoodsReceipt => MenuCodes.InventoryGoodsReceipt,
        CostingRepairOwnerTypes.PurchaseCreditNote => MenuCodes.PurchaseCreditNote,
        CostingRepairOwnerTypes.SalesInvoice => MenuCodes.SalesInvoice,
        CostingRepairOwnerTypes.SalesDeliveryOrder => MenuCodes.SalesDeliveryOrder,
        CostingRepairOwnerTypes.SalesCreditNote => MenuCodes.SalesCreditNote,
        CostingRepairOwnerTypes.ProductionMaterialIssue => MenuCodes.PlanningMaterialIssue,
        CostingRepairOwnerTypes.ProductionOutput => MenuCodes.PlanningDailyProduction,
        CostingRepairOwnerTypes.ProductionFinishedGood => MenuCodes.PlanningFinishedGoodReceipt,
        _ => null
    };
}
