using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Reads customer fulfilment facts and manages DR soft reservations. Physical inventory,
/// shipment posting, production posting and costing remain owned by their existing services.
/// </summary>
public sealed class SaDeliveryRequestFulfilmentService : ISaDeliveryRequestFulfilmentService
{
    private const decimal QuantityTolerance = 0.0001m;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly ICurrentDateService _dates;
    private readonly IIvStockPostingRepository _postingRepository;
    private readonly IInventorySoftReservationReader _softReservations;

    public SaDeliveryRequestFulfilmentService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        ICurrentDateService dates,
        IIvStockPostingRepository postingRepository,
        IInventorySoftReservationReader softReservations)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _dates = dates;
        _postingRepository = postingRepository;
        _softReservations = softReservations;
    }

    public async Task<IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>> GetFactsAsync(
        long deliveryRequestId,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
        {
            return IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>.Fail(
                IvMasterErrorCode.InvalidScope,
                "A valid company and branch context is required.");
        }

        if (deliveryRequestId <= 0)
        {
            return IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>.Fail(
                IvMasterErrorCode.Validation,
                "Delivery Request is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.SaDeliveryRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == deliveryRequestId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode, cancellationToken);
        if (header is null)
        {
            return IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>.Fail(
                IvMasterErrorCode.NotFound,
                "Delivery Request not found.");
        }

        return IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>.Ok(
            await BuildFactsAsync(db, header, scope, cancellationToken));
    }

    public async Task<IvMasterOperationResult<IReadOnlyDictionary<long, SaDeliveryRequestFulfilmentFacts>>> GetFactsBatchAsync(
        IReadOnlyCollection<long> deliveryRequestIds,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
        {
            return IvMasterOperationResult<IReadOnlyDictionary<long, SaDeliveryRequestFulfilmentFacts>>.Fail(
                IvMasterErrorCode.InvalidScope,
                "A valid company and branch context is required.");
        }

        var ids = deliveryRequestIds
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return IvMasterOperationResult<IReadOnlyDictionary<long, SaDeliveryRequestFulfilmentFacts>>.Ok(
                new Dictionary<long, SaDeliveryRequestFulfilmentFacts>());
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var headers = await db.SaDeliveryRequests.AsNoTracking()
            .Where(x => ids.Contains(x.Uid)
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyDictionary<long, SaDeliveryRequestFulfilmentFacts>>.Ok(
            await BuildFactsBatchAsync(db, headers, scope, cancellationToken));
    }

    public async Task<IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>> ReconcileAsync(
        long deliveryRequestId,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryWriteScope();
        if (scope?.BranchCode is null || string.IsNullOrWhiteSpace(scope.LocationCode))
        {
            return IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>.Fail(
                IvMasterErrorCode.InvalidScope,
                "A valid company, branch and location context is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var facts = await ReconcileInTransactionAsync(db, deliveryRequestId, scope, cancellationToken);
            TouchSqliteRowVersions(db);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            await using var readDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var header = await readDb.SaDeliveryRequests.AsNoTracking()
                .SingleAsync(x => x.Uid == deliveryRequestId
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode, cancellationToken);
            return IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>.Ok(
                await BuildFactsAsync(readDb, header, scope, cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<SaDeliveryRequestFulfilmentFacts>.Fail(
                IvMasterErrorCode.Concurrency,
                "Fulfilment changed by another transaction. Reload and try again.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<SaDeliveryRequestFulfilmentFacts> ReconcileInTransactionAsync(
        AppDbContext db,
        long deliveryRequestId,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        var header = await LoadDeliveryRequestForUpdateAsync(db, deliveryRequestId, scope, cancellationToken)
            ?? throw new InvalidOperationException("Delivery Request not found.");
        var sources = await db.SaDeliveryRequestSources
            .Where(x => x.DeliveryRequestId == header.Uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .OrderBy(x => x.RequestedDeliveryDate)
            .ThenBy(x => x.SoNo)
            .ThenBy(x => x.CustRel)
            .ThenBy(x => x.SoLine)
            .ToListAsync(cancellationToken);

        if (header.Status is not (SaDeliveryRequestStatuses.Released or SaDeliveryRequestStatuses.InProduction)
            || string.IsNullOrWhiteSpace(header.WarehouseCode)
            || sources.Count == 0)
        {
            await ReleaseAllInTransactionAsync(db, header.Uid, scope, "DR is not actively released.", cancellationToken);
            return await BuildFactsAsync(db, header, scope, cancellationToken);
        }

        var aggregate = await LoadAggregateAsync(db, header, sources, scope, cancellationToken);
        var target = Math.Max(
            aggregate.OpenDemandQty
                - aggregate.NewShipmentReservedQty
                - aggregate.OutstandingWoSupplyQty,
            0m);

        var currentRows = await db.SaDeliveryRequestStockReservations
            .Where(x => x.DeliveryRequestId == header.Uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.IsActive)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);

        var candidateRows = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.ICode == header.ProductCode
                && x.WhCode == header.WarehouseCode
                && x.StdQty > 0m
                && x.IStatus == IvItemStatuses.Active
                && x.TransDate != null
                && x.TransDate < _dates.Today.Date.AddDays(1)
                && (x.LocationCode == scope.LocationCode || x.LocationCode == null || x.LocationCode == ""))
            .ToListAsync(cancellationToken);

        var allIds = candidateRows.Select(x => x.Id)
            .Concat(currentRows.Select(x => x.BalLocId))
            .Distinct()
            .ToList();
        var locked = await LockBalancesInSliceOrderAsync(db, scope, allIds, cancellationToken);
        if (locked is null)
        {
            throw new InvalidOperationException("A stock balance could not be locked for fulfilment reservation.");
        }

        var reservationTotals = await _softReservations.GetReservedByBalanceAsync(
            db,
            scope.CompanyCode,
            scope.BranchCode!,
            scope.LocationCode!,
            locked.Keys.ToList(),
            cancellationToken: cancellationToken);
        var ownByBalance = currentRows
            .GroupBy(x => x.BalLocId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.ReservedQty));
        var availableByBalance = new Dictionary<int, decimal>();
        foreach (var pair in locked)
        {
            var soft = reservationTotals.GetValueOrDefault(pair.Key);
            availableByBalance[pair.Key] = IvQty.Round(Math.Max(
                0m,
                pair.Value.StdQty
                    - (soft?.TotalReservedQty ?? 0m)
                    + ownByBalance.GetValueOrDefault(pair.Key)));
        }

        var desired = new Dictionary<(long SourceId, int BalLocId), decimal>();
        var remainingTarget = target;
        foreach (var source in sources.Where(x => x.IsActive))
        {
            if (remainingTarget <= QuantityTolerance)
            {
                break;
            }

            var sourceDelivered = aggregate.DeliveredBySource.GetValueOrDefault(source.Uid);
            var sourceShipment = aggregate.NewShipmentBySource.GetValueOrDefault(source.Uid);
            var sourceNeed = Math.Max(source.AllocatedProductionQty - sourceDelivered - sourceShipment, 0m);
            var remainingSource = Math.Min(sourceNeed, remainingTarget);
            if (remainingSource <= QuantityTolerance)
            {
                continue;
            }

            foreach (var pile in candidateRows
                .Where(x => x.Id != 0 && locked.ContainsKey(x.Id))
                .Where(x => IvSpFifoEligibility.MatchesCandidate(
                    locked[x.Id],
                    scope.CompanyCode,
                    scope.BranchCode!,
                    scope.LocationCode!,
                    header.ProductCode,
                    header.WarehouseCode!,
                    _dates.Today.Date))
                .OrderBy(x => x.TransDate)
                .ThenBy(x => x.LotNo)
                .ThenBy(x => x.Id))
            {
                if (remainingSource <= QuantityTolerance || remainingTarget <= QuantityTolerance)
                {
                    break;
                }

                var available = availableByBalance.GetValueOrDefault(pile.Id);
                var take = IvQty.Round(Math.Min(remainingSource, Math.Min(remainingTarget, available)));
                if (take <= QuantityTolerance)
                {
                    continue;
                }

                desired[(source.Uid, pile.Id)] = desired.GetValueOrDefault((source.Uid, pile.Id)) + take;
                availableByBalance[pile.Id] = IvQty.Round(available - take);
                remainingSource = IvQty.Round(remainingSource - take);
                remainingTarget = IvQty.Round(remainingTarget - take);
            }
        }

        ApplyReservationChanges(db, header.Uid, currentRows, desired, scope, DateTime.UtcNow, "Fulfilment reconcile");
        var reconciledReserved = IvQty.Round(desired.Values.Sum());
        var reconciledReady = IvQty.Round(Math.Min(
            aggregate.OpenDemandQty,
            reconciledReserved + aggregate.NewShipmentReservedQty));
        var reconciledProductionRequired = Math.Max(
            aggregate.OpenDemandQty - reconciledReserved - aggregate.NewShipmentReservedQty,
            0m);
        var reconciledProductionUnplanned = Math.Max(
            reconciledProductionRequired - aggregate.OutstandingWoSupplyQty,
            0m);
        return ToFacts(
            header,
            aggregate with
            {
                DrStockReservedQty = reconciledReserved,
                ReadyQty = reconciledReady,
                ProductionRequiredQty = reconciledProductionRequired,
                ProductionUnplannedQty = reconciledProductionUnplanned
            });
    }

    public async Task ReleaseAllInTransactionAsync(
        AppDbContext db,
        long deliveryRequestId,
        InventoryTenantScope scope,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.SaDeliveryRequestStockReservations
            .Where(x => x.DeliveryRequestId == deliveryRequestId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.IsActive)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.IsActive = false;
            row.ReleasedDate = now;
            row.ReleasedBy = scope.UserId;
            row.ReleaseReason = reason;
        }
    }

    public async Task TransferToShipmentInTransactionAsync(
        AppDbContext db,
        long deliveryRequestSourceId,
        decimal quantity,
        InventoryTenantScope scope,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var remaining = IvQty.Round(quantity);
        if (remaining <= QuantityTolerance)
        {
            return;
        }

        var rows = await db.SaDeliveryRequestStockReservations
            .Where(x => x.DeliveryRequestSourceId == deliveryRequestSourceId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.IsActive)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            if (remaining <= QuantityTolerance)
            {
                break;
            }

            var transfer = Math.Min(remaining, row.ReservedQty);
            row.IsActive = false;
            row.ReleasedDate = now;
            row.ReleasedBy = scope.UserId;
            row.ReleaseReason = reason;
            remaining = IvQty.Round(remaining - transfer);

            var remainder = IvQty.Round(row.ReservedQty - transfer);
            if (remainder > QuantityTolerance)
            {
                db.SaDeliveryRequestStockReservations.Add(new SaDeliveryRequestStockReservation
                {
                    DeliveryRequestId = row.DeliveryRequestId,
                    DeliveryRequestSourceId = row.DeliveryRequestSourceId,
                    CompanyCode = row.CompanyCode,
                    BranchCode = row.BranchCode,
                    BalLocId = row.BalLocId,
                    ReservedQty = remainder,
                    IsActive = true,
                    CreatedDate = now,
                    CreatedBy = scope.UserId
                });
            }
        }
    }

    public async Task ReconcileForWorkOrderInTransactionAsync(
        AppDbContext db,
        long workOrderId,
        InventoryTenantScope scope,
        CancellationToken cancellationToken = default)
    {
        if (workOrderId <= 0)
        {
            return;
        }

        var deliveryRequestIds = await db.PrWorkOrderDemandAllocations.AsNoTracking()
            .Where(x => x.WorkOrderId == workOrderId
                && x.DeliveryRequestId > 0
                && x.IsActive
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .Select(x => x.DeliveryRequestId)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        foreach (var deliveryRequestId in deliveryRequestIds)
        {
            await ReconcileInTransactionAsync(db, deliveryRequestId, scope, cancellationToken);
        }
    }

    private async Task<SaDeliveryRequestFulfilmentFacts> BuildFactsAsync(
        AppDbContext db,
        SaDeliveryRequest header,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var sources = await db.SaDeliveryRequestSources.AsNoTracking()
            .Where(x => x.DeliveryRequestId == header.Uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .ToListAsync(cancellationToken);
        var aggregate = await LoadAggregateAsync(db, header, sources, scope, cancellationToken);
        return ToFacts(header, aggregate);
    }

    private async Task<IReadOnlyDictionary<long, SaDeliveryRequestFulfilmentFacts>> BuildFactsBatchAsync(
        AppDbContext db,
        IReadOnlyList<SaDeliveryRequest> headers,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        if (headers.Count == 0)
        {
            return new Dictionary<long, SaDeliveryRequestFulfilmentFacts>();
        }

        var headerIds = headers.Select(x => x.Uid).ToArray();
        var sources = await db.SaDeliveryRequestSources.AsNoTracking()
            .Where(x => headerIds.Contains(x.DeliveryRequestId)
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .ToListAsync(cancellationToken);
        var sourceIds = sources.Select(x => x.Uid).Where(x => x > 0).Distinct().ToArray();
        var sourceToRequest = sources.ToDictionary(x => x.Uid, x => x.DeliveryRequestId);

        var deliveryRows = sourceIds.Length == 0
            ? []
            : await (
                from detail in db.SaDoDetails.AsNoTracking()
                join deliveryOrder in db.SaDos.AsNoTracking()
                    on new { detail.CompanyCode, detail.BranchCode, detail.DoNo }
                    equals new { deliveryOrder.CompanyCode, deliveryOrder.BranchCode, deliveryOrder.DoNo }
                where detail.DeliveryRequestSourceId.HasValue
                    && sourceIds.Contains(detail.DeliveryRequestSourceId.Value)
                    && deliveryOrder.DeletedAtUtc == null
                    && (deliveryOrder.Status == SaDoStatuses.Posted || deliveryOrder.Status == SaDoStatuses.Closed)
                select new
                {
                    SourceId = detail.DeliveryRequestSourceId!.Value,
                    Qty = detail.StdQty,
                    deliveryOrder.PostedDate
                }).ToListAsync(cancellationToken);

        var linkedNewDoLines = sourceIds.Length == 0
            ? []
            : await (
                from detail in db.SaDoDetails.AsNoTracking()
                join deliveryOrder in db.SaDos.AsNoTracking()
                    on new { detail.CompanyCode, detail.BranchCode, detail.DoNo }
                    equals new { deliveryOrder.CompanyCode, deliveryOrder.BranchCode, deliveryOrder.DoNo }
                where detail.DeliveryRequestSourceId.HasValue
                    && sourceIds.Contains(detail.DeliveryRequestSourceId.Value)
                    && deliveryOrder.DeletedAtUtc == null
                    && deliveryOrder.Status == SaDoStatuses.New
                select new
                {
                    detail.DoNo,
                    DoLine = detail.Line,
                    SourceId = detail.DeliveryRequestSourceId!.Value
                }).ToListAsync(cancellationToken);

        var linkedDoKeys = linkedNewDoLines
            .GroupBy(x => $"{x.DoNo.ToUpperInvariant()}\u001F{x.DoLine}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SourceId, StringComparer.Ordinal);
        var linkedDoNos = linkedNewDoLines
            .Select(x => x.DoNo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var newShipmentRows = linkedDoNos.Length == 0
            ? []
            : await (
                from detail in db.IvTrxBatchDetails.AsNoTracking()
                join batch in db.IvTrxBatches.AsNoTracking() on detail.BatchId equals batch.Id
                where detail.CompanyCode == scope.CompanyCode
                    && detail.BranchCode == scope.BranchCode
                    && detail.DoNo != null
                    && linkedDoNos.Contains(detail.DoNo)
                    && detail.SoLineNo.HasValue
                    && batch.CompanyCode == scope.CompanyCode
                    && batch.BranchCode == scope.BranchCode
                    && batch.TrxType == IvTrxTypes.SalesOut
                    && batch.BatchStatus == IvBatchStatuses.New
                    && batch.DeletedAtUtc == null
                select new
                {
                    detail.DoNo,
                    DoLine = detail.SoLineNo!.Value,
                    Qty = detail.FrStdQty ?? 0m
                }).ToListAsync(cancellationToken);

        var newShipmentBySource = new Dictionary<long, decimal>();
        foreach (var row in newShipmentRows)
        {
            if (row.DoNo is null
                || !linkedDoKeys.TryGetValue($"{row.DoNo.ToUpperInvariant()}\u001F{row.DoLine}", out var sourceId))
            {
                continue;
            }

            newShipmentBySource[sourceId] = IvQty.Round(
                newShipmentBySource.GetValueOrDefault(sourceId) + row.Qty);
        }

        var allocationRows = await (
            from allocation in db.PrWorkOrderDemandAllocations.AsNoTracking()
            join workOrder in db.ProductionWorkOrders.AsNoTracking()
                on allocation.WorkOrderId equals workOrder.Uid
            where headerIds.Contains(allocation.DeliveryRequestId)
                && allocation.CompanyCode == scope.CompanyCode
                && allocation.BranchCode == scope.BranchCode
                && allocation.IsActive
                && workOrder.Status != ProductionWorkOrderStatuses.Cancelled
            select new
            {
                allocation.DeliveryRequestId,
                allocation.WorkOrderId,
                allocation.AllocatedQty
            }).ToListAsync(cancellationToken);
        var workOrderIds = allocationRows.Select(x => x.WorkOrderId).Distinct().ToArray();

        var fgRows = workOrderIds.Length == 0
            ? []
            : await (
                from receipt in db.ProductionFinishedGoodReceiptRows.AsNoTracking()
                join batch in db.IvTrxBatches.AsNoTracking() on receipt.BatchId equals batch.Id
                join source in db.ProductionFinishedGoodSourceRows.AsNoTracking()
                    on receipt.BatchId equals source.BatchId
                join detail in db.IvTrxBatchDetails.AsNoTracking()
                    on source.DetailId equals detail.Id
                where workOrderIds.Contains(receipt.WorkOrderId)
                    && receipt.CompanyCode == scope.CompanyCode
                    && receipt.BranchCode == scope.BranchCode
                    && receipt.DeletedAtUtc == null
                    && batch.BatchStatus == IvBatchStatuses.Posted
                select new
                {
                    receipt.WorkOrderId,
                    detail.ICode,
                    detail.ToStdUom,
                    Qty = detail.ToStdQty ?? 0m
                }).ToListAsync(cancellationToken);

        var reservationRows = await db.SaDeliveryRequestStockReservations.AsNoTracking()
            .Where(x => headerIds.Contains(x.DeliveryRequestId)
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.IsActive)
            .Select(x => new { x.DeliveryRequestId, x.ReservedQty })
            .ToListAsync(cancellationToken);

        var deliveryByRequest = deliveryRows
            .Where(x => sourceToRequest.ContainsKey(x.SourceId))
            .GroupBy(x => sourceToRequest[x.SourceId])
            .ToDictionary(
                g => g.Key,
                g => (Qty: IvQty.Round(g.Sum(x => x.Qty)), FulfilledDate: g.Max(x => x.PostedDate)));
        var deliveredBySource = deliveryRows
            .GroupBy(x => x.SourceId)
            .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.Qty)));
        var newShipmentByRequest = newShipmentBySource
            .Where(x => sourceToRequest.ContainsKey(x.Key))
            .GroupBy(x => sourceToRequest[x.Key])
            .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.Value)));
        var activeAllocationByRequest = allocationRows
            .GroupBy(x => x.DeliveryRequestId)
            .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.AllocatedQty)));
        var workOrdersByRequest = allocationRows
            .GroupBy(x => x.DeliveryRequestId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.WorkOrderId).Distinct().ToArray());
        var fgByWorkOrder = fgRows
            .Where(x => x.ICode is not null)
            .GroupBy(x => x.WorkOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var reservationsByRequest = reservationRows
            .GroupBy(x => x.DeliveryRequestId)
            .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.ReservedQty)));

        var facts = new Dictionary<long, SaDeliveryRequestFulfilmentFacts>();
        foreach (var header in headers)
        {
            var headerSources = sources.Where(x => x.DeliveryRequestId == header.Uid && x.IsActive).ToList();
            var requested = IvQty.Round(headerSources.Sum(x => x.AllocatedProductionQty));
            var delivered = deliveryByRequest.GetValueOrDefault(header.Uid).Qty;
            var open = Math.Max(requested - delivered, 0m);
            var drReserved = reservationsByRequest.GetValueOrDefault(header.Uid);
            var newShipment = newShipmentByRequest.GetValueOrDefault(header.Uid);
            var activeWoAllocated = activeAllocationByRequest.GetValueOrDefault(header.Uid);
            var linkedFgReceived = 0m;
            foreach (var workOrderId in workOrdersByRequest.GetValueOrDefault(header.Uid) ?? [])
            {
                if (!fgByWorkOrder.TryGetValue(workOrderId, out var rows))
                {
                    continue;
                }

                linkedFgReceived += rows
                    .Where(x => string.Equals(x.ICode, header.ProductCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.ToStdUom, header.ProductionUom, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.Qty);
            }

            linkedFgReceived = IvQty.Round(linkedFgReceived);
            var outstandingWoSupply = Math.Max(activeWoAllocated - linkedFgReceived, 0m);
            var ready = IvQty.Round(Math.Min(open, drReserved + newShipment));
            var productionRequired = Math.Max(open - drReserved - newShipment, 0m);
            var productionUnplanned = Math.Max(productionRequired - outstandingWoSupply, 0m);
            var aggregate = new FulfilmentAggregate(
                requested,
                IvQty.Round(delivered),
                IvQty.Round(open),
                IvQty.Round(drReserved),
                IvQty.Round(newShipment),
                ready,
                activeWoAllocated,
                linkedFgReceived,
                outstandingWoSupply,
                productionRequired,
                productionUnplanned,
                deliveredBySource,
                newShipmentBySource,
                deliveryByRequest.GetValueOrDefault(header.Uid).FulfilledDate);
            facts[header.Uid] = ToFacts(header, aggregate);
        }

        return facts;
    }

    private async Task<FulfilmentAggregate> LoadAggregateAsync(
        AppDbContext db,
        SaDeliveryRequest header,
        IReadOnlyList<SaDeliveryRequestSource> sources,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var sourceIds = sources.Select(x => x.Uid).Where(x => x > 0).ToList();
        var deliveryRows = sourceIds.Count == 0
            ? []
            : await (
                from detail in db.SaDoDetails.AsNoTracking()
                join deliveryOrder in db.SaDos.AsNoTracking()
                    on new { detail.CompanyCode, detail.BranchCode, detail.DoNo }
                    equals new { deliveryOrder.CompanyCode, deliveryOrder.BranchCode, deliveryOrder.DoNo }
                where detail.DeliveryRequestSourceId.HasValue
                    && sourceIds.Contains(detail.DeliveryRequestSourceId.Value)
                    && deliveryOrder.DeletedAtUtc == null
                    && (deliveryOrder.Status == SaDoStatuses.Posted || deliveryOrder.Status == SaDoStatuses.Closed)
                select new
                {
                    SourceId = detail.DeliveryRequestSourceId!.Value,
                    Qty = detail.StdQty,
                    deliveryOrder.PostedDate
                }).ToListAsync(cancellationToken);

        var linkedNewDoLines = sourceIds.Count == 0
            ? []
            : await (
                from detail in db.SaDoDetails.AsNoTracking()
                join deliveryOrder in db.SaDos.AsNoTracking()
                    on new { detail.CompanyCode, detail.BranchCode, detail.DoNo }
                    equals new { deliveryOrder.CompanyCode, deliveryOrder.BranchCode, deliveryOrder.DoNo }
                where detail.DeliveryRequestSourceId.HasValue
                    && sourceIds.Contains(detail.DeliveryRequestSourceId.Value)
                    && deliveryOrder.DeletedAtUtc == null
                    && deliveryOrder.Status == SaDoStatuses.New
                select new
                {
                    detail.DoNo,
                    DoLine = detail.Line,
                    SourceId = detail.DeliveryRequestSourceId!.Value
                }).ToListAsync(cancellationToken);

        var newShipmentRows = await (
            from detail in db.IvTrxBatchDetails.AsNoTracking()
            join batch in db.IvTrxBatches.AsNoTracking() on detail.BatchId equals batch.Id
            where detail.CompanyCode == scope.CompanyCode
                && detail.BranchCode == scope.BranchCode
                && detail.DoNo != null
                && detail.SoLineNo.HasValue
                && batch.CompanyCode == scope.CompanyCode
                && batch.BranchCode == scope.BranchCode
                && batch.TrxType == IvTrxTypes.SalesOut
                && batch.BatchStatus == IvBatchStatuses.New
                && batch.DeletedAtUtc == null
            select new
            {
                detail.DoNo,
                DoLine = detail.SoLineNo!.Value,
                Qty = detail.FrStdQty ?? 0m
            }).ToListAsync(cancellationToken);

        var newShipmentBySource = new Dictionary<long, decimal>();
        foreach (var row in newShipmentRows)
        {
            var link = linkedNewDoLines.FirstOrDefault(x =>
                string.Equals(x.DoNo, row.DoNo, StringComparison.OrdinalIgnoreCase)
                && x.DoLine == row.DoLine);
            if (link is not null)
            {
                newShipmentBySource[link.SourceId] =
                    IvQty.Round(newShipmentBySource.GetValueOrDefault(link.SourceId) + row.Qty);
            }
        }

        var allocationRows = await (
            from allocation in db.PrWorkOrderDemandAllocations.AsNoTracking()
            join workOrder in db.ProductionWorkOrders.AsNoTracking()
                on allocation.WorkOrderId equals workOrder.Uid
            where allocation.DeliveryRequestId == header.Uid
                && allocation.CompanyCode == scope.CompanyCode
                && allocation.BranchCode == scope.BranchCode
                && allocation.IsActive
                && workOrder.Status != ProductionWorkOrderStatuses.Cancelled
            select new { allocation.WorkOrderId, allocation.AllocatedQty }).ToListAsync(cancellationToken);
        var workOrderIds = allocationRows.Select(x => x.WorkOrderId).Distinct().ToList();

        var linkedFgReceived = workOrderIds.Count == 0
            ? 0m
            : IvQty.Round(await (
                from receipt in db.ProductionFinishedGoodReceiptRows.AsNoTracking()
                join batch in db.IvTrxBatches.AsNoTracking() on receipt.BatchId equals batch.Id
                join source in db.ProductionFinishedGoodSourceRows.AsNoTracking()
                    on receipt.BatchId equals source.BatchId
                join detail in db.IvTrxBatchDetails.AsNoTracking()
                    on source.DetailId equals detail.Id
                where workOrderIds.Contains(receipt.WorkOrderId)
                    && receipt.CompanyCode == scope.CompanyCode
                    && receipt.BranchCode == scope.BranchCode
                    && receipt.DeletedAtUtc == null
                    && batch.BatchStatus == IvBatchStatuses.Posted
                    && detail.ICode == header.ProductCode
                    && detail.ToStdUom == header.ProductionUom
                select detail.ToStdQty ?? 0m).SumAsync(cancellationToken));

        var activeReservations = await db.SaDeliveryRequestStockReservations.AsNoTracking()
            .Where(x => x.DeliveryRequestId == header.Uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.IsActive)
            .ToListAsync(cancellationToken);

        var requested = IvQty.Round(sources.Where(x => x.IsActive).Sum(x => x.AllocatedProductionQty));
        var deliveredBySource = deliveryRows
            .GroupBy(x => x.SourceId)
            .ToDictionary(g => g.Key, g => IvQty.Round(g.Sum(x => x.Qty)));
        var delivered = IvQty.Round(deliveryRows.Sum(x => x.Qty));
        var open = Math.Max(requested - delivered, 0m);
        var newShipment = IvQty.Round(newShipmentBySource.Values.Sum());
        var activeWoAllocated = IvQty.Round(allocationRows.Sum(x => x.AllocatedQty));
        var outstandingWoSupply = Math.Max(activeWoAllocated - linkedFgReceived, 0m);
        var drReserved = IvQty.Round(activeReservations.Sum(x => x.ReservedQty));
        var ready = IvQty.Round(Math.Min(open, drReserved + newShipment));
        var productionRequired = Math.Max(open - drReserved - newShipment, 0m);
        var productionUnplanned = Math.Max(productionRequired - outstandingWoSupply, 0m);

        return new FulfilmentAggregate(
            requested,
            delivered,
            open,
            drReserved,
            newShipment,
            ready,
            activeWoAllocated,
            linkedFgReceived,
            outstandingWoSupply,
            productionRequired,
            productionUnplanned,
            deliveredBySource,
            newShipmentBySource,
            deliveryRows.Max(x => x.PostedDate));
    }

    private SaDeliveryRequestFulfilmentFacts ToFacts(
        SaDeliveryRequest header,
        FulfilmentAggregate aggregate)
    {
        var status = aggregate.DeliveredQty + QuantityTolerance >= aggregate.RequestedQty
            ? SaDeliveryRequestFulfilmentStatuses.Completed
            : aggregate.DeliveredQty > QuantityTolerance
                ? SaDeliveryRequestFulfilmentStatuses.PartialDelivered
                : aggregate.OpenDemandQty > QuantityTolerance
                    && aggregate.ReadyQty + QuantityTolerance >= aggregate.OpenDemandQty
                        ? SaDeliveryRequestFulfilmentStatuses.Ready
                        : aggregate.ReadyQty > QuantityTolerance
                            ? SaDeliveryRequestFulfilmentStatuses.PartialReady
                            : SaDeliveryRequestFulfilmentStatuses.Open;
        var blocker = aggregate.ProductionUnplannedQty > QuantityTolerance
            ? SaDeliveryRequestBlockerCodes.WoNotPlanned
            : aggregate.OpenDemandQty > QuantityTolerance
                && aggregate.ReadyQty < aggregate.OpenDemandQty - QuantityTolerance
                    ? SaDeliveryRequestBlockerCodes.StockNotReady
                    : SaDeliveryRequestBlockerCodes.None;
        var forecast = status == SaDeliveryRequestFulfilmentStatuses.Ready
            ? (DateTime?)_dates.Today.Date
            : null;
        var atRisk = status != SaDeliveryRequestFulfilmentStatuses.Completed
            && header.RequiredDate.Date < _dates.Today.Date;

        return new SaDeliveryRequestFulfilmentFacts
        {
            DeliveryRequestId = header.Uid,
            RequestedQty = aggregate.RequestedQty,
            DeliveredQty = aggregate.DeliveredQty,
            OpenDemandQty = aggregate.OpenDemandQty,
            DrStockReservedQty = aggregate.DrStockReservedQty,
            NewShipmentReservedQty = aggregate.NewShipmentReservedQty,
            ReadyQty = aggregate.ReadyQty,
            ActiveWoAllocatedQty = aggregate.ActiveWoAllocatedQty,
            LinkedFgReceivedQty = aggregate.LinkedFgReceivedQty,
            OutstandingWoSupplyQty = aggregate.OutstandingWoSupplyQty,
            ProductionRequiredQty = aggregate.ProductionRequiredQty,
            ProductionUnplannedQty = aggregate.ProductionUnplannedQty,
            ProducedQty = aggregate.LinkedFgReceivedQty,
            FulfilmentStatus = status,
            BlockerCode = blocker,
            FulfilledDate = status == SaDeliveryRequestFulfilmentStatuses.Completed
                ? aggregate.FulfilledDate
                : null,
            ForecastReadyDate = forecast,
            IsAtRisk = atRisk
        };
    }

    private async Task<SaDeliveryRequest?> LoadDeliveryRequestForUpdateAsync(
        AppDbContext db,
        long deliveryRequestId,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var local = db.SaDeliveryRequests.Local.FirstOrDefault(x => x.Uid == deliveryRequestId);
        if (local is not null)
        {
            return local;
        }

        if (IsSqlServer(db))
        {
            return await db.SaDeliveryRequests.FromSqlInterpolated($@"
SELECT * FROM dbo.SaDeliveryRequest WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE UID = {deliveryRequestId} AND CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}")
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.SaDeliveryRequests.SingleOrDefaultAsync(x => x.Uid == deliveryRequestId
            && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, cancellationToken);
    }

    private async Task<Dictionary<int, IvBalLocLockResult>?> LockBalancesInSliceOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, IvBalLocLockResult>();
        }

        var rows = await db.IvBalLocs.AsNoTracking()
            .Where(x => ids.Contains(x.Id)
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .ToListAsync(cancellationToken);
        var ordered = rows
            .Select(x => new
            {
                x.Id,
                Slice = IvStockSliceKey.Create(x.CompanyCode, x.BranchCode, x.ICode, x.WhCode, x.LocCode, x.LotNo, x.IStatus)
            })
            .OrderBy(x => x.Slice)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToList();

        var result = new Dictionary<int, IvBalLocLockResult>();
        foreach (var id in ordered)
        {
            var locked = await _postingRepository.LockBalLocByIdForTenantAsync(
                db, id, scope.CompanyCode, scope.BranchCode!, cancellationToken);
            if (locked is not null)
            {
                result[id] = locked;
            }
        }

        return result;
    }

    private static void ApplyReservationChanges(
        AppDbContext db,
        long deliveryRequestId,
        IReadOnlyList<SaDeliveryRequestStockReservation> currentRows,
        IReadOnlyDictionary<(long SourceId, int BalLocId), decimal> desired,
        InventoryTenantScope scope,
        DateTime now,
        string reason)
    {
        var currentByKey = currentRows
            .GroupBy(x => (x.DeliveryRequestSourceId, x.BalLocId))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Uid).First());
        var retainedKeys = new HashSet<(long DeliveryRequestSourceId, int BalLocId)>();
        foreach (var current in currentRows)
        {
            var key = (current.DeliveryRequestSourceId, current.BalLocId);
            if (desired.TryGetValue(key, out var quantity)
                && retainedKeys.Add(key)
                && Math.Abs(current.ReservedQty - quantity) <= QuantityTolerance)
            {
                continue;
            }

            current.IsActive = false;
            current.ReleasedDate = now;
            current.ReleasedBy = scope.UserId;
            current.ReleaseReason = reason;
        }

        foreach (var (key, quantity) in desired)
        {
            if (quantity <= QuantityTolerance
                || currentByKey.TryGetValue(key, out var current)
                    && Math.Abs(current.ReservedQty - quantity) <= QuantityTolerance)
            {
                continue;
            }

            db.SaDeliveryRequestStockReservations.Add(new SaDeliveryRequestStockReservation
            {
                DeliveryRequestId = deliveryRequestId,
                DeliveryRequestSourceId = key.SourceId,
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                BalLocId = key.BalLocId,
                ReservedQty = IvQty.Round(quantity),
                IsActive = true,
                CreatedDate = now,
                CreatedBy = scope.UserId
            });
        }
    }

    private static void TouchSqliteRowVersions(AppDbContext db)
    {
        if (!IsSqlite(db))
        {
            return;
        }

        foreach (var entry in db.ChangeTracker.Entries<SaDeliveryRequestStockReservation>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static bool IsSqlite(AppDbContext db) =>
        db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSqlServer(AppDbContext db) =>
        db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record FulfilmentAggregate(
        decimal RequestedQty,
        decimal DeliveredQty,
        decimal OpenDemandQty,
        decimal DrStockReservedQty,
        decimal NewShipmentReservedQty,
        decimal ReadyQty,
        decimal ActiveWoAllocatedQty,
        decimal LinkedFgReceivedQty,
        decimal OutstandingWoSupplyQty,
        decimal ProductionRequiredQty,
        decimal ProductionUnplannedQty,
        IReadOnlyDictionary<long, decimal> DeliveredBySource,
        IReadOnlyDictionary<long, decimal> NewShipmentBySource,
        DateTime? FulfilledDate);
}
