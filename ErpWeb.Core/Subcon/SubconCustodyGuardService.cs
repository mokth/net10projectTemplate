using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Subcon;

public sealed class SubconCustodyGuardService : ISubconCustodyGuard
{
    public static SubconCustodyGuardService Instance { get; } = new();

    public static bool IsCustodyWarehouseType(string? warehouseType) =>
        string.Equals(warehouseType?.Trim(), SubconCustodyConstants.CustodyWarehouseType, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> IsCustodyWarehouseAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return false;
        }

        var wh = warehouseCode.Trim();
        var type = await db.IvWarehouses.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.WarehouseCode == wh)
            .Select(x => x.WarehouseType)
            .FirstOrDefaultAsync(cancellationToken);
        return IsCustodyWarehouseType(type);
    }

    public async Task<string?> AssertStockMovementAllowedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string trxType,
        IReadOnlyCollection<int> sourceBalLocIds,
        IReadOnlyCollection<string> destinationWarehouseCodes,
        bool isAuthorizedSubconCaller,
        CancellationToken cancellationToken = default)
    {
        var distinctSourceIds = sourceBalLocIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        var distinctDestWhs = destinationWarehouseCodes
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sourceHasCustody = false;
        if (distinctSourceIds.Length > 0)
        {
            var sourceWhCodes = await db.IvBalLocs.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode
                            && x.BranchCode == branchCode
                            && distinctSourceIds.Contains(x.Id))
                .Select(x => x.WhCode)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (sourceWhCodes.Count > 0)
            {
                var sourceWhTypes = await db.IvWarehouses.AsNoTracking()
                    .Where(x => x.CompanyCode == companyCode
                                && x.BranchCode == branchCode
                                && sourceWhCodes.Contains(x.WarehouseCode))
                    .Select(x => x.WarehouseType)
                    .ToListAsync(cancellationToken);
                sourceHasCustody = sourceWhTypes.Any(IsCustodyWarehouseType);
            }

            if (!sourceHasCustody)
            {
                sourceHasCustody = await db.SubconCustodyAllocations.AsNoTracking()
                    .AnyAsync(x => x.CompanyCode == companyCode
                                   && x.BranchCode == branchCode
                                   && x.IsActive
                                   && distinctSourceIds.Contains(x.CustodyBalLocId)
                                   && (x.DispatchedBaseQty - x.ReturnedBaseQty - x.ConsumedBaseQty - x.ScrapBaseQty) > 0m,
                        cancellationToken);
            }
        }

        var destHasCustody = false;
        if (distinctDestWhs.Length > 0)
        {
            var destWhTypes = await db.IvWarehouses.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode
                            && x.BranchCode == branchCode
                            && distinctDestWhs.Contains(x.WarehouseCode))
                .Select(x => x.WarehouseType)
                .ToListAsync(cancellationToken);
            destHasCustody = destWhTypes.Any(IsCustodyWarehouseType);
        }

        if (!isAuthorizedSubconCaller)
        {
            if (sourceHasCustody)
            {
                return SubconCustodyConstants.SourceCustodyBlocked;
            }

            if (destHasCustody)
            {
                return SubconCustodyConstants.DestinationCustodyBlocked;
            }

            return null;
        }

        // Authorized Subcon custody TR must move between company stock and supplier custody (never custody->custody or company->company).
        if (sourceHasCustody == destHasCustody)
        {
            return SubconCustodyConstants.SameSideCustodyTransferBlocked;
        }

        return null;
    }

    public async Task<string?> AssertBatchNotSubconOwnedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        int batchId,
        CancellationToken cancellationToken = default)
    {
        if (batchId <= 0)
        {
            return null;
        }

        var isOwned = await db.SubconPostingLinks.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == companyCode
                           && x.BranchCode == branchCode
                           && x.TrxBatchId == batchId,
                cancellationToken);
        return isOwned ? SubconCustodyConstants.SubconOwnedBatchBlocked : null;
    }

    public async Task<string?> AssertWarehouseMutationAllowedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string? priorWarehouseType,
        string? nextWarehouseType,
        bool nextIsActive,
        CancellationToken cancellationToken = default)
    {
        var wh = (warehouseCode ?? string.Empty).Trim();
        if (wh.Length == 0)
            return null;

        var wasCustody = IsCustodyWarehouseType(priorWarehouseType);
        var willBeCustody = IsCustodyWarehouseType(nextWarehouseType);

        if (wasCustody && (!willBeCustody || !nextIsActive))
        {
            var hasActiveAlloc = await db.SubconCustodyAllocations.AsNoTracking()
                .AnyAsync(x => x.CompanyCode == companyCode
                               && x.BranchCode == branchCode
                               && x.IsActive
                               && x.CustodyWarehouseCode == wh
                               && (x.DispatchedBaseQty - x.ReturnedBaseQty - x.ConsumedBaseQty - x.ScrapBaseQty) > 0m,
                    cancellationToken);
            if (hasActiveAlloc)
                return $"Warehouse '{wh}' has active subcontractor custody allocations. Receive back or roll back all outstanding supplier stock before changing its type or deactivating it.";

            var hasCustodyStock = await db.IvBalLocs.AsNoTracking()
                .AnyAsync(x => x.CompanyCode == companyCode
                               && x.BranchCode == branchCode
                               && x.WhCode == wh
                               && x.StdQty != 0m,
                    cancellationToken);
            if (hasCustodyStock)
                return $"Warehouse '{wh}' still holds non-zero subcontractor custody stock. Clear all balances before changing its type or deactivating it.";

            var isDefaultPolicyWh = await db.SubconPolicies.AsNoTracking()
                .AnyAsync(x => x.CompanyCode == companyCode
                               && x.IsPublished
                               && x.InventoryCustodyPostEnabled
                               && x.DefaultCustodyWarehouseCode == wh,
                    cancellationToken);
            if (isDefaultPolicyWh)
                return $"Warehouse '{wh}' is configured as the active Subcontracting default custody warehouse. Update Subcontracting Settings before changing its type or deactivating it.";

            var hasActiveSupplierMap = await db.SubconSupplierCustodyMaps.AsNoTracking()
                .AnyAsync(x => x.CompanyCode == companyCode
                               && x.BranchCode == branchCode
                               && x.IsActive
                               && x.CustodyWarehouseCode == wh,
                    cancellationToken);
            if (hasActiveSupplierMap)
                return $"Warehouse '{wh}' is referenced by an active supplier custody bin mapping. Update or deactivate those mappings before changing its type or deactivating it.";
        }

        if (!wasCustody && willBeCustody)
        {
            var hasNormalStock = await db.IvBalLocs.AsNoTracking()
                .AnyAsync(x => x.CompanyCode == companyCode
                               && x.BranchCode == branchCode
                               && x.WhCode == wh
                               && x.StdQty != 0m,
                    cancellationToken);
            if (hasNormalStock)
                return $"Warehouse '{wh}' holds existing stock and cannot be converted to a subcontractor custody warehouse until its balances are zero.";

            var hasActiveReservations = await (
                from res in db.SaDeliveryRequestStockReservations.AsNoTracking()
                join bal in db.IvBalLocs.AsNoTracking() on res.BalLocId equals bal.Id
                where res.CompanyCode == companyCode
                      && res.BranchCode == branchCode
                      && res.IsActive
                      && bal.CompanyCode == companyCode
                      && bal.BranchCode == branchCode
                      && bal.WhCode == wh
                select res.Uid).AnyAsync(cancellationToken);
            if (hasActiveReservations)
                return $"Warehouse '{wh}' has active delivery reservations and cannot be converted to a subcontractor custody warehouse.";

            var hasDraftSalesShipments = await (
                from detail in db.IvTrxBatchDetails.AsNoTracking()
                join batch in db.IvTrxBatches.AsNoTracking() on detail.BatchId equals batch.Id
                where detail.CompanyCode == companyCode
                      && detail.BranchCode == branchCode
                      && detail.FrWarehouse == wh
                      && batch.CompanyCode == companyCode
                      && batch.BranchCode == branchCode
                      && batch.TrxType == IvTrxTypes.SalesOut
                      && batch.BatchStatus == IvBatchStatuses.New
                      && batch.DeletedAtUtc == null
                select detail.Id).AnyAsync(cancellationToken);
            if (hasDraftSalesShipments)
                return $"Warehouse '{wh}' has pending sales shipment drafts and cannot be converted to a subcontractor custody warehouse.";
        }

        return null;
    }

    public async Task<string?> AssertLocationMutationAllowedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string locCode,
        bool nextIsActive,
        CancellationToken cancellationToken = default)
    {
        if (nextIsActive)
            return null;

        var wh = (warehouseCode ?? string.Empty).Trim();
        var loc = (locCode ?? string.Empty).Trim();
        if (wh.Length == 0 || loc.Length == 0)
            return null;

        if (!await IsCustodyWarehouseAsync(db, companyCode, branchCode, wh, cancellationToken))
            return null;

        var hasActiveAlloc = await db.SubconCustodyAllocations.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == companyCode
                           && x.BranchCode == branchCode
                           && x.IsActive
                           && x.CustodyWarehouseCode == wh
                           && x.CustodyLocationCode == loc
                           && (x.DispatchedBaseQty - x.ReturnedBaseQty - x.ConsumedBaseQty - x.ScrapBaseQty) > 0m,
                cancellationToken);
        if (hasActiveAlloc)
            return $"Custody location '{wh}/{loc}' has active subcontractor custody allocations and cannot be deactivated.";

        var hasStock = await db.IvBalLocs.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == companyCode
                           && x.BranchCode == branchCode
                           && x.WhCode == wh
                           && x.LocCode == loc
                           && x.StdQty != 0m,
                cancellationToken);
        if (hasStock)
            return $"Custody location '{wh}/{loc}' still holds non-zero stock and cannot be deactivated.";

        var hasActiveMap = await db.SubconSupplierCustodyMaps.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == companyCode
                           && x.BranchCode == branchCode
                           && x.IsActive
                           && x.CustodyWarehouseCode == wh
                           && x.CustodyLocationCode == loc,
                cancellationToken);
        if (hasActiveMap)
            return $"Custody location '{wh}/{loc}' is referenced by an active supplier custody mapping and cannot be deactivated.";

        return null;
    }

    public async Task<bool> HasSubconOwnedTransferAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<int> transferBatchNos,
        CancellationToken cancellationToken = default)
    {
        var distinctNos = transferBatchNos.Where(n => n > 0).Distinct().ToArray();
        if (distinctNos.Length == 0)
            return false;

        return await db.SubconPostingLinks.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == companyCode
                           && x.BranchCode == branchCode
                           && distinctNos.Contains(x.TrxBatchNo),
                cancellationToken);
    }
}
