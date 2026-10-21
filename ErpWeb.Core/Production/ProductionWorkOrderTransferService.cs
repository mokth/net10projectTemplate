using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Creates and posts full-balance ownership transfers between compatible Work Orders.</summary>
public sealed partial class ProductionWorkOrderTransferService(
    IDbContextFactory<AppDbContext> dbFactory,
    IInventoryTenantContext tenant,
    IAccessRightService access,
    ICurrentDateService clock,
    IRunningNumberService numbers,
    IBranchStockTransactionLock branchLock,
    IStockPostingCoordinator coordinator,
    IProductionStockWriter productionStock,
    IProductionOperationEligibilityService? operationEligibility = null) : IProductionWorkOrderTransferService
{
    private readonly IProductionOperationEligibilityService _operationEligibility = operationEligibility ?? new ProductionOperationEligibilityService();
    private const string Menu = MenuCodes.PlanningWorkOrderTransfer;
    private const string DuplicateDraftError = "A NEW Work Order Transfer already exists for this WIP lot.";
    private const string SourceChangedError = "Source WIP changed after this transfer was prepared. Refresh the source before posting.";
    private const string SourceUomError = "Source WIP quantity and base quantity do not reconcile with its conversion factor.";
    private const string TargetInvalidError = "The selected Work Order does not have a compatible INTERNAL_ROUTE_WIP requirement for this item.";
    private const string SnapshotChangedError = "The target Work Order snapshot changed after this transfer was prepared. Reselect the target before posting.";
    private const string TargetCollisionError = "An existing target WIP lot has conflicting production identity. Repair the production balance before transfer.";
    private const string ChronologyError = "Transfer date is earlier than the latest stock event for this WIP lot.";

    private sealed class TransferException(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) : Exception(message)
    {
        public IvMasterErrorCode Code { get; } = code;
    }

    private sealed record SourceFacts(ProductionBalLot Lot, ProductionWorkOrder Order,
        ProductionWorkOrderRouteStep Route, ProductionWorkOrderOperation Operation);

    private sealed record TargetFacts(ProductionWorkOrder Order, ProductionWorkOrderMaterial Material,
        ProductionWorkOrderRouteStep ProducerRoute, ProductionWorkOrderOperation ProducerOperation,
        ProductionWorkOrderRouteStep ConsumerRoute, ProductionWorkOrderOperation ConsumerOperation,
        ProductionLocation? ProducerLocation, ProductionLocation? ConsumerLocation, bool SequenceReady, string? BlockingReason);

    private static IvMasterOperationResult<T> Fail<T>(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) =>
        IvMasterOperationResult<T>.Fail(code, message);

    private async Task<InventoryTenantScope> ScopeAsync(string permission, bool write, CancellationToken ct)
    {
        if (!await access.CanAsync(Menu, PermissionCodes.Access, ct)
            || (permission != PermissionCodes.Access && !await access.CanAsync(Menu, permission, ct)))
            throw new TransferException("Access denied.", IvMasterErrorCode.AccessDenied);
        var scope = write ? tenant.TryWriteScope() : tenant.TryBranchScope();
        return scope is { BranchCode: not null }
            ? scope
            : throw new TransferException("A company and branch scope is required.", IvMasterErrorCode.InvalidScope);
    }

    private static string User(InventoryTenantScope scope) => scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;

    private static void CheckVersion(ProductionWorkOrderTransfer row, byte[] version)
    {
        if (version.Length == 0 || !row.RowVersion.SequenceEqual(version))
            throw new TransferException("The Work Order Transfer changed. Reload before continuing.", IvMasterErrorCode.Concurrency);
    }

    private static async Task<ProductionWorkOrderTransfer> LoadTransferAsync(AppDbContext db,
        InventoryTenantScope scope, long id, bool locked, CancellationToken ct)
    {
        IQueryable<ProductionWorkOrderTransfer> q;
        if (locked && db.Database.IsSqlServer())
            q = db.ProductionWorkOrderTransfers.FromSqlInterpolated($@"
SELECT * FROM dbo.PrWorkOrderTransfer WITH (UPDLOCK,HOLDLOCK)
WHERE UID={id} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}");
        else
            q = db.ProductionWorkOrderTransfers.Where(x => x.Uid == id
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        return await q.SingleOrDefaultAsync(ct)
            ?? throw new TransferException("Work Order Transfer was not found.", IvMasterErrorCode.NotFound);
    }

    private static async Task<ProductionBalLot> LoadSourceLotAsync(AppDbContext db,
        InventoryTenantScope scope, long id, bool locked, CancellationToken ct)
    {
        IQueryable<ProductionBalLot> q;
        if (locked && db.Database.IsSqlServer())
            q = db.ProductionBalLots.FromSqlInterpolated($@"
SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK,HOLDLOCK)
WHERE UID={id} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}");
        else
        {
            q = db.ProductionBalLots.Where(x => x.Uid == id
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
            if (!locked)
                q = q.AsNoTracking();
        }
        var lot = await q.Include(x => x.WorkOrder).Include(x => x.ProducingRouteStep)
            .Include(x => x.ProductionLocation)
            .Include(x => x.WorkOrderOperation).SingleOrDefaultAsync(ct);
        return lot ?? throw new TransferException("Source WIP was not found.", IvMasterErrorCode.NotFound);
    }

    private static async Task<ProductionWorkOrder> LockWorkOrderAsync(AppDbContext db,
        InventoryTenantScope scope, long id, CancellationToken ct)
    {
        IQueryable<ProductionWorkOrder> q = db.Database.IsSqlServer()
            ? db.ProductionWorkOrders.FromSqlInterpolated($@"
SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK,HOLDLOCK)
WHERE UID={id} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}")
            : db.ProductionWorkOrders.Where(x => x.Uid == id
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        return await q.SingleOrDefaultAsync(ct)
            ?? throw new TransferException("Work Order was not found.", IvMasterErrorCode.NotFound);
    }

    private async Task<SourceFacts> ValidateSourceAsync(AppDbContext db, InventoryTenantScope scope,
        ProductionBalLot lot, CancellationToken ct)
    {
        if (lot.Kind != ProductionBalLotKinds.Wip || lot.BalanceStage != "PROCESS_WIP"
            || lot.OutputType != PrRouteOutputTypes.WipStocked
            || lot.ProducingRouteStepId is not long routeId || lot.WorkOrderOperationId is not long operationId
            || lot.Qty <= 0m || lot.BaseQty <= 0m || lot.BaseUom is null || string.IsNullOrWhiteSpace(lot.BaseUom)
            || string.IsNullOrWhiteSpace(lot.Uom) || lot.ConversionFactorToBase <= 0m
            || lot.StockStatusCode != "AVAILABLE" || lot.OriginType != "PRODUCED"
            || lot.WorkOrderMaterialId is not null || lot.OriginalIssueMovementId is not null
            || lot.ProductionLocationId is null || string.IsNullOrWhiteSpace(lot.LotNo))
            throw new TransferException("The selected balance is not eligible stockable PROCESS_WIP.");

        if (IvQty.Round(lot.BaseQty / lot.ConversionFactorToBase) != IvQty.Round(lot.Qty))
            throw new TransferException(SourceUomError);
        if (!string.IsNullOrWhiteSpace(lot.PhysicalLotNo) && lot.PhysicalLotNo != lot.LotNo)
            throw new TransferException("This WIP uses an alternate/migrated physical-lot identity that Work Order Transfer cannot safely merge into the current natural target WIP model.");

        var order = lot.WorkOrder ?? await db.ProductionWorkOrders.SingleOrDefaultAsync(x =>
            x.Uid == lot.WorkOrderId && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct)
            ?? throw new TransferException("Source Work Order was not found.", IvMasterErrorCode.NotFound);
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress
            or ProductionWorkOrderStatuses.Completed))
            throw new TransferException("Source Work Order must be RELEASED, IN_PROGRESS or COMPLETED.");
        ValidateWorkOrderSnapshot(order, "Source");

        var route = lot.ProducingRouteStep ?? await db.ProductionWorkOrderRouteSteps
            .SingleOrDefaultAsync(x => x.Uid == routeId, ct)
            ?? throw new TransferException("Source WIP producer route is missing.");
        var operation = lot.WorkOrderOperation ?? await db.ProductionWorkOrderOperations
            .SingleOrDefaultAsync(x => x.Uid == operationId, ct)
            ?? throw new TransferException("Source WIP producer operation is missing.");
        if (route.WorkOrderId != lot.WorkOrderId || operation.RouteStepId != routeId
            || operation.Uid != operationId || !operation.IsFinalOperation
            || route.OutputType != PrRouteOutputTypes.WipStocked || route.OutputItemCode != lot.ItemCode
            || route.OutputUom != lot.Uom || route.OutputBaseUom != lot.BaseUom
            || route.OutputConversionFactorToBase != lot.ConversionFactorToBase)
            throw new TransferException("Source WIP producer route and operation do not match the live balance.");

        if (await ProductionCostReadiness.PoolValuationError(db, lot, ct) is { } costError)
            throw new TransferException(costError);
        if (StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) != lot.AverageUnitCost)
            throw new TransferException($"Production pool {lot.Uid} quantity/value reconciliation failed: average unit cost does not match the verified pool value.");
        await ValidateCompletePoolContributionsAsync(db, lot, ct);
        return new(lot, order, route, operation);
    }

    private async Task<TargetFacts> ValidateTargetAsync(AppDbContext db, InventoryTenantScope scope,
        SourceFacts source, long targetMaterialId, bool createProducerLocation, CancellationToken ct)
    {
        var material = await db.ProductionWorkOrderMaterials.SingleOrDefaultAsync(x => x.Uid == targetMaterialId, ct)
            ?? throw new TransferException(TargetInvalidError);
        var order = await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.Uid == material.WorkOrderId
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct)
            ?? throw new TransferException(TargetInvalidError);
        if (order.Uid == source.Order.Uid
            || order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress)
            || order.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(order.SnapshotHash) || order.SnapshotHash.Length != 64
            || !ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot
            || !ProductionSnapshotHashVersions.SupportsDailyProduction(order.SnapshotHashVersion))
            throw new TransferException(TargetInvalidError);
        if (material.WorkOrderId != order.Uid || material.ComponentCode != source.Lot.ItemCode
            || !string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.OrdinalIgnoreCase)
            || material.ProducingRouteStepId is not long producerRouteId
            || material.WorkOrderOperationId is not long consumerOperationId
            || string.IsNullOrWhiteSpace(material.RequiredUom) || material.RequiredBaseQty < 0m
            || material.RequiredQty < 0m || string.IsNullOrWhiteSpace(material.BaseUom)
            || material.BaseUom != source.Lot.BaseUom || material.ConversionFactorToBase <= 0m
            || IvQty.Round(material.RequiredQty * material.ConversionFactorToBase) != IvQty.Round(material.RequiredBaseQty)
            || IvQty.Round(material.RequiredBaseQty / material.ConversionFactorToBase) != IvQty.Round(material.RequiredQty))
            throw new TransferException(TargetInvalidError);

        var producerRoute = await db.ProductionWorkOrderRouteSteps.SingleOrDefaultAsync(x =>
            x.Uid == producerRouteId && x.WorkOrderId == order.Uid, ct)
            ?? throw new TransferException(TargetInvalidError);
        var producerOperations = await db.ProductionWorkOrderOperations.Where(x =>
            x.RouteStepId == producerRoute.Uid && x.IsFinalOperation).OrderBy(x => x.Uid).ToListAsync(ct);
        if (producerRoute.WorkOrderId != order.Uid || producerRoute.OutputType != PrRouteOutputTypes.WipStocked
            || producerRoute.OutputItemCode != source.Lot.ItemCode || producerRoute.OutputBaseUom != source.Lot.BaseUom
            || producerRoute.OutputConversionFactorToBase != source.Lot.ConversionFactorToBase
            || producerRoute.OutputUom != source.Lot.Uom
            || producerOperations.Count != 1)
            throw new TransferException(TargetInvalidError);
        var producerOperation = producerOperations[0];
        if (producerOperation.RouteStepId != producerRoute.Uid || !producerOperation.IsFinalOperation)
            throw new TransferException(TargetInvalidError);

        var consumerOperation = await db.ProductionWorkOrderOperations.SingleOrDefaultAsync(x =>
            x.Uid == consumerOperationId && x.RouteStepId != null, ct)
            ?? throw new TransferException(TargetInvalidError);
        var consumerRoute = await db.ProductionWorkOrderRouteSteps.SingleOrDefaultAsync(x =>
            x.Uid == consumerOperation.RouteStepId && x.WorkOrderId == order.Uid, ct)
            ?? throw new TransferException(TargetInvalidError);
        if (consumerOperation.WorkOrderId is long consumerOrderId && consumerOrderId != order.Uid)
            throw new TransferException(TargetInvalidError);
        var routeSteps = await db.ProductionWorkOrderRouteSteps.AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid).ToListAsync(ct);
        var operations = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid || routeSteps.Select(r => r.Uid).Contains(x.RouteStepId ?? 0))
            .ToListAsync(ct);
        var readiness = _operationEligibility.Evaluate(consumerOperation, routeSteps, operations);

        var producerLocation = await ResolveProductionLocationAsync(db, scope, producerRoute.WorkCentreCode,
            createProducerLocation, ct);
        var consumerLocation = await FindProductionLocationAsync(db, scope, consumerRoute.WorkCentreCode, ct);
        return new(order, material, producerRoute, producerOperation, consumerRoute, consumerOperation,
            producerLocation, consumerLocation, readiness.IsEligible, readiness.BlockingReason);
    }

    private static void ValidateWorkOrderSnapshot(ProductionWorkOrder order, string role)
    {
        if (order.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(order.SnapshotHash) || order.SnapshotHash.Length != 64
            || !ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot
            || !ProductionSnapshotHashVersions.SupportsDailyProduction(order.SnapshotHashVersion))
            throw new TransferException($"{role} Work Order requires a current full-hierarchy Production snapshot before Work Order Transfer.");
    }

    private sealed record LookupEvidenceMovement(ProductionValuationEvidence Evidence, ProductionBalLotMovement Movement);

    private static async Task<Dictionary<long, SourceFacts>> ValidateSourceLookupBatchAsync(AppDbContext db,
        InventoryTenantScope scope, IReadOnlyCollection<long> candidateIds, CancellationToken ct)
    {
        var lots = await db.ProductionBalLots.AsNoTracking().Where(x => candidateIds.Contains(x.Uid)
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode)
            .Include(x => x.WorkOrder).Include(x => x.ProducingRouteStep).Include(x => x.WorkOrderOperation)
            .ToListAsync(ct);
        var lotIds = lots.Select(x => x.Uid).ToArray();
        if (lotIds.Length == 0) return [];

        var valuations = await db.ProductionPoolValuationRows.AsNoTracking()
            .Where(x => lotIds.Contains(x.ProductionBalLotId)).ToDictionaryAsync(x => x.ProductionBalLotId, ct);
        var currentEvidence = await (from valuation in db.ProductionPoolValuationRows.AsNoTracking()
            join evidence in db.ProductionValuationEvidenceRows.AsNoTracking()
                on new { valuation.ProductionBalLotId, valuation.Generation }
                equals new { evidence.ProductionBalLotId, evidence.Generation }
            join movement in db.ProductionBalLotMovements.AsNoTracking() on evidence.MovementId equals movement.Uid
            where lotIds.Contains(valuation.ProductionBalLotId)
            select new LookupEvidenceMovement(evidence, movement)).ToListAsync(ct);
        var registry = new StockMovementRegistry();
        var receiptIds = new HashSet<long>();
        foreach (var row in currentEvidence)
        {
            if (row.Movement.OriginalMovementId is not null) continue;
            try
            {
                if (registry.GetRequired(row.Movement.MovementType).Direction > 0)
                    receiptIds.Add(row.Movement.Uid);
            }
            catch (InvalidOperationException) { }
        }
        var receiptIdArray = receiptIds.ToArray();
        List<ProductionMovementAllocation> allocations = receiptIdArray.Length == 0 ? [] : await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(x => receiptIdArray.Contains(x.ReceiptMovementId)).ToListAsync(ct);
        var allocationsByReceipt = allocations.GroupBy(x => x.ReceiptMovementId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<ProductionMovementAllocation>)x.ToArray());

        var result = new Dictionary<long, SourceFacts>();
        foreach (var lot in lots)
        {
            try
            {
                if (lot.Kind != ProductionBalLotKinds.Wip || lot.BalanceStage != "PROCESS_WIP"
                    || lot.OutputType != PrRouteOutputTypes.WipStocked
                    || lot.ProducingRouteStepId is not long routeId || lot.WorkOrderOperationId is not long operationId
                    || lot.Qty <= 0m || lot.BaseQty <= 0m || string.IsNullOrWhiteSpace(lot.BaseUom)
                    || string.IsNullOrWhiteSpace(lot.Uom) || lot.ConversionFactorToBase <= 0m
                    || lot.StockStatusCode != "AVAILABLE" || lot.OriginType != "PRODUCED"
                    || lot.WorkOrderMaterialId is not null || lot.OriginalIssueMovementId is not null
                    || lot.ProductionLocationId is null || string.IsNullOrWhiteSpace(lot.LotNo)
                    || !string.IsNullOrWhiteSpace(lot.PhysicalLotNo) && lot.PhysicalLotNo != lot.LotNo
                    || IvQty.Round(lot.BaseQty / lot.ConversionFactorToBase) != IvQty.Round(lot.Qty))
                    throw new TransferException("The selected balance is not eligible stockable PROCESS_WIP.");

                var order = lot.WorkOrder ?? throw new TransferException("Source Work Order was not found.");
                if (order.CompanyCode != scope.CompanyCode || order.BranchCode != scope.BranchCode
                    || order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress
                        or ProductionWorkOrderStatuses.Completed))
                    throw new TransferException("Source Work Order must be RELEASED, IN_PROGRESS or COMPLETED.");
                ValidateWorkOrderSnapshot(order, "Source");
                var route = lot.ProducingRouteStep ?? throw new TransferException("Source WIP producer route is missing.");
                var operation = lot.WorkOrderOperation ?? throw new TransferException("Source WIP producer operation is missing.");
                if (route.Uid != routeId || route.WorkOrderId != lot.WorkOrderId || operation.Uid != operationId
                    || operation.RouteStepId != routeId || !operation.IsFinalOperation
                    || route.OutputType != PrRouteOutputTypes.WipStocked || route.OutputItemCode != lot.ItemCode
                    || route.OutputUom != lot.Uom || route.OutputBaseUom != lot.BaseUom
                    || route.OutputConversionFactorToBase != lot.ConversionFactorToBase)
                    throw new TransferException("Source WIP producer route and operation do not match the live balance.");

                valuations.TryGetValue(lot.Uid, out var valuation);
                if (ProductionCostReadiness.PoolValuationError(lot, valuation) is { } costError)
                    throw new TransferException(costError);
                if (StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) != lot.AverageUnitCost)
                    throw new TransferException($"Production pool {lot.Uid} quantity/value reconciliation failed: average unit cost does not match the verified pool value.");

                var poolRows = currentEvidence.Where(x => x.Evidence.ProductionBalLotId == lot.Uid).ToArray();
                var poolMovements = poolRows.Select(x => x.Movement).ToArray();
                var receipts = poolRows.Where(x => receiptIds.Contains(x.Movement.Uid)).ToArray();
                if (receipts.Length == 0)
                    throw new TransferException($"Production pool {lot.Uid} has incomplete current-generation quantity contribution evidence.");
                var available = new List<decimal>(receipts.Length);
                foreach (var receipt in receipts)
                {
                    var receiptAllocations = allocationsByReceipt.GetValueOrDefault(receipt.Movement.Uid) ?? [];
                    var availableBase = ProductionContributionAvailabilityService.CalculateAvailableBaseQty(
                        receipt.Movement.Uid, receipt.Movement.BaseQty, poolMovements, receiptAllocations);
                    if (receipt.Evidence.Status != ProductionPoolValuationService.Verified || availableBase < 0m)
                        throw new TransferException($"Production pool {lot.Uid} has incomplete current-generation quantity contribution evidence.");
                    available.Add(availableBase);
                }
                if (!available.Any(x => x > 0m) || IvQty.Round(available.Sum()) != IvQty.Round(lot.BaseQty))
                    throw new TransferException($"Production pool {lot.Uid} has incomplete current-generation quantity contribution evidence.");
                result[lot.Uid] = new(lot, order, route, operation);
            }
            catch (TransferException) { }
        }
        return result;
    }

    private sealed record TargetLookupResult(TargetFacts Facts, decimal? TargetCurrentWipValue);

    private async Task<Dictionary<long, TargetLookupResult>> ValidateTargetLookupBatchAsync(AppDbContext db,
        InventoryTenantScope scope, SourceFacts source, IReadOnlyCollection<long> candidateMaterialIds,
        bool canViewCost, CancellationToken ct)
    {
        var materials = await db.ProductionWorkOrderMaterials.AsNoTracking()
            .Where(x => candidateMaterialIds.Contains(x.Uid)).ToListAsync(ct);
        var orderIds = materials.Select(x => x.WorkOrderId).Distinct().ToArray();
        if (orderIds.Length == 0) return [];
        var orders = await db.ProductionWorkOrders.AsNoTracking().Where(x => orderIds.Contains(x.Uid)
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode)
            .ToDictionaryAsync(x => x.Uid, ct);
        var routeSteps = await db.ProductionWorkOrderRouteSteps.AsNoTracking()
            .Where(x => orderIds.Contains(x.WorkOrderId)).ToListAsync(ct);
        var routeIds = routeSteps.Select(x => x.Uid).ToArray();
        var operations = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrderId != null && orderIds.Contains(x.WorkOrderId.Value)
                || x.RouteStepId != null && routeIds.Contains(x.RouteStepId.Value))
            .ToListAsync(ct);
        var routesById = routeSteps.ToDictionary(x => x.Uid);
        var operationsById = operations.ToDictionary(x => x.Uid);
        var operationsByRoute = operations.Where(x => x.RouteStepId is not null)
            .GroupBy(x => x.RouteStepId!.Value).ToDictionary(x => x.Key, x => x.ToArray());

        var locationCodes = routeSteps.Select(x => x.WorkCentreCode)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Length > 20 ? x[..20] : x)
            .Distinct().ToArray();
        var locations = await db.ProductionLocations.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && locationCodes.Contains(x.Code))
            .ToListAsync(ct);
        var locationByCode = locations.ToDictionary(x => x.Code);

        List<ProductionBalLot> targetBalances = canViewCost
            ? await db.ProductionBalLots.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode && x.Kind == ProductionBalLotKinds.Wip
                    && orderIds.Contains(x.WorkOrderId) && x.ItemCode == source.Lot.ItemCode && x.LotNo == source.Lot.LotNo)
                .ToListAsync(ct)
            : [];
        var balancesByIdentity = targetBalances.GroupBy(x => (x.WorkOrderId, x.ProducingRouteStepId))
            .ToDictionary(x => x.Key, x => x.ToArray());

        var result = new Dictionary<long, TargetLookupResult>();
        foreach (var material in materials)
        {
            try
            {
                if (!orders.TryGetValue(material.WorkOrderId, out var order)
                    || order.Uid == source.Order.Uid
                    || order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress)
                    || order.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(order.SnapshotHash) || order.SnapshotHash.Length != 64
                    || !ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot
                    || !ProductionSnapshotHashVersions.SupportsDailyProduction(order.SnapshotHashVersion))
                    throw new TransferException(TargetInvalidError);
                if (material.WorkOrderId != order.Uid || material.ComponentCode != source.Lot.ItemCode
                    || !string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.OrdinalIgnoreCase)
                    || material.ProducingRouteStepId is not long producerRouteId
                    || material.WorkOrderOperationId is not long consumerOperationId
                    || string.IsNullOrWhiteSpace(material.RequiredUom) || material.RequiredBaseQty < 0m
                    || material.RequiredQty < 0m || string.IsNullOrWhiteSpace(material.BaseUom)
                    || material.BaseUom != source.Lot.BaseUom || material.ConversionFactorToBase <= 0m
                    || IvQty.Round(material.RequiredQty * material.ConversionFactorToBase) != IvQty.Round(material.RequiredBaseQty)
                    || IvQty.Round(material.RequiredBaseQty / material.ConversionFactorToBase) != IvQty.Round(material.RequiredQty)
                    || !routesById.TryGetValue(producerRouteId, out var producerRoute)
                    || producerRoute.WorkOrderId != order.Uid || producerRoute.OutputType != PrRouteOutputTypes.WipStocked
                    || producerRoute.OutputItemCode != source.Lot.ItemCode || producerRoute.OutputBaseUom != source.Lot.BaseUom
                    || producerRoute.OutputConversionFactorToBase != source.Lot.ConversionFactorToBase
                    || producerRoute.OutputUom != source.Lot.Uom
                    || !operationsByRoute.TryGetValue(producerRouteId, out var finalOperations))
                    throw new TransferException(TargetInvalidError);
                var producerFinalOperations = finalOperations.Where(x => x.IsFinalOperation).OrderBy(x => x.Uid).ToArray();
                if (producerFinalOperations.Length != 1) throw new TransferException(TargetInvalidError);
                var producerOperation = producerFinalOperations[0];
                if (!operationsById.TryGetValue(consumerOperationId, out var consumerOperation)
                    || consumerOperation.RouteStepId is not long consumerRouteId
                    || consumerOperation.WorkOrderId is long consumerOrderId && consumerOrderId != order.Uid
                    || !routesById.TryGetValue(consumerRouteId, out var consumerRoute)
                    || consumerRoute.WorkOrderId != order.Uid)
                    throw new TransferException(TargetInvalidError);

                var orderRoutes = routeSteps.Where(x => x.WorkOrderId == order.Uid).ToArray();
                var orderRouteIds = orderRoutes.Select(x => x.Uid).ToHashSet();
                var orderOperations = operations.Where(x => x.WorkOrderId == order.Uid
                    || x.RouteStepId is long routeId && orderRouteIds.Contains(routeId)).ToArray();
                var readiness = _operationEligibility.Evaluate(consumerOperation, orderRoutes, orderOperations);
                var producerCode = string.IsNullOrWhiteSpace(producerRoute.WorkCentreCode)
                    ? throw new TransferException("A target producer Work Centre is required.")
                    : producerRoute.WorkCentreCode.Length > 20 ? producerRoute.WorkCentreCode[..20] : producerRoute.WorkCentreCode;
                locationByCode.TryGetValue(producerCode, out var producerLocation);
                if (producerLocation is { IsActive: false })
                    throw new TransferException("The target producer production location is inactive.");
                ProductionLocation? consumerLocation = null;
                if (!string.IsNullOrWhiteSpace(consumerRoute.WorkCentreCode))
                {
                    var consumerCode = consumerRoute.WorkCentreCode.Length > 20
                        ? consumerRoute.WorkCentreCode[..20] : consumerRoute.WorkCentreCode;
                    if (locationByCode.TryGetValue(consumerCode, out var found) && found.IsActive)
                        consumerLocation = found;
                }

                decimal? targetValue = null;
                if (canViewCost)
                {
                    var identity = (order.Uid, (long?)producerRoute.Uid);
                    if (balancesByIdentity.TryGetValue(identity, out var identityBalances))
                    {
                        if (identityBalances.Length != 1) throw new TransferException(TargetCollisionError);
                        targetValue = identityBalances[0].TotalCost;
                    }
                    else targetValue = 0m;
                }
                var facts = new TargetFacts(order, material, producerRoute, producerOperation, consumerRoute,
                    consumerOperation, producerLocation, consumerLocation, readiness.IsEligible, readiness.BlockingReason);
                result[material.Uid] = new(facts, targetValue);
            }
            catch (TransferException) { }
        }
        return result;
    }

    private static async Task ValidateCompletePoolContributionsAsync(AppDbContext db, ProductionBalLot lot, CancellationToken ct,
        bool requireActivePositiveReceipt = true)
    {
        var valuation = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == lot.Uid, ct)
            ?? throw new TransferException($"Production pool {lot.Uid} has no verified valuation generation.");
        if (valuation.Status != ProductionPoolValuationService.Verified
            || valuation.TrackedBaseQty != lot.BaseQty || valuation.TrackedValue != lot.TotalCost)
            throw new TransferException($"Production pool {lot.Uid} valuation does not match the live WIP balance.");
        var contributions = await ProductionContributionAvailabilityService.ReadAsync(db, lot.Uid, valuation.Generation, ct);
        if (contributions.Count == 0 || requireActivePositiveReceipt && !contributions.Any(x => x.Contribution.AvailableBaseQty > 0m)
            || contributions.Any(x => x.Evidence.Status != ProductionPoolValuationService.Verified
                || x.Contribution.AvailableBaseQty < 0m)
            || IvQty.Round(contributions.Sum(x => x.Contribution.AvailableBaseQty)) != IvQty.Round(lot.BaseQty))
            throw new TransferException($"Production pool {lot.Uid} has incomplete current-generation quantity contribution evidence.");
    }

    private static async Task<string> CaptureExecutionProjectionAsync(AppDbContext db, long[] workOrderIds, CancellationToken ct)
    {
        var orders = await db.ProductionWorkOrders.AsNoTracking().Where(x => workOrderIds.Contains(x.Uid))
            .OrderBy(x => x.Uid).Select(x => new { x.Uid, x.Status, x.GoodQty, x.ScrapQty, x.RejectQty, x.HoldQty, x.RemainingQty })
            .ToListAsync(ct);
        var operations = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrderId != null && workOrderIds.Contains(x.WorkOrderId.Value))
            .OrderBy(x => x.Uid).Select(x => new
            {
                x.Uid, x.InputQty, x.ProcessedQty, x.GoodQty, x.ScrapQty, x.RejectQty, x.HoldQty,
                x.ReworkQty, x.TransferredQty, x.RemainingQty
            }).ToListAsync(ct);
        var materials = await db.ProductionWorkOrderMaterials.AsNoTracking().Where(x => workOrderIds.Contains(x.WorkOrderId))
            .OrderBy(x => x.Uid).Select(x => new
            {
                x.Uid, x.ReservedQty, x.PickedQty, x.IssuedQty, x.ReturnedQty, x.ConsumedQty, x.VarianceQty
            }).ToListAsync(ct);
        return StockPostingFingerprint.Create(new { orders, operations, materials }, new { }).RequestFingerprint;
    }

    private static async Task<ProductionLocation?> ResolveProductionLocationAsync(AppDbContext db,
        InventoryTenantScope scope, string workCentre, bool create, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workCentre))
            throw new TransferException("A target producer Work Centre is required.");
        var code = workCentre.Length > 20 ? workCentre[..20] : workCentre;
        var location = await db.ProductionLocations.SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode && x.Code == code, ct);
        if (location is null && create)
        {
            location = new ProductionLocation
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, Code = code,
                Description = workCentre, WorkCentreCode = workCentre
            };
            db.ProductionLocations.Add(location);
            await db.SaveChangesAsync(ct);
        }
        if (location is { IsActive: false })
            throw new TransferException("The target producer production location is inactive.");
        return location;
    }

    private static Task<ProductionLocation?> FindProductionLocationAsync(AppDbContext db,
        InventoryTenantScope scope, string workCentre, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workCentre)) return Task.FromResult<ProductionLocation?>(null);
        var code = workCentre.Length > 20 ? workCentre[..20] : workCentre;
        return db.ProductionLocations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode && x.Code == code && x.IsActive, ct);
    }

    private static async Task<string> AllocateTransferNoAsync(AppDbContext db, string company,
        IRunningNumberService numberService, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var number = $"WT{await numberService.GetNextAsync(db, company, RunningNumberKeys.ProductionWorkOrderTransfer, ct):D8}";
            if (!await db.ProductionWorkOrderTransfers.AnyAsync(x => x.CompanyCode == company && x.TransferNo == number, ct))
                return number;
        }
        throw new TransferException("Unable to allocate a Work Order Transfer number.");
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferPage>> SearchAsync(
        ProductionWorkOrderTransferQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            var canViewCost = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.ProductionWorkOrderTransfers.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.DeletedAtUtc == null);
            if (!string.IsNullOrWhiteSpace(query.Status)) q = q.Where(x => x.Status == query.Status.Trim());
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var term = query.SearchText.Trim();
                q = q.Where(x => x.TransferNo.Contains(term) || x.ItemCode.Contains(term) || x.LotNo.Contains(term)
                    || x.SourceWorkOrderNo.Contains(term) || x.TargetWorkOrderNo.Contains(term));
            }
            var total = await q.CountAsync(ct);
            var skip = Math.Max(0, query.Skip);
            var take = Math.Clamp(query.Take, 1, 100);
            var rows = await q.OrderByDescending(x => x.TransferDateTime).ThenByDescending(x => x.Uid)
                .Skip(skip).Take(take).ToListAsync(ct);
            var page = rows.Select(x => new ProductionWorkOrderTransferSummary(x.Uid, x.TransferNo,
                x.TransferDateTime, x.ItemCode, x.LotNo, x.Qty, x.Uom, x.SourceWorkOrderNo,
                x.SourceWorkCentreCode, x.SourceProcessCode, x.TargetWorkOrderNo,
                x.TargetConsumingWorkCentreCode, x.TargetConsumingProcessCode, x.Status, x.CreatedBy,
                x.PostedBy, canViewCost ? x.TransferTotalCost : null, x.RowVersion)).ToArray();
            return IvMasterOperationResult<ProductionWorkOrderTransferPage>.Ok(new(page, total));
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferPage>(ex.Message, ex.Code); }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> GetAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var transfer = await db.ProductionWorkOrderTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == id
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.DeletedAtUtc == null, ct)
                ?? throw new TransferException("Work Order Transfer was not found.", IvMasterErrorCode.NotFound);
            return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(await MapAsync(db, transfer, scope, ct));
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, ex.Code); }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferSourcePage>> SearchEligibleSourcesAsync(
        ProductionWorkOrderTransferSourceQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            var canViewCost = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.ProductionBalLots.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.Kind == ProductionBalLotKinds.Wip
                && x.BalanceStage == "PROCESS_WIP" && x.OutputType == PrRouteOutputTypes.WipStocked
                && x.ProducingRouteStepId != null && x.WorkOrderOperationId != null
                && x.Qty > 0m && x.BaseQty > 0m && x.ConversionFactorToBase > 0m
                && x.StockStatusCode == "AVAILABLE" && x.OriginType == "PRODUCED"
                && x.WorkOrderMaterialId == null && x.OriginalIssueMovementId == null
                && x.ProductionLocationId != null && x.LotNo.Trim() != ""
                && (x.PhysicalLotNo == null || x.PhysicalLotNo.Trim() == "" || x.PhysicalLotNo == x.LotNo)
                && x.Uom.Trim() != "" && x.BaseUom != null && x.BaseUom.Trim() != ""
                && !db.ProductionWorkOrderTransfers.Any(transfer => transfer.CompanyCode == scope.CompanyCode
                    && transfer.BranchCode == scope.BranchCode && transfer.SourceProductionBalLotId == x.Uid
                    && transfer.Status == ProductionWorkOrderTransferStatuses.New && transfer.DeletedAtUtc == null)
                && db.ProductionWorkOrders.Any(w => w.Uid == x.WorkOrderId && w.CompanyCode == scope.CompanyCode
                    && w.BranchCode == scope.BranchCode && (w.Status == ProductionWorkOrderStatuses.Released
                        || w.Status == ProductionWorkOrderStatuses.InProgress || w.Status == ProductionWorkOrderStatuses.Completed)));
            if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
            {
                var workOrderNo = query.WorkOrderNo.Trim();
                q = q.Where(x => db.ProductionWorkOrders.Any(w => w.Uid == x.WorkOrderId
                    && w.CompanyCode == scope.CompanyCode && w.BranchCode == scope.BranchCode
                    && w.WorkOrderNo == workOrderNo));
            }
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var term = query.SearchText.Trim();
                q = q.Where(x => x.ItemCode.Contains(term) || x.LotNo.Contains(term)
                    || (x.Description != null && x.Description.Contains(term))
                    || db.ProductionWorkOrders.Any(w => w.Uid == x.WorkOrderId
                        && w.CompanyCode == scope.CompanyCode && w.BranchCode == scope.BranchCode
                        && w.WorkOrderNo.Contains(term)));
            }
            var skip = Math.Max(0, query.Skip);
            var take = Math.Clamp(query.Take, 1, 100);
            var eligibleCount = 0;
            var candidateSkip = 0;
            var rows = new List<ProductionWorkOrderTransferSource>(take);
            var workOrders = new Dictionary<long, (SourceFacts Facts, int BalanceCount)>();
            while (true)
            {
                var candidates = await q.OrderBy(x => x.ItemCode).ThenBy(x => x.LotNo).ThenBy(x => x.Uid)
                    .Skip(candidateSkip).Take(250).Select(x => x.Uid).ToListAsync(ct);
                if (candidates.Count == 0) break;
                candidateSkip += candidates.Count;
                var factsById = await ValidateSourceLookupBatchAsync(db, scope, candidates, ct);
                foreach (var id in candidates)
                {
                    if (!factsById.TryGetValue(id, out var facts)) continue;
                    if (workOrders.TryGetValue(facts.Order.Uid, out var workOrder))
                        workOrders[facts.Order.Uid] = (workOrder.Facts, workOrder.BalanceCount + 1);
                    else
                        workOrders[facts.Order.Uid] = (facts, 1);
                    var lot = facts.Lot;
                    if (eligibleCount >= skip && eligibleCount < skip + take)
                        rows.Add(new(lot.Uid, facts.Order.WorkOrderNo, lot.ItemCode, lot.Description ?? string.Empty,
                            lot.LotNo, lot.Qty, lot.Uom, lot.BaseQty, lot.BaseUom, facts.Route.WorkCentreCode,
                            facts.Operation.OperationCode, lot.LastMovementDate, canViewCost ? lot.TotalCost : null, lot.RowVersion));
                    eligibleCount++;
                }
            }
            var workOrderChoices = workOrders.Values.Select(x => new ProductionWorkOrderTransferWorkOrderChoice(
                    x.Facts.Order.Uid, x.Facts.Order.WorkOrderNo, x.Facts.Order.ProductCode,
                    x.Facts.Order.ProductDescription, x.Facts.Order.Status))
                .OrderBy(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase).ToArray();
            return IvMasterOperationResult<ProductionWorkOrderTransferSourcePage>.Ok(
                new ProductionWorkOrderTransferSourcePage(rows, eligibleCount) { WorkOrders = workOrderChoices });
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferSourcePage>(ex.Message, ex.Code); }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferTargetPage>> SearchEligibleTargetsAsync(
        long sourceProductionBalLotId, ProductionWorkOrderTransferTargetQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, false, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var lot = await LoadSourceLotAsync(db, scope, sourceProductionBalLotId, false, ct);
            var source = await ValidateSourceAsync(db, scope, lot, ct);
            var targets = db.ProductionWorkOrderMaterials.AsNoTracking().Where(x => x.ComponentCode == lot.ItemCode
                && x.BaseUom == lot.BaseUom && x.RequiredQty >= 0m && x.RequiredBaseQty >= 0m
                && x.ConversionFactorToBase > 0m && x.RequiredUom != null && x.RequiredUom.Trim() != ""
                && x.SupplySource == PrMaterialSupplySources.InternalRouteWip
                && x.ProducingRouteStepId != null && x.WorkOrderOperationId != null
                && x.WorkOrderId != source.Order.Uid
                && db.ProductionWorkOrders.Any(w => w.Uid == x.WorkOrderId && w.CompanyCode == scope.CompanyCode
                    && w.BranchCode == scope.BranchCode && (w.Status == ProductionWorkOrderStatuses.Released
                        || w.Status == ProductionWorkOrderStatuses.InProgress)));
            if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
            {
                var workOrderNo = query.WorkOrderNo.Trim();
                targets = targets.Where(x => x.WorkOrder!.WorkOrderNo == workOrderNo);
            }
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var term = query.SearchText.Trim();
                targets = targets.Where(x => x.WorkOrder!.WorkOrderNo.Contains(term)
                    || x.WorkOrder.ProductCode.Contains(term) || x.ComponentCode.Contains(term));
            }
            var canViewCost = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
            var skip = Math.Max(0, query.Skip);
            var take = Math.Clamp(query.Take, 1, 100);
            var eligibleCount = 0;
            var candidateSkip = 0;
            var rows = new List<ProductionWorkOrderTransferTarget>(take);
            var workOrders = new Dictionary<long, (TargetFacts Facts, int RequirementCount)>();
            while (true)
            {
                var materialIds = await targets.OrderBy(x => x.WorkOrderId).ThenBy(x => x.Uid)
                    .Skip(candidateSkip).Take(250).Select(x => x.Uid).ToListAsync(ct);
                if (materialIds.Count == 0) break;
                candidateSkip += materialIds.Count;
                var factsByMaterialId = await ValidateTargetLookupBatchAsync(db, scope, source, materialIds, canViewCost, ct);
                foreach (var materialId in materialIds)
                {
                    if (!factsByMaterialId.TryGetValue(materialId, out var result)) continue;
                    var facts = result.Facts;
                    var targetValue = result.TargetCurrentWipValue;
                    if (workOrders.TryGetValue(facts.Order.Uid, out var workOrder))
                        workOrders[facts.Order.Uid] = (workOrder.Facts, workOrder.RequirementCount + 1);
                    else
                        workOrders[facts.Order.Uid] = (facts, 1);
                    if (!string.IsNullOrWhiteSpace(query.WorkOrderNo)
                        && !string.Equals(facts.Order.WorkOrderNo, query.WorkOrderNo.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (eligibleCount >= skip && eligibleCount < skip + take)
                        rows.Add(new(facts.Order.Uid, facts.Order.WorkOrderNo, facts.Order.ProductCode,
                            facts.Order.ProductDescription, facts.Order.Status, facts.Material.Uid,
                            facts.Material.ComponentCode, facts.Material.RequiredQty, facts.Material.RequiredUom,
                            facts.ConsumerRoute.WorkCentreCode, facts.ConsumerOperation.OperationCode,
                            facts.ProducerRoute.WorkCentreCode, facts.ProducerOperation.OperationCode,
                            facts.ProducerRoute.Uid, facts.ProducerOperation.Uid, targetValue,
                            canViewCost ? targetValue + source.Lot.TotalCost : null,
                            facts.Material.RequiredBaseQty, facts.Material.BaseUom, facts.SequenceReady, facts.BlockingReason));
                    eligibleCount++;
                }
            }
            var workOrderChoices = workOrders.Values.Select(x => new ProductionWorkOrderTransferWorkOrderChoice(
                    x.Facts.Order.Uid, x.Facts.Order.WorkOrderNo, x.Facts.Order.ProductCode,
                    x.Facts.Order.ProductDescription, x.Facts.Order.Status))
                .OrderBy(x => x.WorkOrderNo, StringComparer.OrdinalIgnoreCase).ToArray();
            return IvMasterOperationResult<ProductionWorkOrderTransferTargetPage>.Ok(
                new ProductionWorkOrderTransferTargetPage(rows, eligibleCount) { WorkOrders = workOrderChoices });
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferTargetPage>(ex.Message, ex.Code); }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> CreateAsync(
        ProductionWorkOrderTransferCreateRequest request, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Add, true, ct);
            if (request.SourceProductionBalLotId <= 0 || request.TargetWorkOrderMaterialId <= 0)
                throw new TransferException("Select a source WIP lot and a target INTERNAL_ROUTE_WIP requirement.");
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ProductionWorkOrderTransferWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var lot = await LoadSourceLotAsync(db, scope, request.SourceProductionBalLotId, true, ct);
            var source = await ValidateSourceAsync(db, scope, lot, ct);
            var target = await ValidateTargetAsync(db, scope, source, request.TargetWorkOrderMaterialId, true, ct);
            if (await db.ProductionWorkOrderTransfers.AnyAsync(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.SourceProductionBalLotId == lot.Uid
                && x.Status == ProductionWorkOrderTransferStatuses.New && x.DeletedAtUtc == null, ct))
                throw new TransferException(DuplicateDraftError);

            var requestId = Guid.NewGuid().ToString("N");
            var link = new ProductionPostingLink
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                CommandType = ProductionPostingCommandTypes.WorkOrderTransferPost,
                PostingRequestId = requestId, WorkOrderId = target.Order.Uid,
                DocumentRevision = 1,
                ProductionDocumentType = ProductionDocumentTypes.WorkOrderTransfer,
                ProductionDocumentNo = await AllocateTransferNoAsync(db, scope.CompanyCode, numbers, ct),
                SnapshotRevision = target.Order.SnapshotRevision, SnapshotHash = target.Order.SnapshotHash,
                Status = ProductionPostingLinkStatuses.Draft, CreatedDate = clock.Now, CreatedBy = User(scope)
            };
            db.ProductionPostingLinks.Add(link);
            await db.SaveChangesAsync(ct);
            var transfer = new ProductionWorkOrderTransfer
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                TransferNo = link.ProductionDocumentNo!, Status = ProductionWorkOrderTransferStatuses.New,
                TransferDateTime = request.TransferDateTime == default ? clock.Now : request.TransferDateTime,
                Remark = NormalizeRemark(request.Remark),
                SourceProductionBalLotId = lot.Uid, SourceWorkOrderId = source.Order.Uid,
                SourceWorkOrderNo = source.Order.WorkOrderNo, SourceProducingRouteStepId = source.Route.Uid,
                SourceProducerOperationId = source.Operation.Uid, SourceWorkCentreCode = source.Route.WorkCentreCode,
                SourceProcessCode = source.Operation.OperationCode, SourceProductionLocationId = lot.ProductionLocationId!.Value,
                ItemCode = lot.ItemCode, LotNo = lot.LotNo, PhysicalLotNo = lot.PhysicalLotNo,
                PoolCode = lot.PoolCode, LotIdentityKind = lot.LotIdentityKind,
                Qty = lot.Qty, Uom = lot.Uom, BaseQty = lot.BaseQty, BaseUom = lot.BaseUom,
                ConversionFactorToBase = lot.ConversionFactorToBase, TransferTotalCost = lot.TotalCost,
                SourceLastMovementDate = lot.LastMovementDate,
                SourceLastStockEventEffectiveAt = lot.LastStockEventEffectiveAt,
                SourceBalanceRowVersion = lot.RowVersion.ToArray(),
                SourceSnapshotRevision = source.Order.SnapshotRevision,
                SourceSnapshotHash = source.Order.SnapshotHash,
                TargetWorkOrderId = target.Order.Uid, TargetWorkOrderNo = target.Order.WorkOrderNo,
                TargetProducingRouteStepId = target.ProducerRoute.Uid, TargetProducerOperationId = target.ProducerOperation.Uid,
                TargetProducerWorkCentreCode = target.ProducerRoute.WorkCentreCode,
                TargetProducerProcessCode = target.ProducerOperation.OperationCode,
                TargetProducerProductionLocationId = target.ProducerLocation!.Id,
                TargetWorkOrderMaterialId = target.Material.Uid, TargetConsumingRouteStepId = target.ConsumerRoute.Uid,
                TargetConsumingOperationId = target.ConsumerOperation.Uid,
                TargetConsumingWorkCentreCode = target.ConsumerRoute.WorkCentreCode,
                TargetConsumingProcessCode = target.ConsumerOperation.OperationCode,
                TargetConsumerProductionLocationId = target.ConsumerLocation?.Id,
                TargetSnapshotRevision = target.Order.SnapshotRevision, TargetSnapshotHash = target.Order.SnapshotHash,
                DocumentRevision = 1,
                PostingRequestId = requestId, ProductionPostingLinkId = link.Uid,
                CreatedDate = clock.Now, CreatedBy = User(scope)
            };
            db.ProductionWorkOrderTransfers.Add(transfer);
            await db.SaveChangesAsync(ct);
            link.ProductionDocumentLineId = transfer.Uid;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(await MapAsync(db, transfer, scope, ct));
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, ex.Code); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Fail<ProductionWorkOrderTransferDocument>(DuplicateDraftError, IvMasterErrorCode.Concurrency); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionWorkOrderTransferDocument>("The Work Order Transfer source changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> UpdateAsync(
        ProductionWorkOrderTransferUpdateRequest request, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Edit, true, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ProductionWorkOrderTransferWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var transfer = await LoadTransferAsync(db, scope, request.Id, true, ct);
            CheckVersion(transfer, request.ExpectedVersion);
            RequireNew(transfer);
            var source = await ValidateSourceAsync(db, scope,
                await LoadSourceLotAsync(db, scope, transfer.SourceProductionBalLotId, false, ct), ct);
            var target = await ValidateTargetAsync(db, scope, source, request.TargetWorkOrderMaterialId, true, ct);
            ApplyTargetSnapshot(transfer, target);
            transfer.TransferDateTime = request.TransferDateTime == default ? clock.Now : request.TransferDateTime;
            transfer.Remark = NormalizeRemark(request.Remark);
            transfer.ModifiedDate = clock.Now;
            transfer.ModifiedBy = User(scope);
            var link = await LoadPostingLinkAsync(db, scope, transfer, true, ct);
            link.WorkOrderId = target.Order.Uid;
            link.DocumentRevision = transfer.DocumentRevision;
            link.ProductionDocumentLineId = transfer.Uid;
            link.SnapshotRevision = target.Order.SnapshotRevision;
            link.SnapshotHash = target.Order.SnapshotHash;
            await db.SaveChangesAsync(ct);
            var document = await MapAsync(db, transfer, scope, ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(document);
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, ex.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionWorkOrderTransferDocument>("The Work Order Transfer changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> RefreshSourceSnapshotAsync(
        long id, byte[] expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Edit, true, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ProductionWorkOrderTransferWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var transfer = await LoadTransferAsync(db, scope, id, true, ct);
            CheckVersion(transfer, expectedVersion);
            RequireNew(transfer);
            var facts = await ValidateSourceAsync(db, scope,
                await LoadSourceLotAsync(db, scope, transfer.SourceProductionBalLotId, true, ct), ct);
            if (facts.Order.Uid != transfer.SourceWorkOrderId || facts.Route.Uid != transfer.SourceProducingRouteStepId
                || facts.Operation.Uid != transfer.SourceProducerOperationId || facts.Lot.ItemCode != transfer.ItemCode
                || facts.Lot.LotNo != transfer.LotNo)
                throw new TransferException("The source WIP identity changed; create a new Work Order Transfer.");
            transfer.Qty = facts.Lot.Qty;
            transfer.BaseQty = facts.Lot.BaseQty;
            transfer.TransferTotalCost = facts.Lot.TotalCost;
            transfer.SourceLastMovementDate = facts.Lot.LastMovementDate;
            transfer.SourceLastStockEventEffectiveAt = facts.Lot.LastStockEventEffectiveAt;
            transfer.SourceBalanceRowVersion = facts.Lot.RowVersion.ToArray();
            transfer.SourceSnapshotRevision = facts.Order.SnapshotRevision;
            transfer.SourceSnapshotHash = facts.Order.SnapshotHash;
            transfer.ModifiedDate = clock.Now;
            transfer.ModifiedBy = User(scope);
            await db.SaveChangesAsync(ct);
            var document = await MapAsync(db, transfer, scope, ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(document);
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, ex.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionWorkOrderTransferDocument>("The Work Order Transfer changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
    }

    public async Task<IvMasterOperationResult<bool>> DeleteAsync(long id, byte[] expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Delete, true, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ProductionWorkOrderTransferWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var transfer = await LoadTransferAsync(db, scope, id, true, ct);
            CheckVersion(transfer, expectedVersion);
            RequireNew(transfer);
            var link = await LoadPostingLinkAsync(db, scope, transfer, true, ct);
            transfer.DeletedAtUtc = DateTime.UtcNow;
            transfer.DeletedBy = User(scope);
            transfer.DeleteReason = "Deleted NEW Work Order Transfer draft";
            link.Status = ProductionPostingLinkStatuses.Cancelled;
            link.ResultCode = "DELETED";
            link.ResultMessage = transfer.DeleteReason;
            link.CompletedDate = clock.Now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<bool>.Ok(true);
        }
        catch (TransferException ex) { return Fail<bool>(ex.Message, ex.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<bool>("The Work Order Transfer changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> PostAsync(
        ProductionWorkOrderTransferCommand request, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Post, true, ct);
            if (request.RequestId == Guid.Empty || request.ExpectedVersion is null || request.ExpectedVersion.Length == 0)
                throw new TransferException("A stable request ID and document version are required.");
            const string commandType = ProductionPostingCommandTypes.WorkOrderTransferPost;
            var commandFingerprint = StockPostingFingerprint.Create(new
            {
                request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, Reason = (string?)null
            }, new { });
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ProductionWorkOrderTransferWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var replay = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.CommandType == commandType && x.RequestId == request.RequestId, ct);
            if (replay is not null)
            {
                if (replay.RequestFingerprint != commandFingerprint.RequestFingerprint)
                    throw new TransferException("The request ID was reused with different input.");
                if (replay.SealedAtUtc is null)
                    throw new TransferException("The existing posting is not sealed; reconciliation is required.");
                var recorded = await LoadTransferAsync(db, scope, request.Id, false, ct);
                var replayDocument = await MapAsync(db, recorded, scope, ct);
                await tx.CommitAsync(ct);
                return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(replayDocument);
            }

            var observed = await db.ProductionWorkOrderTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == request.Id
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DeletedAtUtc == null, ct)
                ?? throw new TransferException("Work Order Transfer was not found.", IvMasterErrorCode.NotFound);
            var workOrderIds = new[] { observed.SourceWorkOrderId, observed.TargetWorkOrderId }.Distinct().Order().ToArray();
            foreach (var workOrderId in workOrderIds)
                await ProductionWorkOrderExecutionLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, workOrderId, ct);
            var transfer = await LoadTransferAsync(db, scope, request.Id, true, ct);
            CheckVersion(transfer, request.ExpectedVersion);
            RequireNew(transfer);
            if (transfer.SourceWorkOrderId != observed.SourceWorkOrderId || transfer.TargetWorkOrderId != observed.TargetWorkOrderId)
                throw new TransferException("Work Order Transfer ownership changed while locks were being acquired. Reload and retry.", IvMasterErrorCode.Concurrency);
            var lockedOrders = new Dictionary<long, ProductionWorkOrder>();
            foreach (var workOrderId in workOrderIds)
                lockedOrders[workOrderId] = await LockWorkOrderAsync(db, scope, workOrderId, ct);
            var sourceOrder = lockedOrders[transfer.SourceWorkOrderId];
            var targetOrder = lockedOrders[transfer.TargetWorkOrderId];
            var sourceLot = await LoadSourceLotAsync(db, scope, transfer.SourceProductionBalLotId, true, ct);
            var source = await ValidateSourceAsync(db, scope, sourceLot, ct);
            ValidateSourceSnapshot(transfer, source);
            if (sourceOrder.Uid != source.Order.Uid || targetOrder.Uid != transfer.TargetWorkOrderId)
                throw new TransferException(SourceChangedError, IvMasterErrorCode.Concurrency);
            var target = await ValidateTargetAsync(db, scope, source, transfer.TargetWorkOrderMaterialId, false, ct);
            ValidateTargetSnapshot(transfer, target);
            if (transfer.TransferDateTime > clock.Now)
                throw new TransferException("Work Order Transfer cannot be posted before its effective date and time.");
            var link = await LoadPostingLinkAsync(db, scope, transfer, true, ct);
            if (link.Status != ProductionPostingLinkStatuses.Draft || link.WorkOrderId != transfer.TargetWorkOrderId
                || link.SnapshotRevision != transfer.TargetSnapshotRevision || link.SnapshotHash != transfer.TargetSnapshotHash
                || link.DocumentRevision != transfer.DocumentRevision || link.ProductionDocumentLineId != transfer.Uid)
                throw new TransferException("The Work Order Transfer posting link is inconsistent. Reconcile the document before posting.");

            ValidateChronology(transfer.TransferDateTime, source.Lot.LastMovementDate, source.Lot.LastStockEventEffectiveAt);
            var destination = await LockDestinationBalanceAsync(db, scope, transfer, ct);
            if (destination is not null)
                await ValidateDestinationAsync(db, transfer, source, target, destination, ct);

            var sourceValueBefore = StockLedgerPrecision.Money(source.Lot.TotalCost);
            var sourceBaseBefore = IvQty.Round(source.Lot.BaseQty);
            var targetValueBefore = StockLedgerPrecision.Money(destination?.TotalCost ?? 0m);
            var targetBaseBefore = IvQty.Round(destination?.BaseQty ?? 0m);
            var sourceGenerationBefore = await db.ProductionPoolValuationRows.AsNoTracking()
                .Where(x => x.ProductionBalLotId == source.Lot.Uid).Select(x => (int?)x.Generation).SingleOrDefaultAsync(ct);
            var targetGenerationBefore = destination is null ? null : await db.ProductionPoolValuationRows.AsNoTracking()
                .Where(x => x.ProductionBalLotId == destination.Uid).Select(x => (int?)x.Generation).SingleOrDefaultAsync(ct);
            var executionBefore = await CaptureExecutionProjectionAsync(db, workOrderIds, ct);
            var command = BuildPostingCommand(transfer, request, commandFingerprint.RequestFingerprint);
            var begin = await coordinator.BeginInTransactionAsync(db, command, ct);
            if (begin.Error is not null) throw new TransferException(begin.Error.Message);
            if (!begin.LedgerEnabled) throw new TransferException("Stock ledger V2 is not active for this branch.");
            if (begin.WasReplay || begin.Context is null)
                throw new TransferException("A prior Work Order Transfer posting exists without matching POSTED document evidence. Reconcile before retrying.");
            var context = begin.Context;
            await using var postingScope = new StockPostingAbortScope(coordinator, context);
            link.Status = ProductionPostingLinkStatuses.Pending;
            link.PostingOperationId = request.RequestId.ToString("N");

            if (destination is null)
            {
                destination = new ProductionBalLot
                {
                    CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, Kind = ProductionBalLotKinds.Wip,
                    ItemCode = source.Lot.ItemCode, Description = source.Lot.Description,
                    Qty = 0m, Uom = source.Lot.Uom, BaseQty = 0m, BaseUom = source.Lot.BaseUom,
                    ConversionFactorToBase = source.Lot.ConversionFactorToBase, TotalCost = 0m, AverageUnitCost = 0m,
                    WorkOrderId = target.Order.Uid, WorkOrderNo = target.Order.WorkOrderNo,
                    WarehouseCode = string.Empty, LocationCode = string.Empty,
                     LotNo = source.Lot.LotNo, PhysicalLotNo = source.Lot.LotNo,
                     PoolCode = null, LotIdentityKind = null,
                    BalanceStage = "PROCESS_WIP", OutputType = PrRouteOutputTypes.WipStocked,
                    StockStatusCode = "AVAILABLE", OriginType = "PRODUCED",
                    ProductionLocationId = target.ProducerLocation?.Id ?? transfer.TargetProducerProductionLocationId,
                    ProducingRouteStepId = target.ProducerRoute.Uid, WorkOrderOperationId = target.ProducerOperation.Uid,
                    WorkCentreCode = target.ProducerRoute.WorkCentreCode, ProcessCode = target.ProducerOperation.OperationCode
                };
                db.ProductionBalLots.Add(destination);
                await db.SaveChangesAsync(ct);
            }

            var groupId = Guid.NewGuid();
            var legs = new[]
            {
                new ProductionStockLeg(source.Lot, ProductionBalLotMovementTypes.TransferOut,
                    source.Lot.Qty, source.Lot.BaseQty, source.Order.Uid, null, source.Operation.Uid,
                    source.Route.Uid, link.Uid, ProductionDocumentTypes.WorkOrderTransfer, transfer.TransferNo,
                    $"{transfer.Uid}:OUT", 0, ExactTotalValue: source.Lot.TotalCost,
                    ValuationStatus: ProductionPoolValuationService.Verified),
                new ProductionStockLeg(destination, ProductionBalLotMovementTypes.TransferIn,
                    source.Lot.Qty, source.Lot.BaseQty, target.Order.Uid, target.Material.Uid,
                    target.ProducerOperation.Uid, target.ProducerRoute.Uid, link.Uid,
                    ProductionDocumentTypes.WorkOrderTransfer, transfer.TransferNo,
                    $"{transfer.Uid}:IN", 0, ExactTotalValue: source.Lot.TotalCost,
                    ValuationStatus: ProductionPoolValuationService.Verified)
            };
            var movements = await productionStock.ApplyAsync(context, legs, ct);
            if (movements.Count != 2) throw new TransferException("Work Order Transfer did not create exactly two production movements.");
            var outbound = movements.Single(x => x.MovementType == ProductionBalLotMovementTypes.TransferOut);
            var inbound = movements.Single(x => x.MovementType == ProductionBalLotMovementTypes.TransferIn);
            StampTransferMovement(outbound, source.Route.WorkCentreCode, source.Operation.OperationCode,
                source.Lot.ProductionLocation?.Code, groupId);
            StampTransferMovement(inbound, target.ProducerRoute.WorkCentreCode, target.ProducerOperation.OperationCode,
                target.ProducerLocation?.Code, groupId);
            AssertMovementPair(outbound, inbound, source.Lot);
            if (source.Lot.Qty != 0m || source.Lot.BaseQty != 0m || source.Lot.TotalCost != 0m
                || source.Lot.AverageUnitCost != 0m)
                throw new TransferException("The source WIP balance did not clear exactly after the full-balance transfer.");
            await db.SaveChangesAsync(ct);
            await ProductionPoolValuationService.RecordAsync(context, movements, ct);
            if (outbound.ValuationStatus != ProductionPoolValuationService.Verified
                || inbound.ValuationStatus != ProductionPoolValuationService.Verified)
                throw new TransferException("Work Order Transfer value evidence did not remain VERIFIED.");

            AssertMovementPair(outbound, inbound, source.Lot);
            var sourceAfterValue = StockLedgerPrecision.Money(source.Lot.TotalCost);
            var sourceAfterBase = IvQty.Round(source.Lot.BaseQty);
            var targetAfterValue = StockLedgerPrecision.Money(destination.TotalCost);
            var targetAfterBase = IvQty.Round(destination.BaseQty);
            if (sourceAfterBase != 0m || sourceAfterValue != 0m
                || IvQty.Round(sourceBaseBefore + targetBaseBefore) != IvQty.Round(sourceAfterBase + targetAfterBase)
                || StockLedgerPrecision.Money(sourceValueBefore + targetValueBefore) != StockLedgerPrecision.Money(sourceAfterValue + targetAfterValue)
                || IvQty.Round(source.Lot.BaseQty / source.Lot.ConversionFactorToBase) != IvQty.Round(source.Lot.Qty)
                || IvQty.Round(destination.BaseQty / destination.ConversionFactorToBase) != IvQty.Round(destination.Qty))
                throw new TransferException("Work Order Transfer quantity/value conservation failed.");

            var sourceValuationAfter = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == source.Lot.Uid, ct);
            var targetValuationAfter = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == destination.Uid, ct);
            if (sourceValuationAfter.Status != ProductionPoolValuationService.Verified
                || sourceValuationAfter.TrackedBaseQty != source.Lot.BaseQty || sourceValuationAfter.TrackedValue != source.Lot.TotalCost
                || targetValuationAfter.Status != ProductionPoolValuationService.Verified
                || targetValuationAfter.TrackedBaseQty != destination.BaseQty || targetValuationAfter.TrackedValue != destination.TotalCost)
                throw new TransferException("Work Order Transfer pool valuation does not match the live balances.");
            var sourceAvailability = await ProductionContributionAvailabilityService.ReadAsync(db, source.Lot.Uid,
                sourceValuationAfter.Generation, ct);
            if (sourceAvailability.Any(x => x.Evidence.Status != ProductionPoolValuationService.Verified
                    || x.Contribution.AvailableBaseQty < 0m)
                || IvQty.Round(sourceAvailability.Sum(x => x.Contribution.AvailableBaseQty)) != 0m)
                throw new TransferException("Source WIP contribution availability did not clear with the full-balance transfer.");
            var sourceAllocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
                .Where(x => x.OutboundMovementId == outbound.Uid)
                .SumAsync(x => (decimal?)x.BaseQty, ct) ?? 0m;
            if (IvQty.Round(sourceAllocations) != IvQty.Round(outbound.BaseQty))
                throw new TransferException("Work Order Transfer source contribution allocations do not cover the full outbound quantity.");
            await ValidateCompletePoolContributionsAsync(db, destination, ct);

            if (executionBefore != await CaptureExecutionProjectionAsync(db, workOrderIds, ct))
                throw new TransferException("Work Order Transfer changed Work Order execution quantities unexpectedly.");
            if (await db.IvTrxHistories.AnyAsync(x => x.StockPostingId == context.Posting.Id, ct)
                || await db.StockValuationFacts.AnyAsync(x => x.StockPostingId == context.Posting.Id, ct)
                || await db.ProductionMaterialMovements.AnyAsync(x => x.StockPostingId == context.Posting.Id, ct))
                throw new TransferException("Work Order Transfer created an unrelated Inventory or material execution fact.");

            db.ProductionWorkOrderTransferFacts.Add(new ProductionWorkOrderTransferFact
            {
                TransferId = transfer.Uid, DocumentRevision = transfer.DocumentRevision,
                StockPostingId = context.Posting.Id, ProductionPostingLinkId = link.Uid,
                SourceProductionBalLotId = source.Lot.Uid, DestinationProductionBalLotId = destination.Uid,
                SourceLegMovementId = outbound.Uid, TargetLegMovementId = inbound.Uid, MovementGroupId = groupId,
                Qty = outbound.Qty, BaseQty = outbound.BaseQty, BaseUom = outbound.BaseUom,
                TotalValue = outbound.TotalCost, UnitCost = outbound.UnitCost,
                SourceBaseBefore = sourceBaseBefore, SourceValueBefore = sourceValueBefore,
                SourceBaseAfter = sourceAfterBase, SourceValueAfter = sourceAfterValue,
                TargetBaseBefore = targetBaseBefore, TargetValueBefore = targetValueBefore,
                TargetBaseAfter = targetAfterBase, TargetValueAfter = targetAfterValue,
                SourceGenerationBefore = sourceGenerationBefore, SourceGenerationAfter = sourceValuationAfter.Generation,
                TargetGenerationBefore = targetGenerationBefore, TargetGenerationAfter = targetValuationAfter.Generation,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            if (await db.ProductionWorkOrderTransferFacts.CountAsync(x => x.TransferId == transfer.Uid
                    && x.DocumentRevision == transfer.DocumentRevision && x.StockPostingId == context.Posting.Id
                    && x.ReversesFactId == null, ct) != 1)
                throw new TransferException("Work Order Transfer immutable posting fact is missing or duplicated.");

            transfer.Status = ProductionWorkOrderTransferStatuses.Posted;
            transfer.StockPostingId = context.Posting.Id;
            transfer.DestinationProductionBalLotId = destination.Uid;
            transfer.TransferOutMovementId = outbound.Uid;
            transfer.TransferInMovementId = inbound.Uid;
            transfer.MovementGroupId = groupId;
            transfer.ReversalPostingId = null;
            transfer.ReversalReason = null;
            transfer.RollbackDate = null;
            transfer.RollbackBy = null;
            transfer.PostedDate = clock.Now;
            transfer.PostedBy = User(scope);
            link.Status = ProductionPostingLinkStatuses.Succeeded;
            link.ResultCode = "POSTED";
            link.ResultMessage = "Work Order Transfer posted.";
            link.CompletedDate = transfer.PostedDate;
            await db.SaveChangesAsync(ct);
            await coordinator.CompleteInTransactionAsync(context, ct);
            postingScope.MarkCompleted();
            if (context.Posting.SealedAtUtc is null)
                throw new TransferException("The Work Order Transfer stock posting was not sealed.");
            var document = await MapAsync(db, transfer, scope, ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(document);
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, ex.Code); }
        catch (StockLedgerException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Error.Message); }
        catch (ProductionWorkOrderExecutionLockException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, IvMasterErrorCode.Concurrency); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionWorkOrderTransferDocument>(SourceChangedError, IvMasterErrorCode.Concurrency); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Fail<ProductionWorkOrderTransferDocument>(TargetCollisionError, IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message); }
    }

    private static StockPostingCommand BuildPostingCommand(ProductionWorkOrderTransfer transfer,
        ProductionWorkOrderTransferCommand request, string requestFingerprint)
    {
        var effectiveAt = transfer.TransferDateTime;
        var sourceSnapshot = new
        {
            transfer.Uid, transfer.TransferNo, transfer.DocumentRevision,
            Source = new { transfer.SourceProductionBalLotId, transfer.SourceWorkOrderId, transfer.SourceProducingRouteStepId,
                transfer.SourceProducerOperationId, transfer.SourceSnapshotRevision, transfer.SourceSnapshotHash,
                transfer.Qty, transfer.BaseQty, transfer.TransferTotalCost, transfer.Uom, transfer.BaseUom,
                transfer.ConversionFactorToBase, transfer.PhysicalLotNo, transfer.SourceLastMovementDate,
                transfer.SourceLastStockEventEffectiveAt, SourceBalanceRowVersion = Convert.ToBase64String(transfer.SourceBalanceRowVersion) },
            Target = new { transfer.TargetWorkOrderId, transfer.TargetWorkOrderMaterialId, transfer.TargetProducingRouteStepId,
                transfer.TargetProducerOperationId, transfer.TargetConsumingRouteStepId, transfer.TargetConsumingOperationId,
                transfer.TargetSnapshotRevision, transfer.TargetSnapshotHash },
            EffectiveAt = effectiveAt
        };
        var snapshotJson = StockPostingFingerprint.Canonicalize(System.Text.Json.JsonSerializer.Serialize(sourceSnapshot,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        var evidence = new StockPostingEvidence(requestFingerprint, snapshotJson, StockPostingFingerprint.Hash(snapshotJson));
        return new StockPostingCommand
        {
            RequestId = request.RequestId, CommandType = ProductionPostingCommandTypes.WorkOrderTransferPost,
            SourceModule = "PRODUCTION", SourceDocumentType = ProductionDocumentTypes.WorkOrderTransfer,
            SourceDocumentId = transfer.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceDocumentNo = transfer.TransferNo, DocumentRevision = transfer.DocumentRevision,
            PostingRole = "PRIMARY", EffectiveAt = effectiveAt, Evidence = evidence,
            ProductionPostingLinkId = transfer.ProductionPostingLinkId
        };
    }

    private static void ValidateSourceSnapshot(ProductionWorkOrderTransfer transfer, SourceFacts source)
    {
        var lot = source.Lot;
        if (!lot.RowVersion.SequenceEqual(transfer.SourceBalanceRowVersion)
            || lot.Qty != transfer.Qty || lot.BaseQty != transfer.BaseQty || lot.TotalCost != transfer.TransferTotalCost
            || lot.ItemCode != transfer.ItemCode || lot.LotNo != transfer.LotNo
            || lot.Uom != transfer.Uom || lot.BaseUom != transfer.BaseUom
            || lot.ConversionFactorToBase != transfer.ConversionFactorToBase
            || lot.PhysicalLotNo != transfer.PhysicalLotNo || lot.PoolCode != transfer.PoolCode
            || lot.LotIdentityKind != transfer.LotIdentityKind
            || lot.ProductionLocationId != transfer.SourceProductionLocationId
            || lot.LastMovementDate != transfer.SourceLastMovementDate
            || lot.LastStockEventEffectiveAt != transfer.SourceLastStockEventEffectiveAt
            || source.Order.Uid != transfer.SourceWorkOrderId || source.Route.Uid != transfer.SourceProducingRouteStepId
            || transfer.SourceSnapshotRevision is not int snapshotRevision || snapshotRevision != source.Order.SnapshotRevision
            || !string.Equals(transfer.SourceSnapshotHash, source.Order.SnapshotHash, StringComparison.Ordinal)
            || source.Order.WorkOrderNo != transfer.SourceWorkOrderNo
            || source.Route.WorkCentreCode != transfer.SourceWorkCentreCode
            || source.Operation.Uid != transfer.SourceProducerOperationId
            || source.Operation.OperationCode != transfer.SourceProcessCode)
            throw new TransferException(SourceChangedError, IvMasterErrorCode.Concurrency);
    }

    private static void ValidateTargetSnapshot(ProductionWorkOrderTransfer transfer, TargetFacts target)
    {
        if (target.Order.SnapshotRevision != transfer.TargetSnapshotRevision
            || !string.Equals(target.Order.SnapshotHash, transfer.TargetSnapshotHash, StringComparison.Ordinal)
            || !ProductionSnapshotHashVersions.SupportsDailyProduction(target.Order.SnapshotHashVersion)
            || target.Order.Uid != transfer.TargetWorkOrderId
            || target.Order.WorkOrderNo != transfer.TargetWorkOrderNo
            || target.Material.Uid != transfer.TargetWorkOrderMaterialId
            || target.Material.ComponentCode != transfer.ItemCode
            || target.Material.SupplySource != PrMaterialSupplySources.InternalRouteWip
            || target.ProducerRoute.Uid != transfer.TargetProducingRouteStepId
            || target.ProducerOperation.Uid != transfer.TargetProducerOperationId
            || target.ConsumerRoute.Uid != transfer.TargetConsumingRouteStepId
            || target.ConsumerOperation.Uid != transfer.TargetConsumingOperationId
            || target.ProducerRoute.WorkCentreCode != transfer.TargetProducerWorkCentreCode
            || target.ProducerOperation.OperationCode != transfer.TargetProducerProcessCode
            || target.ConsumerRoute.WorkCentreCode != transfer.TargetConsumingWorkCentreCode
            || target.ConsumerOperation.OperationCode != transfer.TargetConsumingProcessCode
            || target.ProducerLocation?.Id != transfer.TargetProducerProductionLocationId)
            throw new TransferException(SnapshotChangedError, IvMasterErrorCode.Concurrency);
    }

    private static void ValidateChronology(DateTime effectiveAt, DateTime? lastMovementDate, DateTime? lastStockEvent)
    {
        if (lastMovementDate > effectiveAt || lastStockEvent > effectiveAt)
            throw new TransferException(ChronologyError);
    }

    private static async Task<ProductionBalLot?> LockDestinationBalanceAsync(AppDbContext db,
        InventoryTenantScope scope, ProductionWorkOrderTransfer transfer, CancellationToken ct)
    {
        IQueryable<ProductionBalLot> q = db.Database.IsSqlServer()
            ? db.ProductionBalLots.FromSqlInterpolated($@"
SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK,HOLDLOCK)
WHERE CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}
  AND Kind={ProductionBalLotKinds.Wip} AND WorkOrderID={transfer.TargetWorkOrderId}
  AND ProducingRouteStepID={transfer.TargetProducingRouteStepId}
  AND ItemCode={transfer.ItemCode} AND LotNo={transfer.LotNo}")
            : db.ProductionBalLots.Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.Kind == ProductionBalLotKinds.Wip && x.WorkOrderId == transfer.TargetWorkOrderId
                && x.ProducingRouteStepId == transfer.TargetProducingRouteStepId
                && x.ItemCode == transfer.ItemCode && x.LotNo == transfer.LotNo);
        return await q.SingleOrDefaultAsync(ct);
    }

    private async Task ValidateDestinationAsync(AppDbContext db, ProductionWorkOrderTransfer transfer,
        SourceFacts source, TargetFacts target, ProductionBalLot destination, CancellationToken ct)
    {
        var physicalLotCompatible = string.IsNullOrWhiteSpace(destination.PhysicalLotNo)
            || destination.PhysicalLotNo == source.Lot.LotNo;
        if (destination.Kind != ProductionBalLotKinds.Wip || destination.WorkOrderId != target.Order.Uid
            || destination.ProducingRouteStepId != target.ProducerRoute.Uid
            || destination.WorkOrderOperationId != target.ProducerOperation.Uid
            || destination.WorkOrderMaterialId is not null || destination.ItemCode != source.Lot.ItemCode
            || destination.LotNo != source.Lot.LotNo || !physicalLotCompatible
            || destination.Uom != source.Lot.Uom || destination.BaseUom != source.Lot.BaseUom
            || destination.ConversionFactorToBase != source.Lot.ConversionFactorToBase
            || destination.BalanceStage != "PROCESS_WIP" || destination.OutputType != PrRouteOutputTypes.WipStocked
            || destination.StockStatusCode != "AVAILABLE" || destination.OriginType != "PRODUCED"
            || destination.ProductionLocationId != target.ProducerLocation?.Id
            || destination.WorkCentreCode != target.ProducerRoute.WorkCentreCode
            || destination.ProcessCode != target.ProducerOperation.OperationCode)
            throw new TransferException(TargetCollisionError);
        if (destination.Qty < 0m || destination.BaseQty < 0m || destination.TotalCost < 0m
            || destination.ConversionFactorToBase <= 0m
            || IvQty.Round(destination.BaseQty / destination.ConversionFactorToBase) != IvQty.Round(destination.Qty))
            throw new TransferException(TargetCollisionError);
        if (destination.BaseQty == 0m || destination.Qty == 0m)
        {
            if (destination.BaseQty != 0m || destination.Qty != 0m || destination.TotalCost != 0m
                || destination.AverageUnitCost != 0m)
                throw new TransferException(TargetCollisionError);
        }
        else if (await ProductionCostReadiness.PoolValuationError(db, destination, ct) is { } costError)
        {
            throw new TransferException(costError);
        }
        else if (destination.BaseQty > 0m
            && StockLedgerPrecision.Money(destination.TotalCost / destination.BaseQty) != destination.AverageUnitCost)
        {
            throw new TransferException(TargetCollisionError);
        }
        if (destination.BaseQty > 0m)
            await ValidateCompletePoolContributionsAsync(db, destination, ct);
        ValidateChronology(transfer.TransferDateTime, destination.LastMovementDate, destination.LastStockEventEffectiveAt);
    }

    private static void StampTransferMovement(ProductionBalLotMovement movement, string workCentre,
        string process, string? locationCode, Guid groupId)
    {
        movement.MovementGroupId = groupId;
        movement.WorkCentreCode = workCentre;
        movement.ProcessCode = process;
        movement.ProductionLocationCode = locationCode;
        movement.ValuationStatus = ProductionPoolValuationService.Verified;
    }

    private static void AssertMovementPair(ProductionBalLotMovement outbound,
        ProductionBalLotMovement inbound, ProductionBalLot source)
    {
        if (outbound.MovementType != ProductionBalLotMovementTypes.TransferOut
            || inbound.MovementType != ProductionBalLotMovementTypes.TransferIn
            || outbound.StockPostingId != inbound.StockPostingId
            || outbound.MovementGroupId is null || outbound.MovementGroupId != inbound.MovementGroupId
            || outbound.ItemCode != inbound.ItemCode || outbound.ItemCode != source.ItemCode
            || outbound.Qty != inbound.Qty || outbound.BaseQty != inbound.BaseQty
            || outbound.TotalCost != inbound.TotalCost || outbound.BaseUom != inbound.BaseUom
            || outbound.ConversionFactorToBase != inbound.ConversionFactorToBase
            || outbound.OriginalMovementId is not null || inbound.OriginalMovementId is not null)
            throw new TransferException("Work Order Transfer movement pair does not reconcile.");
    }

    private static async Task<ProductionPostingLink> LoadPostingLinkAsync(AppDbContext db,
        InventoryTenantScope scope, ProductionWorkOrderTransfer transfer, bool locked, CancellationToken ct)
    {
        IQueryable<ProductionPostingLink> q = db.ProductionPostingLinks.Where(x => x.Uid == transfer.ProductionPostingLinkId
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        if (locked && db.Database.IsSqlServer())
            q = db.ProductionPostingLinks.FromSqlInterpolated($@"
SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK,HOLDLOCK)
WHERE UID={transfer.ProductionPostingLinkId} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}");
        return await q.SingleOrDefaultAsync(ct)
            ?? throw new TransferException("The Work Order Transfer posting link is missing.");
    }

    private static async Task EnsurePostedEvidenceAsync(AppDbContext db, InventoryTenantScope scope,
        ProductionWorkOrderTransfer transfer, CancellationToken ct)
    {
        if (transfer.StockPostingId is not long postingId || transfer.DestinationProductionBalLotId is not long targetId
            || transfer.TransferOutMovementId is not long outId || transfer.TransferInMovementId is not long inId
            || transfer.MovementGroupId is not Guid groupId)
            throw new TransferException("POSTED Work Order Transfer evidence is incomplete. Reconcile before retrying.");
        var posting = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == postingId
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.SourceDocumentType == ProductionDocumentTypes.WorkOrderTransfer
            && x.SourceDocumentId == transfer.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && x.SealedAtUtc != null, ct);
        var pair = await db.ProductionBalLotMovements.AsNoTracking().Where(x => x.Uid == outId || x.Uid == inId)
            .OrderBy(x => x.Uid).ToListAsync(ct);
        if (posting is null || pair.Count != 2
            || pair.Count(x => x.MovementType == ProductionBalLotMovementTypes.TransferOut) != 1
            || pair.Count(x => x.MovementType == ProductionBalLotMovementTypes.TransferIn) != 1
            || pair.Any(x => x.StockPostingId != postingId || x.MovementGroupId != groupId
                || x.DocumentType != ProductionDocumentTypes.WorkOrderTransfer))
            throw new TransferException("POSTED Work Order Transfer evidence is inconsistent. Reconcile before retrying.");
        if (!await db.ProductionBalLots.AsNoTracking().AnyAsync(x => x.Uid == targetId
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.WorkOrderId == transfer.TargetWorkOrderId
            && x.ProducingRouteStepId == transfer.TargetProducingRouteStepId, ct))
            throw new TransferException("POSTED Work Order Transfer destination is missing. Reconcile before retrying.");
    }

    private static void ApplyTargetSnapshot(ProductionWorkOrderTransfer transfer, TargetFacts target)
    {
        transfer.TargetWorkOrderId = target.Order.Uid;
        transfer.TargetWorkOrderNo = target.Order.WorkOrderNo;
        transfer.TargetProducingRouteStepId = target.ProducerRoute.Uid;
        transfer.TargetProducerOperationId = target.ProducerOperation.Uid;
        transfer.TargetProducerWorkCentreCode = target.ProducerRoute.WorkCentreCode;
        transfer.TargetProducerProcessCode = target.ProducerOperation.OperationCode;
        transfer.TargetProducerProductionLocationId = target.ProducerLocation!.Id;
        transfer.TargetWorkOrderMaterialId = target.Material.Uid;
        transfer.TargetConsumingRouteStepId = target.ConsumerRoute.Uid;
        transfer.TargetConsumingOperationId = target.ConsumerOperation.Uid;
        transfer.TargetConsumingWorkCentreCode = target.ConsumerRoute.WorkCentreCode;
        transfer.TargetConsumingProcessCode = target.ConsumerOperation.OperationCode;
        transfer.TargetConsumerProductionLocationId = target.ConsumerLocation?.Id;
        transfer.TargetSnapshotRevision = target.Order.SnapshotRevision;
        transfer.TargetSnapshotHash = target.Order.SnapshotHash;
    }

    private static string? NormalizeRemark(string? value)
    {
        var result = value?.Trim();
        if (result?.Length > 250) throw new TransferException("Remark cannot exceed 250 characters.");
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static void RequireNew(ProductionWorkOrderTransfer transfer)
    {
        if (transfer.Status != ProductionWorkOrderTransferStatuses.New || transfer.DeletedAtUtc is not null)
            throw new TransferException("Only a NEW Work Order Transfer can be changed or posted.");
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("2601", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("2627", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<ProductionWorkOrderTransferDocument> MapAsync(AppDbContext db,
        ProductionWorkOrderTransfer transfer, InventoryTenantScope scope, CancellationToken ct)
    {
        var canViewCost = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
        var source = await db.ProductionBalLots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Uid == transfer.SourceProductionBalLotId && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, ct);
        var sourceOrder = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Uid == transfer.SourceWorkOrderId && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, ct);
        var target = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Uid == transfer.TargetWorkOrderId && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, ct);
        var consumerOperation = await db.ProductionWorkOrderOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == transfer.TargetConsumingOperationId, ct);
        var targetSequenceReady = true;
        string? targetSequenceBlockingReason = null;
        if (target is not null && consumerOperation is not null)
        {
            var routeSteps = await db.ProductionWorkOrderRouteSteps.AsNoTracking().Where(x => x.WorkOrderId == target.Uid).ToListAsync(ct);
            var routeStepIds = routeSteps.Select(x => x.Uid).ToArray();
            var operations = await db.ProductionWorkOrderOperations.AsNoTracking()
                .Where(x => x.WorkOrderId == target.Uid || x.RouteStepId != null && routeStepIds.Contains(x.RouteStepId.Value))
                .ToListAsync(ct);
            var readiness = _operationEligibility.Evaluate(consumerOperation, routeSteps, operations);
            targetSequenceReady = readiness.IsEligible;
            targetSequenceBlockingReason = readiness.BlockingReason;
        }
        var targetBalance = await db.ProductionBalLots.AsNoTracking().SingleOrDefaultAsync(x =>
            x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.Kind == ProductionBalLotKinds.Wip && x.WorkOrderId == transfer.TargetWorkOrderId
            && x.ProducingRouteStepId == transfer.TargetProducingRouteStepId
            && x.ItemCode == transfer.ItemCode && x.LotNo == transfer.LotNo, ct);
        var targetValue = canViewCost ? targetBalance?.TotalCost ?? 0m : (decimal?)null;
        return new ProductionWorkOrderTransferDocument
        {
            Id = transfer.Uid, TransferNo = transfer.TransferNo, Status = transfer.Status,
            TransferDateTime = transfer.TransferDateTime, Remark = transfer.Remark,
            SourceProductionBalLotId = transfer.SourceProductionBalLotId,
            SourceWorkOrderNo = transfer.SourceWorkOrderNo,
            SourceWorkOrderStatus = sourceOrder?.Status ?? string.Empty,
            ItemCode = transfer.ItemCode, ItemDescription = source?.Description ?? string.Empty,
            LotNo = transfer.LotNo, Qty = transfer.Qty, Uom = transfer.Uom,
            BaseQty = transfer.BaseQty, BaseUom = transfer.BaseUom,
            SourceWorkCentreCode = transfer.SourceWorkCentreCode,
            SourceProcessCode = transfer.SourceProcessCode,
            SourceLastMovementDate = transfer.SourceLastMovementDate,
            SourceCurrentWipValue = canViewCost ? source?.TotalCost : null,
            TransferTotalCost = canViewCost ? transfer.TransferTotalCost : null,
            TargetWorkOrderId = transfer.TargetWorkOrderId, TargetWorkOrderNo = transfer.TargetWorkOrderNo,
            TargetProductCode = target?.ProductCode ?? string.Empty,
            TargetProductDescription = target?.ProductDescription,
            TargetWorkOrderStatus = target?.Status ?? string.Empty,
            TargetWorkOrderMaterialId = transfer.TargetWorkOrderMaterialId,
            TargetConsumingWorkCentreCode = transfer.TargetConsumingWorkCentreCode,
            TargetConsumingProcessCode = transfer.TargetConsumingProcessCode,
            TargetProducerWorkCentreCode = transfer.TargetProducerWorkCentreCode,
            TargetProducerProcessCode = transfer.TargetProducerProcessCode,
            TargetCurrentWipValue = targetValue,
            ProjectedTargetWipValue = canViewCost ? (targetBalance?.TotalCost ?? 0m) + transfer.TransferTotalCost : null,
            CanViewCost = canViewCost, TargetSnapshotRevision = transfer.TargetSnapshotRevision,
            TargetSnapshotHash = transfer.TargetSnapshotHash, RowVersion = transfer.RowVersion,
            DocumentRevision = transfer.DocumentRevision, ReversalPostingId = transfer.ReversalPostingId,
            ReversalReason = transfer.ReversalReason, RollbackDate = transfer.RollbackDate,
            RollbackBy = transfer.RollbackBy, RollbackCount = transfer.RollbackCount,
            TargetSequenceReady = targetSequenceReady, TargetSequenceBlockingReason = targetSequenceBlockingReason,
            StockPostingId = transfer.StockPostingId, PostedDate = transfer.PostedDate, PostedBy = transfer.PostedBy
        };
    }
}
