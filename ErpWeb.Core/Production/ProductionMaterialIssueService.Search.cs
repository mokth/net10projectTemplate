using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public async Task<IvMasterOperationResult<ProductionMaterialIssueFilterOptions>> GetEligibleOperationFilterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionMaterialIssueFilterOptions>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueFilterOptions>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operations = EligibleManualIssueOperations(db, scope.CompanyCode, scope.BranchCode!);
        var workOrderRows = await operations.Select(x => new { x.WorkOrder!.WorkOrderNo, x.WorkOrder.ProductCode, x.WorkOrder.ProductDescription })
            .Distinct().OrderBy(x => x.WorkOrderNo).ToListAsync(cancellationToken);
        var workOrders = workOrderRows.Select(x => new ProductionMaterialIssueFilterChoice(x.WorkOrderNo,
            $"{x.WorkOrderNo} — {x.ProductCode}{(string.IsNullOrWhiteSpace(x.ProductDescription) ? string.Empty : $" · {x.ProductDescription}")}")).ToList();
        var products = await operations.Select(x => x.WorkOrder!.ProductCode).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var workCentres = await operations.Where(x => x.WorkCentreCode != null).Select(x => x.WorkCentreCode!).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var processRows = await operations.Select(x => new { x.OperationCode, x.OperationDescription }).Distinct()
            .OrderBy(x => x.OperationCode).ToListAsync(cancellationToken);
        var processes = processRows.GroupBy(x => x.OperationCode).Select(x => new ProductionMaterialIssueFilterChoice(x.Key,
            $"{x.Key}{(string.IsNullOrWhiteSpace(x.First().OperationDescription) ? string.Empty : $" — {x.First().OperationDescription}")}")).ToList();
        var outputItems = await operations.Select(x => x.RouteStep != null ? x.RouteStep.OutputItemCode : x.WorkOrder!.ProductCode)
            .Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var rawMaterials = await operations.SelectMany(x => x.Materials.Where(m =>
                m.IssueMethod == PrMaterialIssueMethods.Manual
                && (m.SupplySource == PrMaterialSupplySources.Purchased || m.SupplySource == PrMaterialSupplySources.ExternalSupply)
                && m.RequiredQty > 0m
                && m.ConversionFactorToBase > 0m
                && m.RequiredUom != null && m.RequiredUom != ""
                && m.BaseUom != null && m.BaseUom != ""
                && m.WarehouseCode != null && m.WarehouseCode != ""))
            .Select(x => x.ComponentCode).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        var machines = await operations.SelectMany(x => x.Machines.Where(m => m.IsSelected)).Select(x => x.MachineCode).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        return IvMasterOperationResult<ProductionMaterialIssueFilterOptions>.Ok(new()
        {
            WorkOrders = workOrders, Products = products, WorkCentres = workCentres, Processes = processes,
            OutputItems = outputItems, RawMaterials = rawMaterials, Machines = machines
        });
    }

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
        var rows = EligibleManualIssueOperations(db, scope.CompanyCode, scope.BranchCode!);
        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo)) { var v = query.WorkOrderNo.Trim(); rows = query.ExactMatch ? rows.Where(x => x.WorkOrder!.WorkOrderNo == v) : rows.Where(x => x.WorkOrder!.WorkOrderNo.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(query.Product)) { var v = query.Product.Trim(); rows = query.ExactMatch ? rows.Where(x => x.WorkOrder!.ProductCode == v) : rows.Where(x => x.WorkOrder!.ProductCode.Contains(v) || (x.WorkOrder.ProductDescription != null && x.WorkOrder.ProductDescription.Contains(v))); }
        if (!string.IsNullOrWhiteSpace(query.WorkCentre)) { var v = query.WorkCentre.Trim(); rows = query.ExactMatch ? rows.Where(x => x.WorkCentreCode == v) : rows.Where(x => x.WorkCentreCode != null && x.WorkCentreCode.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(query.Process)) { var v = query.Process.Trim(); rows = query.ExactMatch ? rows.Where(x => x.OperationCode == v) : rows.Where(x => x.OperationCode.Contains(v) || (x.OperationDescription != null && x.OperationDescription.Contains(v))); }
        if (!string.IsNullOrWhiteSpace(query.OutputItem)) { var v = query.OutputItem.Trim(); rows = query.ExactMatch ? rows.Where(x => x.RouteStep != null ? x.RouteStep.OutputItemCode == v : x.WorkOrder!.ProductCode == v) : rows.Where(x => (x.RouteStep != null && x.RouteStep.OutputItemCode.Contains(v)) || x.WorkOrder!.ProductCode.Contains(v)); }
        if (!string.IsNullOrWhiteSpace(query.RawMaterial))
        {
            var v = query.RawMaterial.Trim();
            rows = query.ExactMatch
                ? rows.Where(x => x.Materials.Any(m =>
                    m.IssueMethod == PrMaterialIssueMethods.Manual
                    && (m.SupplySource == PrMaterialSupplySources.Purchased || m.SupplySource == PrMaterialSupplySources.ExternalSupply)
                    && m.RequiredQty > 0m && m.ConversionFactorToBase > 0m
                    && m.RequiredUom != null && m.RequiredUom != ""
                    && m.BaseUom != null && m.BaseUom != ""
                    && m.WarehouseCode != null && m.WarehouseCode != ""
                    && m.ComponentCode == v))
                : rows.Where(x => x.Materials.Any(m =>
                    m.IssueMethod == PrMaterialIssueMethods.Manual
                    && (m.SupplySource == PrMaterialSupplySources.Purchased || m.SupplySource == PrMaterialSupplySources.ExternalSupply)
                    && m.RequiredQty > 0m && m.ConversionFactorToBase > 0m
                    && m.RequiredUom != null && m.RequiredUom != ""
                    && m.BaseUom != null && m.BaseUom != ""
                    && m.WarehouseCode != null && m.WarehouseCode != ""
                    && (m.ComponentCode.Contains(v) || (m.ComponentDescription != null && m.ComponentDescription.Contains(v)))));
        }
        if (!string.IsNullOrWhiteSpace(query.Machine)) { var v = query.Machine.Trim(); rows = query.ExactMatch ? rows.Where(x => x.Machines.Any(m => m.IsSelected && m.MachineCode == v)) : rows.Where(x => x.Machines.Any(m => m.IsSelected && m.MachineCode.Contains(v))); }
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
        if (page.Count > 0)
        {
            var operationIds = page.Select(x => x.WorkOrderOperationId).ToArray();
            var materials = await db.ProductionWorkOrderMaterials.AsNoTracking()
                .Where(x => x.WorkOrderOperationId.HasValue && operationIds.Contains(x.WorkOrderOperationId.Value)
                    && x.IssueMethod == PrMaterialIssueMethods.Manual
                    && (x.SupplySource == PrMaterialSupplySources.Purchased || x.SupplySource == PrMaterialSupplySources.ExternalSupply)
                    && x.RequiredQty > 0m && x.ConversionFactorToBase > 0m
                    && x.RequiredUom != null && x.RequiredUom != ""
                    && x.BaseUom != null && x.BaseUom != ""
                    && x.WarehouseCode != null && x.WarehouseCode != "")
                .Select(x => new { OperationId = x.WorkOrderOperationId!.Value, x.ComponentCode })
                .ToListAsync(cancellationToken);
            var rawByOperation = materials.GroupBy(x => x.OperationId).ToDictionary(
                x => x.Key,
                x => string.Join(", ", x.Select(y => y.ComponentCode).Distinct().OrderBy(y => y, StringComparer.Ordinal)));
            foreach (var row in page) row.RawMaterialCodes = rawByOperation.GetValueOrDefault(row.WorkOrderOperationId, string.Empty);
        }
        return IvMasterOperationResult<ProductionMaterialIssueOperationPage>.Ok(new() { Rows = page, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<ProductionMaterialIssueBomPreview>> GetBomPreviewAsync(
        long workOrderOperationId, decimal productionQtyThisIssue, DateTime trxDateTime,
        CancellationToken cancellationToken = default,
        int? excludeBatchNo = null)
    {
        if (productionQtyThisIssue <= 0m)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.Validation, "Desired output quantity must be greater than zero.");
        if (trxDateTime > _clock.Now)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.Validation, "Transaction time cannot be in the future.");
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.Uid == workOrderOperationId && x.WorkOrder != null && x.WorkOrder.CompanyCode == scope.CompanyCode && x.WorkOrder.BranchCode == scope.BranchCode)
            .Select(x => new { x.PlannedOutputQty, x.WorkOrder!.WorkOrderNo }).SingleOrDefaultAsync(cancellationToken);
        if (operation is null || operation.PlannedOutputQty <= 0m)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.NotFound, "Eligible Work Order operation was not found.");
        if (productionQtyThisIssue > operation.PlannedOutputQty)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(IvMasterErrorCode.Validation, "Desired output quantity cannot exceed operation planned output.");
        var workspace = await GetWorkspaceAsync(operation.WorkOrderNo, workOrderOperationId, cancellationToken, excludeBatchNo);
        if (!workspace.Succeeded || workspace.Data is null)
            return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Fail(workspace.ErrorCode, workspace.Message ?? "Unable to load Process BOM.");
        var lines = new List<ProductionMaterialIssueBomPreviewLine>();
        foreach (var material in workspace.Data.Materials)
        {
            var requested = ProductionMaterialExecutionCalc.RequestedForProductionQty(
                material.RequiredQty, operation.PlannedOutputQty, productionQtyThisIssue);
            var desiredMax = ProductionMaterialExecutionCalc.MaxForProductionQty(requested, material.TolerancePercent);
            var maxIssue = IvQty.Round(Math.Min(desiredMax, material.AvailableToDraft));
            var availableForDate = 0m;
            if (material.CanManualIssue && material.ConversionFactorToBase > 0m)
            {
                var candidates = await _allocation.GetStockCandidatesAsync(material.WorkOrderMaterialId, trxDateTime, cancellationToken);
                if (candidates.Succeeded)
                    availableForDate = ProductionMaterialExecutionCalc.IssueQtyForBaseQty(
                        candidates.Data!.Sum(x => x.UsableBaseQty), material.ConversionFactorToBase);
            }
            lines.Add(new ProductionMaterialIssueBomPreviewLine
            {
                WorkOrderMaterialId = material.WorkOrderMaterialId,
                ItemCode = material.ComponentCode,
                RequestedMaterialQty = requested,
                MaxIssueQty = maxIssue,
                AvailableToDraft = material.AvailableToDraft,
                AvailableForIssueDateQty = availableForDate,
                SuggestedIssueQty = IvQty.Round(Math.Max(0m, Math.Min(requested, Math.Min(material.AvailableToDraft, availableForDate)))),
                CanManualIssue = material.CanManualIssue,
                BlockingReason = material.BlockingReason
            });
        }
        return IvMasterOperationResult<ProductionMaterialIssueBomPreview>.Ok(new()
        {
            WorkOrderOperationId = workOrderOperationId,
            OperationPlannedOutputQty = operation.PlannedOutputQty,
            ProductionQtyThisIssue = productionQtyThisIssue,
            Lines = lines
        });
    }

    /// <summary>
    /// EF-translatable base query: released/in-progress ops with PlannedOutputQty &gt; 0 and at least one
    /// snapshot-valid manual warehouse material. Predicate is inlined (not a local method) for SQL translation.
    /// </summary>
    private static IQueryable<ProductionWorkOrderOperation> EligibleManualIssueOperations(
        AppDbContext db, string companyCode, string branchCode) =>
        db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrder != null
                && x.WorkOrder.CompanyCode == companyCode
                && x.WorkOrder.BranchCode == branchCode
                && (x.WorkOrder.Status == ProductionWorkOrderStatuses.Released || x.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && !x.WorkOrder.IsLegacySnapshot
                && x.WorkOrder.SnapshotFormatVersion >= ProductionSnapshotFormatVersions.FullHierarchyV2
                && x.PlannedOutputQty > 0m
                && x.Materials.Any(m =>
                    m.IssueMethod == PrMaterialIssueMethods.Manual
                    && (m.SupplySource == PrMaterialSupplySources.Purchased
                        || m.SupplySource == PrMaterialSupplySources.ExternalSupply)
                    && m.RequiredQty > 0m
                    && m.ConversionFactorToBase > 0m
                    && m.RequiredUom != null && m.RequiredUom != ""
                    && m.BaseUom != null && m.BaseUom != ""
                    && m.WarehouseCode != null && m.WarehouseCode != ""));
}
