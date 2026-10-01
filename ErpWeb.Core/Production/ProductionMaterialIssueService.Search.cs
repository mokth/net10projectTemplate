using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public async Task<IvMasterOperationResult<ProductionMaterialIssueOperationPage>> SearchEligibleOperationsAsync(
        ProductionMaterialIssueOperationQuery query, CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionMaterialIssueOperationPage>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueOperationPage>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        query ??= new ProductionMaterialIssueOperationQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrder != null && x.WorkOrder.CompanyCode == scope.CompanyCode && x.WorkOrder.BranchCode == scope.BranchCode
                && (x.WorkOrder.Status == ProductionWorkOrderStatuses.Released || x.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && !x.WorkOrder.IsLegacySnapshot && x.WorkOrder.SnapshotFormatVersion >= ProductionSnapshotFormatVersions.FullHierarchyV2);
        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo)) { var v = query.WorkOrderNo.Trim(); rows = rows.Where(x => x.WorkOrder!.WorkOrderNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(query.Product)) { var v = query.Product.Trim(); rows = rows.Where(x => x.WorkOrder!.ProductCode.Contains(v) || (x.WorkOrder.ProductDescription != null && x.WorkOrder.ProductDescription.Contains(v))); }
        if (!string.IsNullOrWhiteSpace(query.WorkCentre)) { var v = query.WorkCentre.Trim(); rows = rows.Where(x => x.WorkCentreCode != null && x.WorkCentreCode.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(query.Process)) { var v = query.Process.Trim(); rows = rows.Where(x => x.OperationCode.Contains(v) || (x.OperationDescription != null && x.OperationDescription.Contains(v))); }
        if (!string.IsNullOrWhiteSpace(query.OutputItem)) { var v = query.OutputItem.Trim(); rows = rows.Where(x => (x.RouteStep != null && x.RouteStep.OutputItemCode.Contains(v)) || x.WorkOrder!.ProductCode.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(query.RawMaterial)) { var v = query.RawMaterial.Trim(); rows = rows.Where(x => x.Materials.Any(m => m.ComponentCode.Contains(v) || (m.ComponentDescription != null && m.ComponentDescription.Contains(v)))); }
        if (!string.IsNullOrWhiteSpace(query.Machine)) { var v = query.Machine.Trim(); rows = rows.Where(x => x.Machines.Any(m => m.IsSelected && m.MachineCode.Contains(v))); }
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderBy(x => x.WorkOrder!.WorkOrderNo).ThenBy(x => x.ProcessSequence).ThenBy(x => x.Uid)
            .Skip(Math.Max(query.Skip, 0)).Take(Math.Clamp(query.Take, 1, 100))
            .Select(x => new ProductionMaterialIssueOperationRow
            {
                WorkOrderOperationId = x.Uid, WorkOrderNo = x.WorkOrder!.WorkOrderNo,
                ProductCode = x.WorkOrder.ProductCode, ProductDescription = x.WorkOrder.ProductDescription,
                WorkCentreCode = x.WorkCentreCode, OperationCode = x.OperationCode, OperationDescription = x.OperationDescription,
                OutputItemCode = x.RouteStep != null ? x.RouteStep.OutputItemCode : x.WorkOrder.ProductCode,
                SelectedMachineCode = x.Machines.Where(m => m.IsSelected).Select(m => m.MachineCode).FirstOrDefault(),
                PlannedOutputQty = x.PlannedOutputQty, PlannedOutputUom = x.PlannedOutputUom
            }).ToListAsync(cancellationToken);
        return IvMasterOperationResult<ProductionMaterialIssueOperationPage>.Ok(new() { Rows = page, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<ProductionMaterialIssueBomPreview>> GetBomPreviewAsync(
        long workOrderOperationId, decimal productionQtyThisIssue, DateTime trxDateTime,
        CancellationToken cancellationToken = default)
    {
        if (productionQtyThisIssue < 0m || trxDateTime > _clock.Now)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.Validation, "Production quantity must be non-negative and transaction time cannot be in the future.");
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.Uid == workOrderOperationId && x.WorkOrder != null && x.WorkOrder.CompanyCode == scope.CompanyCode && x.WorkOrder.BranchCode == scope.BranchCode)
            .Select(x => new { x.PlannedOutputQty, x.WorkOrder!.WorkOrderNo }).SingleOrDefaultAsync(cancellationToken);
        if (operation is null || operation.PlannedOutputQty <= 0m)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.NotFound, "Eligible Work Order operation was not found.");
        var workspace = await GetWorkspaceAsync(operation.WorkOrderNo, workOrderOperationId, cancellationToken);
        if (!workspace.Succeeded || workspace.Data is null)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(workspace.ErrorCode, workspace.Message ?? "Unable to load Process BOM.");
        var ratio = productionQtyThisIssue / operation.PlannedOutputQty;
        var lines = new List<ProductionMaterialIssueBomPreviewLine>();
        foreach (var material in workspace.Data.Materials)
        {
            var requested = IvQty.Round(material.RequiredQty * ratio);
            var available = 0m;
            if (material.CanManualIssue)
            {
                var candidates = await _allocation.GetStockCandidatesAsync(material.WorkOrderMaterialId, trxDateTime, cancellationToken);
                if (candidates.Succeeded)
                    available = ProductionMaterialExecutionCalc.IssueQtyForBaseQty(candidates.Data!.Sum(x => x.UsableBaseQty), material.ConversionFactorToBase);
            }
            lines.Add(new ProductionMaterialIssueBomPreviewLine { WorkOrderMaterialId = material.WorkOrderMaterialId,
                ItemCode = material.ComponentCode, RequestedMaterialQty = requested, AvailableToDraft = material.AvailableToDraft,
                SuggestedIssueQty = IvQty.Round(Math.Max(0m, Math.Min(requested, Math.Min(material.AvailableToDraft, available)))),
                CanManualIssue = material.CanManualIssue, BlockingReason = material.BlockingReason });
        }
        return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Ok(new() { WorkOrderOperationId = workOrderOperationId,
            OperationPlannedOutputQty = operation.PlannedOutputQty, ProductionQtyThisIssue = productionQtyThisIssue, Lines = lines });
    }
}
