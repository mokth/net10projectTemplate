using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService : IProductionMaterialIssueService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly ICurrentDateService _clock;
    private readonly IProductionMaterialAllocationService _allocation;
    private readonly IRunningNumberService? _runningNumbers;
    private readonly IIvStockPostingRepository? _stockPosting;
    private readonly IIvInventoryPostingService? _inventoryPosting;

    public ProductionMaterialIssueService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICurrentDateService clock,
        IProductionMaterialAllocationService allocation)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _clock = clock;
        _allocation = allocation;
    }

    public ProductionMaterialIssueService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICurrentDateService clock,
        IProductionMaterialAllocationService allocation,
        IRunningNumberService runningNumbers,
        IIvStockPostingRepository stockPosting,
        IIvInventoryPostingService inventoryPosting)
        : this(dbFactory, tenant, access, clock, allocation)
    {
        _runningNumbers = runningNumbers;
        _stockPosting = stockPosting;
        _inventoryPosting = inventoryPosting;
    }

    public async Task<IvMasterOperationResult<ProductionMaterialIssueWorkspace>> GetWorkspaceAsync(
        string workOrderNo,
        long? operationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken))
            return Fail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");
        var normalizedWorkOrderNo = (workOrderNo ?? string.Empty).Trim();
        if (normalizedWorkOrderNo.Length == 0)
            return Fail(IvMasterErrorCode.Validation, "Work Order number is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var order = await db.ProductionWorkOrders
            .AsNoTracking()
            .Include(x => x.RouteSteps)
            .Include(x => x.Operations)
            .Include(x => x.Materials)
            .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderNo == normalizedWorkOrderNo, cancellationToken);

        if (order is null)
            return Fail(IvMasterErrorCode.NotFound, "Work Order was not found.");
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return Fail(IvMasterErrorCode.Validation, "Materials can be issued only for released or in-progress Work Orders.");
        if (!ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot)
            return Fail(IvMasterErrorCode.Validation, "The Work Order must have a current, non-legacy snapshot before material issue.");
        if (operationId.HasValue && order.Operations.All(x => x.Uid != operationId.Value))
            return Fail(IvMasterErrorCode.NotFound, "Work Order operation was not found.");

        var materialEntities = order.Materials
            .Where(x => !operationId.HasValue || x.WorkOrderOperationId == operationId.Value)
            .OrderBy(x => x.WorkOrderOperationId)
            .ThenBy(x => x.MaterialSequence)
            .ThenBy(x => x.Uid)
            .ToList();
        var materialIds = materialEntities.Select(x => x.Uid).ToList();

        // Fetch facts and aggregate in memory so the read contract is provider-independent; SQLite
        // does not support decimal SUM, while SQL Server does.
        var movements = await db.ProductionMaterialMovements
            .AsNoTracking()
            .Where(x => materialIds.Contains(x.WorkOrderMaterialId))
            .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.Qty })
            .ToListAsync(cancellationToken);
        var totals = movements
            .GroupBy(x => x.WorkOrderMaterialId)
            .ToDictionary(
                g => g.Key,
                g => new MovementTotals(
                    g.Where(x => x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty),
                    g.Where(x => x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty),
                    g.Where(x => x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty),
                    g.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty)));

        var itemCodes = materialEntities.Select(x => x.ComponentCode).Distinct().ToList();
        var stockMasters = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && itemCodes.Contains(x.ICode))
            .Select(x => new { x.ICode, x.IsActive, x.StockControl, x.LotControl })
            .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var operationsById = order.Operations.ToDictionary(x => x.Uid);
        var materials = new List<ProductionMaterialIssueMaterial>(materialEntities.Count);
        foreach (var material in materialEntities)
        {
            totals.TryGetValue(material.Uid, out var movement);
            movement ??= new MovementTotals(0m, 0m, 0m, 0m);
            stockMasters.TryGetValue(material.ComponentCode, out var stock);
            var blockingReason = BlockingReason(material, stock?.IsActive == true, stock?.StockControl == true);
            var canManualIssue = blockingReason is null;
            var availableBaseQty = 0m;

            if (canManualIssue)
            {
                var candidates = await _allocation.GetStockCandidatesAsync(
                    material.Uid, _clock.Today, cancellationToken);
                if (candidates.Succeeded)
                    availableBaseQty = IvQty.Round(candidates.Data!.Sum(x => x.AvailableBaseQty));
                else
                {
                    canManualIssue = false;
                    blockingReason = candidates.Message ?? "Eligible stock could not be loaded.";
                }
            }

            var issued = ProductionMaterialExecutionCalc.MovementEffectiveIssue(movement.Issue, movement.IssueReversal);
            var returned = IvQty.Round(movement.Returned);
            var netIssued = ProductionWorkOrderCalc.NetIssuedQty(issued, returned);
            var outstanding = ProductionMaterialExecutionCalc.Outstanding(material.RequiredQty, issued, returned);
            var availableQty = material.ConversionFactorToBase > 0m
                ? ProductionMaterialExecutionCalc.IssueQtyForBaseQty(availableBaseQty, material.ConversionFactorToBase)
                : 0m;
            operationsById.TryGetValue(material.WorkOrderOperationId ?? 0, out var operation);

            materials.Add(new ProductionMaterialIssueMaterial
            {
                WorkOrderMaterialId = material.Uid,
                WorkOrderOperationId = material.WorkOrderOperationId ?? 0,
                OperationCode = operation?.OperationCode ?? string.Empty,
                WorkCentreCode = operation?.WorkCentreCode,
                ComponentCode = material.ComponentCode,
                Description = material.ComponentDescription,
                IssueMethod = material.IssueMethod,
                SupplySource = material.SupplySource,
                RequiredQty = IvQty.Round(material.RequiredQty),
                RequiredUom = material.RequiredUom ?? string.Empty,
                RequiredBaseQty = IvQty.Round(material.RequiredBaseQty),
                BaseUom = material.BaseUom ?? string.Empty,
                ConversionFactorToBase = material.ConversionFactorToBase,
                IssuedQty = issued,
                ReturnedQty = returned,
                NetIssuedQty = netIssued,
                ConsumedQty = IvQty.Round(movement.Consumed),
                OutstandingQty = outstanding,
                TolerancePercent = material.Tolerance,
                MaxAllowedNetIssue = ProductionMaterialExecutionCalc.MaxAllowedNetIssue(material.RequiredQty, material.Tolerance),
                AvailableBaseQty = availableBaseQty,
                AvailableQty = availableQty,
                ShortageQty = IvQty.Round(Math.Max(outstanding - availableQty, 0m)),
                WarehouseCode = material.WarehouseCode,
                LocationCode = material.LocationCode,
                LotControl = stock?.LotControl == true,
                CanManualIssue = canManualIssue,
                BlockingReason = blockingReason
            });
        }

        var selectedOperationIds = operationId.HasValue
            ? new HashSet<long> { operationId.Value }
            : order.Operations.Select(x => x.Uid).ToHashSet();
        var operations = order.Operations
            .Where(x => selectedOperationIds.Contains(x.Uid))
            .OrderBy(x => x.ProcessSequence).ThenBy(x => x.Uid)
            .Select(x => new ProductionMaterialIssueOperation
            {
                WorkOrderOperationId = x.Uid,
                RouteStepId = x.RouteStepId,
                ProcessSequence = x.ProcessSequence,
                OperationCode = x.OperationCode,
                OperationDescription = x.OperationDescription,
                WorkCentreCode = x.WorkCentreCode
            }).ToList();
        var routeIds = operations.Where(x => x.RouteStepId.HasValue).Select(x => x.RouteStepId!.Value).ToHashSet();
        var routeSteps = order.RouteSteps
            .Where(x => !operationId.HasValue || routeIds.Contains(x.Uid))
            .OrderBy(x => x.StageSequence).ThenBy(x => x.Uid)
            .Select(x => new ProductionMaterialIssueRouteStep
            {
                RouteStepId = x.Uid,
                StageSequence = x.StageSequence,
                WorkCentreCode = x.WorkCentreCode,
                WorkCentreDescription = x.WorkCentreDescription
            }).ToList();

        return IvMasterOperationResult<ProductionMaterialIssueWorkspace>.Ok(new ProductionMaterialIssueWorkspace
        {
            WorkOrderId = order.Uid,
            WorkOrderNo = order.WorkOrderNo,
            Status = order.Status,
            ProductCode = order.ProductCode,
            ProductDescription = order.ProductDescription,
            PlannedQty = order.PlannedQty,
            OutputUom = order.OutputUom,
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            IssueDate = _clock.Today,
            RouteSteps = routeSteps,
            Operations = operations,
            Materials = materials,
            CanViewCost = await _access.CanAsync(
                MenuCodes.PlanningMaterialIssue, PermissionCodes.ViewCost, cancellationToken)
        });
    }

    private static string? BlockingReason(
        ProductionWorkOrderMaterial material,
        bool stockActive,
        bool stockControlled)
    {
        if (material.WorkOrderOperationId is null) return "The material has no consuming Work Order operation.";
        if (!string.Equals(material.IssueMethod, PrMaterialIssueMethods.Manual, StringComparison.OrdinalIgnoreCase))
            return material.IssueMethod switch
            {
                PrMaterialIssueMethods.Backflush => "Backflush — issued automatically by production output.",
                PrMaterialIssueMethods.PickList => "Pick List — execution not enabled yet.",
                _ => "The material has an unsupported issue method."
            };
        if (material.SupplySource == PrMaterialSupplySources.InternalRouteWip)
            return "Supplied by internal route/WIP.";
        if (material.SupplySource == PrMaterialSupplySources.SeparateProductDefinition)
            return "Supplied by separate production definition/work order.";
        if (material.SupplySource is not (PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply))
            return "The material has an unsupported supply source.";
        if (string.IsNullOrWhiteSpace(material.WarehouseCode)) return "The Work Order material has no source warehouse.";
        if (string.IsNullOrWhiteSpace(material.RequiredUom) || string.IsNullOrWhiteSpace(material.BaseUom)
            || material.ConversionFactorToBase <= 0m)
            return "The Work Order material has an invalid UOM chain.";
        if (!stockActive) return "The inventory item is missing or inactive.";
        if (!stockControlled) return "The inventory item is not stock-controlled.";
        return null;
    }

    private static IvMasterOperationResult<ProductionMaterialIssueWorkspace> Fail(
        IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<ProductionMaterialIssueWorkspace>.Fail(code, message);

    private sealed record MovementTotals(decimal Issue, decimal IssueReversal, decimal Returned, decimal Consumed);
}
