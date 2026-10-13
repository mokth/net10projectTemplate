using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderTransferService
{
    private static IQueryable<ProductionBalLot> SourceStructuralCandidatePools(
        AppDbContext db, InventoryTenantScope scope, bool excludeReserved)
    {
        var pools = db.ProductionBalLots.AsNoTracking().Where(pool =>
            pool.CompanyCode == scope.CompanyCode && pool.BranchCode == scope.BranchCode
            && pool.Kind == ProductionBalLotKinds.Wip && pool.BalanceStage == "PROCESS_WIP"
            && pool.OutputType == PrRouteOutputTypes.WipStocked
            && pool.Qty > 0m && pool.BaseQty > 0m
            && pool.Uom != null && pool.Uom.Trim() != ""
            && pool.BaseUom != null && pool.BaseUom.Trim() != ""
            && pool.ConversionFactorToBase > 0m && pool.StockStatusCode == "AVAILABLE"
            && pool.OriginType == "PRODUCED" && pool.WorkOrderMaterialId == null
            && pool.OriginalIssueMovementId == null && pool.ProductionLocationId != null
            && pool.LotNo.Trim() != ""
            && (pool.PhysicalLotNo == null || pool.PhysicalLotNo.Trim() == "" || pool.PhysicalLotNo == pool.LotNo)
            && pool.ProducingRouteStepId != null && pool.WorkOrderOperationId != null
            && db.ProductionWorkOrders.Any(order => order.Uid == pool.WorkOrderId
                && order.CompanyCode == scope.CompanyCode && order.BranchCode == scope.BranchCode
                && (order.Status == ProductionWorkOrderStatuses.Released
                    || order.Status == ProductionWorkOrderStatuses.InProgress
                    || order.Status == ProductionWorkOrderStatuses.Completed)
                && order.SnapshotRevision > 0 && order.SnapshotHash != null && order.SnapshotHash.Length == 64
                && order.SnapshotFormatVersion >= ProductionSnapshotFormatVersions.FullHierarchyV2
                && !order.IsLegacySnapshot
                && order.SnapshotHashVersion >= ProductionSnapshotHashVersions.RouteOutputContractV3)
            && db.ProductionWorkOrderRouteSteps.Any(route => route.Uid == pool.ProducingRouteStepId
                && route.WorkOrderId == pool.WorkOrderId
                && route.OutputType == PrRouteOutputTypes.WipStocked
                && route.OutputItemCode == pool.ItemCode && route.OutputUom == pool.Uom
                && route.OutputBaseUom == pool.BaseUom
                && route.OutputConversionFactorToBase == pool.ConversionFactorToBase
                && db.ProductionWorkOrderOperations.Any(operation => operation.Uid == pool.WorkOrderOperationId
                    && operation.RouteStepId == route.Uid && operation.IsFinalOperation)));

        if (excludeReserved)
        {
            pools = pools.Where(pool => !db.ProductionWorkOrderTransfers.Any(transfer =>
                transfer.CompanyCode == scope.CompanyCode && transfer.BranchCode == scope.BranchCode
                && transfer.SourceProductionBalLotId == pool.Uid
                && transfer.Status == ProductionWorkOrderTransferStatuses.New
                && transfer.DeletedAtUtc == null));
        }

        return pools;
    }

    private static IQueryable<ProductionWorkOrderMaterial> TargetConsumerRequirements(
        AppDbContext db, InventoryTenantScope scope, SourceFacts source)
    {
        return db.ProductionWorkOrderMaterials.AsNoTracking().Where(material =>
            material.WorkOrderId != source.Order.Uid
            && material.ComponentCode == source.Lot.ItemCode
            && material.SupplySource == PrMaterialSupplySources.InternalRouteWip
            && material.ProducingRouteStepId != null && material.WorkOrderOperationId != null
            && material.BaseUom == source.Lot.BaseUom
            && material.RequiredUom != null && material.RequiredUom.Trim() != ""
            && material.ConversionFactorToBase > 0m
            && material.RequiredQty >= 0m && material.RequiredBaseQty >= 0m
            // SQL Server translates Math.Round(decimal, int) to ROUND. The deep target validator
            // repeats the exact IvQty contract after paging, including both conversion directions.
            && Math.Round(material.RequiredQty * material.ConversionFactorToBase, IvQty.Scale)
                == Math.Round(material.RequiredBaseQty, IvQty.Scale)
            && Math.Round(material.RequiredBaseQty / material.ConversionFactorToBase, IvQty.Scale)
                == Math.Round(material.RequiredQty, IvQty.Scale)
            && db.ProductionWorkOrders.Any(order => order.Uid == material.WorkOrderId
                && order.CompanyCode == scope.CompanyCode && order.BranchCode == scope.BranchCode
                && order.Uid != source.Order.Uid
                && (order.Status == ProductionWorkOrderStatuses.Released
                    || order.Status == ProductionWorkOrderStatuses.InProgress)
                && order.SnapshotRevision > 0 && order.SnapshotHash != null && order.SnapshotHash.Length == 64
                && order.SnapshotFormatVersion >= ProductionSnapshotFormatVersions.FullHierarchyV2
                && !order.IsLegacySnapshot
                && order.SnapshotHashVersion >= ProductionSnapshotHashVersions.RouteOutputContractV3)
            && db.ProductionWorkOrderOperations.Any(operation => operation.Uid == material.WorkOrderOperationId
                && operation.RouteStepId != null
                && (operation.WorkOrderId == null || operation.WorkOrderId == material.WorkOrderId)
                && db.ProductionWorkOrderRouteSteps.Any(route => route.Uid == operation.RouteStepId
                    && route.WorkOrderId == material.WorkOrderId)));
    }

    private static IQueryable<ProductionWorkOrderMaterial> TargetStructuralRequirements(
        AppDbContext db, InventoryTenantScope scope, SourceFacts source)
    {
        var requirements = TargetConsumerRequirements(db, scope, source);
        return requirements.Where(material =>
            db.ProductionWorkOrderRouteSteps.Any(route => route.Uid == material.ProducingRouteStepId
                && route.WorkOrderId == material.WorkOrderId
                && route.OutputType == PrRouteOutputTypes.WipStocked
                && route.OutputItemCode == source.Lot.ItemCode
                && route.OutputUom == source.Lot.Uom
                && route.OutputBaseUom == source.Lot.BaseUom
                && route.OutputConversionFactorToBase == source.Lot.ConversionFactorToBase
                && db.ProductionWorkOrderOperations.Count(operation =>
                    operation.RouteStepId == route.Uid && operation.IsFinalOperation) == 1));
    }

    private static ProductionStockWorkOrderLookupRow MapTransferLookupRow(ProductionWorkOrder order, int count) => new()
    {
        WorkOrderId = order.Uid,
        WorkOrderNo = order.WorkOrderNo,
        ProductCode = order.ProductCode,
        ProductDescription = order.ProductDescription,
        Status = order.Status,
        PlannedQty = order.PlannedQty,
        RemainingQty = order.RemainingQty,
        OutputUom = order.OutputUom,
        SnapshotRevision = order.SnapshotRevision,
        SnapshotHash = order.SnapshotHash,
        CandidateSourceCount = count
    };

    private static bool HasCurrentTransferSnapshot(ProductionWorkOrder order) =>
        order.SnapshotRevision > 0 && !string.IsNullOrWhiteSpace(order.SnapshotHash)
        && order.SnapshotHash.Length == 64
        && ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion)
        && !order.IsLegacySnapshot
        && ProductionSnapshotHashVersions.SupportsDailyProduction(order.SnapshotHashVersion);

    private async Task<SourceFacts> LoadSourceLookupFactsAsync(AppDbContext db,
        InventoryTenantScope scope, long sourceProductionBalLotId, CancellationToken ct)
    {
        // The WIP picker already selected a row from SearchEligibleSourcesAsync. Recheck the
        // source identity and structural contract here, but leave valuation/contribution traversal
        // to the exact source and target requirement lookup paths.
        var lot = await LoadSourceLotAsync(db, scope, sourceProductionBalLotId, false, ct);
        if (lot.Kind != ProductionBalLotKinds.Wip || lot.BalanceStage != "PROCESS_WIP"
            || lot.OutputType != PrRouteOutputTypes.WipStocked
            || lot.ProducingRouteStepId is not long routeId || lot.WorkOrderOperationId is not long operationId
            || lot.Qty <= 0m || lot.BaseQty <= 0m || string.IsNullOrWhiteSpace(lot.BaseUom)
            || string.IsNullOrWhiteSpace(lot.Uom) || lot.ConversionFactorToBase <= 0m
            || lot.StockStatusCode != "AVAILABLE" || lot.OriginType != "PRODUCED"
            || lot.WorkOrderMaterialId is not null || lot.OriginalIssueMovementId is not null
            || lot.ProductionLocationId is null || string.IsNullOrWhiteSpace(lot.LotNo)
            || IvQty.Round(lot.BaseQty / lot.ConversionFactorToBase) != IvQty.Round(lot.Qty)
            || !string.IsNullOrWhiteSpace(lot.PhysicalLotNo) && lot.PhysicalLotNo != lot.LotNo)
            throw new TransferException("The selected balance is not structurally eligible stockable PROCESS_WIP.");

        var order = lot.WorkOrder ?? await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Uid == lot.WorkOrderId && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct)
            ?? throw new TransferException("Source Work Order was not found.", IvMasterErrorCode.NotFound);
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress
            or ProductionWorkOrderStatuses.Completed))
            throw new TransferException($"Source Work Order is {order.Status} and cannot be used for WOT.");
        if (!HasCurrentTransferSnapshot(order))
            throw new TransferException("Source Work Order does not have a current Production snapshot.");

        var route = lot.ProducingRouteStep ?? await db.ProductionWorkOrderRouteSteps.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == routeId, ct)
            ?? throw new TransferException("Source WIP producer route is missing.");
        var operation = lot.WorkOrderOperation ?? await db.ProductionWorkOrderOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == operationId, ct)
            ?? throw new TransferException("Source WIP producer operation is missing.");
        if (route.WorkOrderId != lot.WorkOrderId || operation.RouteStepId != routeId
            || operation.Uid != operationId || !operation.IsFinalOperation
            || route.OutputType != PrRouteOutputTypes.WipStocked || route.OutputItemCode != lot.ItemCode
            || route.OutputUom != lot.Uom || route.OutputBaseUom != lot.BaseUom
            || route.OutputConversionFactorToBase != lot.ConversionFactorToBase)
            throw new TransferException("Source WIP producer route and operation do not match the live balance.");

        return new(lot, order, route, operation);
    }

    public async Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchSourceWorkOrdersAsync(
        LargeLookupSearchRequest request, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var pools = SourceStructuralCandidatePools(db, scope, excludeReserved: true);
            var orders = db.ProductionWorkOrders.AsNoTracking().Where(order =>
                pools.Any(pool => pool.WorkOrderId == order.Uid));
            if (!string.IsNullOrWhiteSpace(request.SearchText))
            {
                var term = request.SearchText.Trim();
                orders = orders.Where(order => order.WorkOrderNo.Contains(term)
                    || order.ProductCode.Contains(term)
                    || (order.ProductDescription != null && order.ProductDescription.Contains(term)));
            }

            var total = await orders.CountAsync(ct);
            var rows = await orders.OrderBy(order => order.WorkOrderNo).ThenBy(order => order.Uid)
                .Skip(request.NormalizedSkip).Take(request.NormalizedTake)
                .Select(order => new
                {
                    Order = order,
                    CandidateCount = pools.Count(pool => pool.WorkOrderId == order.Uid)
                }).ToListAsync(ct);
            return LargeLookupPage<ProductionStockWorkOrderLookupRow>.Ok(
                rows.Select(row => MapTransferLookupRow(row.Order, row.CandidateCount)).ToArray(), total);
        }
        catch (TransferException ex)
        {
            return LargeLookupPage<ProductionStockWorkOrderLookupRow>.Fail(ex.Message);
        }
    }

    public async Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveSourceWorkOrderAsync(
        string workOrderNo, CancellationToken ct = default)
    {
        var code = workOrderNo?.Trim() ?? "";
        if (code.Length == 0)
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail("Work Order number is required.");

        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var order = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.WorkOrderNo == code, ct);
            if (order is null)
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail($"Work Order '{code}' was not found.");
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress
                or ProductionWorkOrderStatuses.Completed))
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' is {order.Status} and cannot be used as a WOT source.");
            if (!HasCurrentTransferSnapshot(order))
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' does not have a current Production snapshot.");

            var structuralCount = await SourceStructuralCandidatePools(db, scope, excludeReserved: false)
                .CountAsync(pool => pool.WorkOrderId == order.Uid, ct);
            if (structuralCount == 0)
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' has no remaining stockable PROCESS_WIP.");

            var unreservedCount = await SourceStructuralCandidatePools(db, scope, excludeReserved: true)
                .CountAsync(pool => pool.WorkOrderId == order.Uid, ct);
            if (unreservedCount == 0)
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' has no unreserved WIP available for a new transfer. One or more candidate WIP balances already belong to NEW Work Order Transfer drafts.");

            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Ok(
                MapTransferLookupRow(order, unreservedCount));
        }
        catch (TransferException ex)
        {
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(ex.Message);
        }
    }

    public async Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchTargetWorkOrdersAsync(
        long sourceProductionBalLotId, LargeLookupSearchRequest request, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var source = await LoadSourceLookupFactsAsync(db, scope, sourceProductionBalLotId, ct);
            var requirements = TargetStructuralRequirements(db, scope, source);
            var orders = db.ProductionWorkOrders.AsNoTracking().Where(order =>
                requirements.Any(material => material.WorkOrderId == order.Uid));
            if (!string.IsNullOrWhiteSpace(request.SearchText))
            {
                var term = request.SearchText.Trim();
                orders = orders.Where(order => order.WorkOrderNo.Contains(term)
                    || order.ProductCode.Contains(term)
                    || (order.ProductDescription != null && order.ProductDescription.Contains(term)));
            }

            var total = await orders.CountAsync(ct);
            var rows = await orders.OrderBy(order => order.WorkOrderNo).ThenBy(order => order.Uid)
                .Skip(request.NormalizedSkip).Take(request.NormalizedTake)
                .Select(order => new
                {
                    Order = order,
                    CandidateCount = requirements.Count(material => material.WorkOrderId == order.Uid)
                }).ToListAsync(ct);
            return LargeLookupPage<ProductionStockWorkOrderLookupRow>.Ok(
                rows.Select(row => MapTransferLookupRow(row.Order, row.CandidateCount)).ToArray(), total);
        }
        catch (TransferException ex)
        {
            return LargeLookupPage<ProductionStockWorkOrderLookupRow>.Fail(ex.Message);
        }
    }

    public async Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveTargetWorkOrderAsync(
        long sourceProductionBalLotId, string workOrderNo, CancellationToken ct = default)
    {
        var code = workOrderNo?.Trim() ?? "";
        if (code.Length == 0)
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail("Work Order number is required.");

        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var source = await LoadSourceLookupFactsAsync(db, scope, sourceProductionBalLotId, ct);
            var order = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.WorkOrderNo == code, ct);
            if (order is null)
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail($"Work Order '{code}' was not found.");
            if (order.Uid == source.Order.Uid)
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' is the source Work Order and cannot receive its own WIP.");
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' is {order.Status} and cannot receive a WOT.");
            if (!HasCurrentTransferSnapshot(order))
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' does not have a current Production snapshot.");

            var basicCount = await TargetConsumerRequirements(db, scope, source)
                .CountAsync(material => material.WorkOrderId == order.Uid, ct);
            if (basicCount == 0)
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' does not have a compatible INTERNAL_ROUTE_WIP requirement for {source.Lot.ItemCode}.");

            var structuralCount = await TargetStructuralRequirements(db, scope, source)
                .CountAsync(material => material.WorkOrderId == order.Uid, ct);
            if (structuralCount == 0)
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(
                    $"Work Order '{code}' does not have a compatible producer route for {source.Lot.ItemCode}.");

            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Ok(
                MapTransferLookupRow(order, structuralCount));
        }
        catch (TransferException ex)
        {
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(ex.Message);
        }
    }
}
