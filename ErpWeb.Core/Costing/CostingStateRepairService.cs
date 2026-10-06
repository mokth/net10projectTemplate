using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class CostingStateRepairService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly IBranchStockTransactionLock _branchLock;

    public CostingStateRepairService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        IBranchStockTransactionLock branchLock)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _branchLock = branchLock;
    }

    public async Task<CostingRepairExecutionResult> ExecuteAsync(
        string itemCode,
        string costMethod,
        string previewHash,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken)
            || !await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.RepairCost, cancellationToken)
            || !await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.ViewCost, cancellationToken))
            return new CostingRepairExecutionResult { Status = "DENIED", Message = "Not authorized to rebuild cost state." };
        if (string.IsNullOrWhiteSpace(reason))
            return new CostingRepairExecutionResult { Status = "DENIED", Message = "A repair reason is required." };

        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return new CostingRepairExecutionResult { Status = "DENIED", Message = "A trusted company and branch are required." };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode, cancellationToken);
            var decision = await CostingStateRepairEvaluator.EvaluateAsync(
                db, scope.CompanyCode, scope.BranchCode, itemCode, costMethod, null, cancellationToken);
            if (!string.Equals(decision.PreviewHash, previewHash, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                return new CostingRepairExecutionResult
                {
                    Stale = true,
                    Status = "REPLAN_REQUIRED",
                    Message = "The preview is stale. Build a new plan before executing."
                };
            }

            if (!decision.CanRepair)
            {
                await tx.RollbackAsync(cancellationToken);
                return new CostingRepairExecutionResult
                {
                    Status = "PREVIEW_ONLY",
                    Message = decision.BlockingReason ?? "Cost-state rebuild is blocked."
                };
            }

            if (decision.StateMissing && decision.ExpectedQty == 0m && decision.ExpectedValue == 0m)
            {
                await tx.RollbackAsync(cancellationToken);
                return new CostingRepairExecutionResult { Status = "PREVIEW_ONLY", Message = "A zero cost state is not created." };
            }

            var state = await db.StockCostStates
                .FirstOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.ItemCode == decision.ItemCode
                    && x.CostMethod == decision.CostMethod, cancellationToken);
            var beforeQty = state?.OnHandBaseQty ?? 0m;
            var beforeValue = state?.InventoryValue ?? 0m;
            var beforeAverage = state?.AverageUnitCost ?? 0m;
            if (state is null)
            {
                state = new StockCostState
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode,
                    ItemCode = decision.ItemCode,
                    CostMethod = decision.CostMethod
                };
                db.StockCostStates.Add(state);
            }

            var qty = StockLedgerPrecision.Quantity(decision.ExpectedQty);
            var value = StockLedgerPrecision.Money(decision.ExpectedValue);
            var average = qty == 0m ? 0m : StockLedgerPrecision.Money(value / qty);
            state.OnHandBaseQty = qty;
            state.InventoryValue = value;
            state.AverageUnitCost = average;
            state.CurrentUnitCost = average;
            state.LastValuationFactId = decision.LatestFactId;
            state.LastPostingSequence = decision.LatestPostingSequence;

            var now = DateTime.UtcNow;
            var requestId = Guid.NewGuid();
            var evidence = JsonSerializer.Serialize(new
            {
                decision.ItemCode,
                decision.CostMethod,
                Before = new { Qty = beforeQty, Value = beforeValue, Average = beforeAverage },
                Expected = new { Qty = qty, Value = value, Average = average },
                SourcePostingWatermark = decision.LatestPostingSequence,
                ActiveLedgerEpochId = decision.ActiveEpochId
            });
            var repairCase = new CostingRepairCase
            {
                RepairRequestId = requestId,
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                RootFindingCode = decision.FindingCode,
                RootStockPostingId = null,
                Strategy = "REBUILD_COST_STATE",
                PreviewHash = decision.PreviewHash,
                Status = "COMPLETED",
                RequestedBy = scope.UserId,
                RequestedAtUtc = now,
                StartedAtUtc = now,
                CompletedAtUtc = now,
                Reason = reason.Trim()
            };
            repairCase.Events.Add(new CostingRepairAuditEvent
            {
                Sequence = 1,
                EventType = "STEP_COMPLETED",
                StepNo = 1,
                StableStepId = $"state-{decision.ItemCode}",
                Module = "COST_STATE",
                DocumentType = "COST_STATE",
                DocumentNo = decision.ItemCode,
                DesiredAction = CostingRepairActions.RebuildCostState,
                EvidenceJson = evidence,
                PostingWatermark = decision.LatestPostingSequence,
                CreatedAtUtc = now,
                CreatedBy = scope.UserId
            });
            db.CostingRepairCases.Add(repairCase);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new CostingRepairExecutionResult
            {
                Succeeded = true,
                RepairRequestId = requestId,
                Status = "COMPLETED",
                Message = "The derived cost state was rebuilt from sealed evidence. Costing health has been left for the page to recheck."
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return new CostingRepairExecutionResult
            {
                Stale = true,
                Status = "REPLAN_REQUIRED",
                Message = "The cost state changed after the preview. No update was written."
            };
        }
    }
}
