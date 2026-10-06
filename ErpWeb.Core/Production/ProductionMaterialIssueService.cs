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
    internal static Action? TestHookAfterIssueValuation;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly ICurrentDateService _clock;
    private readonly IProductionMaterialAllocationService _allocation;
    private readonly IRunningNumberService? _runningNumbers;
    private readonly IIvStockPostingRepository? _stockPosting;
    private readonly IIvInventoryPostingService? _inventoryPosting;
    private readonly IProductionOperationEligibilityService _operationEligibility;

    public ProductionMaterialIssueService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICurrentDateService clock,
        IProductionMaterialAllocationService allocation,
        IProductionOperationEligibilityService? operationEligibility = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _clock = clock;
        _allocation = allocation;
        _operationEligibility = operationEligibility ?? new ProductionOperationEligibilityService();
    }

    public ProductionMaterialIssueService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICurrentDateService clock,
        IProductionMaterialAllocationService allocation,
        IRunningNumberService runningNumbers,
        IIvStockPostingRepository stockPosting,
        IIvInventoryPostingService inventoryPosting,
        IProductionOperationEligibilityService? operationEligibility = null)
        : this(dbFactory, tenant, access, clock, allocation, operationEligibility)
    {
        _runningNumbers = runningNumbers;
        _stockPosting = stockPosting;
        _inventoryPosting = inventoryPosting;
    }

    public async Task<IvMasterOperationResult<ProductionMaterialIssueWorkspace>> GetWorkspaceAsync(
        string workOrderNo,
        long? operationId = null,
        CancellationToken cancellationToken = default,
        int? excludeBatchNo = null)
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

        var eligibilityByOperation = order.Operations.ToDictionary(
            x => x.Uid,
            x => _operationEligibility.Evaluate(x, order.RouteSteps.ToList(), order.Operations.ToList()));

        var validatedExcludeBatchNo = await ResolveValidatedExcludeBatchNoAsync(
            db, scope.CompanyCode, scope.BranchCode!, order.Uid, operationId, excludeBatchNo, cancellationToken);
        var basisByOperation = await LoadActiveBasisByOperationAsync(
            db, scope.CompanyCode, scope.BranchCode!, order.Uid, validatedExcludeBatchNo, cancellationToken);

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
                    ProductionMaterialMovementTotals.EffectiveConsumed(
                        g.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty),
                        g.Where(x => x.MovementType == ProductionMaterialMovementTypes.ConsumeReversal).Sum(x => x.Qty))));
        var draftRows = await (from map in db.ProductionMaterialIssueLines.AsNoTracking()
            join link in db.ProductionPostingLinks.AsNoTracking() on map.PostingLinkId equals link.Uid
            join batch in db.IvTrxBatches.AsNoTracking() on map.InventoryBatchId equals batch.Id
            where materialIds.Contains(map.WorkOrderMaterialId)
                && link.Status == ProductionPostingLinkStatuses.Draft && batch.BatchStatus == IvBatchStatuses.New
                && batch.DeletedAtUtc == null
                && (!validatedExcludeBatchNo.HasValue || map.InventoryBatchNo != validatedExcludeBatchNo.Value)
            select new { map.WorkOrderMaterialId, map.IssueQty }).ToListAsync(cancellationToken);

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
                    material.Uid, _clock.Today, cancellationToken, excludeInventoryBatchNo: validatedExcludeBatchNo);
                if (candidates.Succeeded)
                    availableBaseQty = IvQty.Round(candidates.Data!.Sum(x => x.AvailableToAllocateBaseQty));
                else
                {
                    canManualIssue = false;
                    blockingReason = candidates.Message ?? "Eligible stock could not be loaded.";
                }
            }

            var issued = ProductionMaterialExecutionCalc.MovementEffectiveIssue(movement.Issue, movement.IssueReversal);
            var returned = IvQty.Round(movement.Returned);
            var netIssued = ProductionWorkOrderCalc.NetIssuedQty(issued, returned);
            var otherOpenDraft = IvQty.Round(draftRows.Where(x => x.WorkOrderMaterialId == material.Uid).Sum(x => x.IssueQty));
            var maxAllowed = ProductionMaterialExecutionCalc.MaxAllowedNetIssue(material.RequiredQty, material.Tolerance);
            var availableToDraft = IvQty.Round(Math.Max(maxAllowed - netIssued - otherOpenDraft, 0m));
            var outstanding = ProductionMaterialExecutionCalc.Outstanding(material.RequiredQty, issued, returned);
            var availableQty = material.ConversionFactorToBase > 0m
                ? ProductionMaterialExecutionCalc.IssueQtyForBaseQty(availableBaseQty, material.ConversionFactorToBase)
                : 0m;
            operationsById.TryGetValue(material.WorkOrderOperationId ?? 0, out var operation);
            var reservedByOtherDrafts = canManualIssue
                ? IvQty.Round((await _allocation.GetStockCandidatesAsync(material.Uid, _clock.Today, cancellationToken,
                    excludeInventoryBatchNo: validatedExcludeBatchNo)).Data?.Sum(x => x.ReservedOtherDraftBaseQty) ?? 0m)
                : 0m;

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
                MaxAllowedNetIssue = maxAllowed,
                OtherOpenDraftQty = otherOpenDraft,
                StandardRemaining = IvQty.Round(Math.Max(material.RequiredQty - netIssued, 0m)),
                AvailableToDraft = availableToDraft,
                AvailableBaseQty = availableBaseQty,
                AvailableQty = availableQty,
                ShortageQty = IvQty.Round(Math.Max(outstanding - availableQty, 0m)),
                OtherDraftReservedBaseQty = reservedByOtherDrafts,
                AvailableAfterDraftReservations = availableQty,
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
                WorkCentreCode = x.WorkCentreCode,
                PlannedOutputQty = x.PlannedOutputQty,
                PlannedOutputUom = x.PlannedOutputUom,
                StageSequence = x.RouteStepId.HasValue
                    ? order.RouteSteps.Single(step => step.Uid == x.RouteStepId.Value).StageSequence : 0,
                IsSequenceEligible = eligibilityByOperation.GetValueOrDefault(x.Uid)?.IsEligible == true,
                SequenceBlockingReason = eligibilityByOperation.GetValueOrDefault(x.Uid)?.BlockingReason,
                ActualOutputQty = IvQty.Round(x.GoodQty),
                PostedBasisQty = basisByOperation.GetValueOrDefault(x.Uid, OperationBasisTotals.Empty).Posted,
                OpenDraftBasisQty = basisByOperation.GetValueOrDefault(x.Uid, OperationBasisTotals.Empty).OpenDraft,
                RemainingBasisQty = IvQty.Round(Math.Max(x.PlannedOutputQty
                    - basisByOperation.GetValueOrDefault(x.Uid, OperationBasisTotals.Empty).Posted
                    - basisByOperation.GetValueOrDefault(x.Uid, OperationBasisTotals.Empty).OpenDraft, 0m))
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
            IssueDate = _clock.Now,
            RouteSteps = routeSteps,
            Operations = operations,
            Materials = materials,
            CanViewCost = await _access.CanAsync(
                MenuCodes.PlanningMaterialIssue, PermissionCodes.ViewCost, cancellationToken)
        });
    }

    private static async Task<int?> ResolveValidatedExcludeBatchNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderId,
        long? operationId,
        int? excludeBatchNo,
        CancellationToken cancellationToken)
    {
        if (excludeBatchNo is not > 0) return null;
        var link = await db.ProductionPostingLinks.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
                && x.InventoryBatchNo == excludeBatchNo.Value
                && x.WorkOrderId == workOrderId
                && x.Status == ProductionPostingLinkStatuses.Draft)
            .Select(x => new { x.Uid, x.InventoryBatchNo })
            .SingleOrDefaultAsync(cancellationToken);
        if (link is null) return null;
        var batchOk = await db.IvTrxBatches.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == companyCode && x.BranchCode == branchCode
            && x.BatchNo == excludeBatchNo.Value
            && x.TrxType == IvTrxTypes.IssueToProduction
            && x.BatchStatus == IvBatchStatuses.New
            && x.DeletedAtUtc == null, cancellationToken);
        if (!batchOk) return null;
        if (operationId.HasValue)
        {
            var opMatch = await db.ProductionMaterialIssueLines.AsNoTracking()
                .AnyAsync(x => x.PostingLinkId == link.Uid && x.WorkOrderOperationId == operationId.Value, cancellationToken);
            if (!opMatch) return null;
        }
        return excludeBatchNo;
    }

    private static async Task<IReadOnlyDictionary<long, OperationBasisTotals>> LoadActiveBasisByOperationAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderId,
        int? excludeBatchNo,
        CancellationToken cancellationToken)
    {
        var rows = await (from map in db.ProductionMaterialIssueLines.AsNoTracking()
                          join link in db.ProductionPostingLinks.AsNoTracking() on map.PostingLinkId equals link.Uid
                          join batch in db.IvTrxBatches.AsNoTracking() on map.InventoryBatchId equals batch.Id
                          where link.CompanyCode == companyCode
                              && link.BranchCode == branchCode
                              && link.WorkOrderId == workOrderId
                              && link.CommandType == ProductionPostingCommandTypes.MaterialIssuePost
                              && (!excludeBatchNo.HasValue || map.InventoryBatchNo != excludeBatchNo.Value)
                              && ((link.Status == ProductionPostingLinkStatuses.Draft && batch.BatchStatus == IvBatchStatuses.New && batch.DeletedAtUtc == null)
                                  || (link.Status == ProductionPostingLinkStatuses.Succeeded && batch.BatchStatus == IvBatchStatuses.Posted))
                          select new
                          {
                              map.WorkOrderOperationId,
                              link.Uid,
                              link.ProductionQtyThisIssue,
                              IsDraft = link.Status == ProductionPostingLinkStatuses.Draft
                          })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(x => new { x.WorkOrderOperationId, x.Uid, x.IsDraft, x.ProductionQtyThisIssue })
            .GroupBy(x => x.Key.WorkOrderOperationId)
            .ToDictionary(
                x => x.Key,
                x => new OperationBasisTotals(
                    IvQty.Round(x.Where(y => !y.Key.IsDraft).Sum(y => y.Key.ProductionQtyThisIssue ?? 0m)),
                    IvQty.Round(x.Where(y => y.Key.IsDraft).Sum(y => y.Key.ProductionQtyThisIssue ?? 0m))));
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
    private sealed record OperationBasisTotals(decimal Posted, decimal OpenDraft)
    {
        public static readonly OperationBasisTotals Empty = new(0m, 0m);
    }
}
