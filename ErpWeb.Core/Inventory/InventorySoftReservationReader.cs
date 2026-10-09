using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class InventorySoftReservationReader : IInventorySoftReservationReader
{
    public async Task<IReadOnlyDictionary<int, InventorySoftReservationAmounts>> GetReservedByBalanceAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string locationCode,
        IReadOnlyCollection<int> balanceIds,
        IReadOnlyCollection<int>? excludeSpDetailIds = null,
        long? excludeDeliveryRequestSourceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var ids = balanceIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        var result = ids.ToDictionary(x => x, _ => new InventorySoftReservationAmounts(0m, 0m));
        if (ids.Count == 0)
        {
            return result;
        }

        var spQuery =
            from detail in db.IvTrxBatchDetails.AsNoTracking()
            join batch in db.IvTrxBatches.AsNoTracking() on detail.BatchId equals batch.Id
            where detail.CompanyCode == companyCode
                  && detail.BranchCode == branchCode
                  && detail.LocationCode == locationCode
                  && detail.FromBalLocId.HasValue
                  && ids.Contains(detail.FromBalLocId.Value)
                  && batch.CompanyCode == companyCode
                  && batch.BranchCode == branchCode
                  && batch.LocationCode == locationCode
                  && batch.TrxType == IvTrxTypes.SalesOut
                  && batch.BatchStatus == IvBatchStatuses.New
                  && batch.DeletedAtUtc == null
            select new
            {
                BalLocId = detail.FromBalLocId!.Value,
                Qty = detail.FrStdQty ?? 0m,
                DetailId = detail.Id
            };

        if (excludeSpDetailIds is { Count: > 0 })
        {
            spQuery = spQuery.Where(x => !excludeSpDetailIds.Contains(x.DetailId));
        }

        var spRows = await spQuery
            .GroupBy(x => x.BalLocId)
            .Select(g => new { BalLocId = g.Key, Qty = g.Sum(x => x.Qty) })
            .ToListAsync(cancellationToken);

        var drQuery =
            from reservation in db.SaDeliveryRequestStockReservations.AsNoTracking()
            join balLoc in db.IvBalLocs.AsNoTracking() on reservation.BalLocId equals balLoc.Id
            where reservation.CompanyCode == companyCode
                  && reservation.BranchCode == branchCode
                  && reservation.IsActive
                  && ids.Contains(reservation.BalLocId)
                  && balLoc.CompanyCode == companyCode
                  && balLoc.BranchCode == branchCode
                  && (balLoc.LocationCode == locationCode
                      || balLoc.LocationCode == null
                      || balLoc.LocationCode == "")
            select new
            {
                reservation.BalLocId,
                reservation.DeliveryRequestSourceId,
                reservation.ReservedQty
            };

        if (excludeDeliveryRequestSourceId is long sourceId)
        {
            drQuery = drQuery.Where(x => x.DeliveryRequestSourceId != sourceId);
        }

        var drRows = await drQuery
            .GroupBy(x => x.BalLocId)
            .Select(g => new { BalLocId = g.Key, Qty = g.Sum(x => x.ReservedQty) })
            .ToListAsync(cancellationToken);

        foreach (var row in spRows)
        {
            var prior = result[row.BalLocId];
            result[row.BalLocId] = prior with { NewSpReservedQty = IvQty.Round(row.Qty) };
        }

        foreach (var row in drRows)
        {
            var prior = result[row.BalLocId];
            result[row.BalLocId] = prior with { DrReservedQty = IvQty.Round(row.Qty) };
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<(long DeliveryRequestSourceId, int BalLocId), decimal>>
        GetActiveDeliveryRequestReservationsAsync(
            AppDbContext db,
            string companyCode,
            string branchCode,
            string locationCode,
            IReadOnlyCollection<long> deliveryRequestSourceIds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var sourceIds = deliveryRequestSourceIds
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        if (sourceIds.Count == 0)
        {
            return new Dictionary<(long DeliveryRequestSourceId, int BalLocId), decimal>();
        }

        var rows = await (
            from reservation in db.SaDeliveryRequestStockReservations.AsNoTracking()
            join balLoc in db.IvBalLocs.AsNoTracking() on reservation.BalLocId equals balLoc.Id
            where reservation.CompanyCode == companyCode
                && reservation.BranchCode == branchCode
                && reservation.IsActive
                && sourceIds.Contains(reservation.DeliveryRequestSourceId)
                && balLoc.CompanyCode == companyCode
                && balLoc.BranchCode == branchCode
                && (balLoc.LocationCode == locationCode
                    || balLoc.LocationCode == null
                    || balLoc.LocationCode == "")
            group reservation by new
            {
                reservation.DeliveryRequestSourceId,
                reservation.BalLocId
            }
            into grouped
            select new
            {
                grouped.Key.DeliveryRequestSourceId,
                grouped.Key.BalLocId,
                Qty = grouped.Sum(x => x.ReservedQty)
            }).ToListAsync(cancellationToken);

        return rows.ToDictionary(
            x => (x.DeliveryRequestSourceId, x.BalLocId),
            x => IvQty.Round(x.Qty));
    }
}
