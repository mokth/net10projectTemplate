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
}
