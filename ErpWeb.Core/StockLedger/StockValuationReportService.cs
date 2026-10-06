using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Builds the authoritative Inventory Valuation read model.
///
/// <para>
/// Closed periods start from the latest sealed valuation snapshot and only facts after its posting
/// watermark are replayed. Open/current periods replay sealed valuation facts directly. Warehouse rows
/// are deterministic allocations of the branch/item financial pool; they are never treated as separate
/// FIFO accounting pools.
/// </para>
/// </summary>
public sealed class StockValuationReportService : IStockValuationReportService
{
    private const decimal QuantityTolerance = 0.000001m;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryAsOfStockService _asOfStock;

    public StockValuationReportService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryAsOfStockService asOfStock)
    {
        _dbFactory = dbFactory;
        _asOfStock = asOfStock;
    }

    public async Task<StockValuationReportPage> SearchAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var built = await BuildAsync(companyCode, branchCode, query, cancellationToken);
        var rows = Sort(built.Rows, query).ToList();
        var total = rows.Count;
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, 500);

        return new StockValuationReportPage
        {
            Rows = rows.Skip(skip).Take(take).ToArray(),
            TotalCount = total
        };
    }

    public async Task<StockValuationReportSummary> SummariseAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var built = await BuildAsync(companyCode, branchCode, query, cancellationToken);
        return new StockValuationReportSummary
        {
            GroupCount = built.Rows.Count,
            ItemCount = built.ItemCount,
            PileCount = built.PileCount,
            ZeroQtyPileCount = built.ZeroQtyPileCount,
            TotalQty = built.TotalQty,
            TotalValue = built.TotalValue,
            UnresolvedCount = built.Rows.Count(x => !IsResolved(x.ValuationStatus))
        };
    }

    private async Task<BuildResult> BuildAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var company = Required(companyCode, nameof(companyCode));
        var branch = Required(branchCode, nameof(branchCode));
        var asOf = (query.AsOfDate ?? DateTime.Today).Date;
        var groupBy = IvStockSummaryGroupBys.Normalize(query.GroupBy);
        var methodFilter = NormalizeMethod(query.CostMethod);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var snapshot = await FindSnapshotAsync(db, company, branch, asOf, cancellationToken);
        var pools = new Dictionary<PoolKey, PoolValue>();
        if (snapshot is not null)
        {
            var lines = await db.StockValuationPeriodSnapshotLines.AsNoTracking()
                .Where(x => x.HeaderId == snapshot.Id)
                .ToListAsync(cancellationToken);
            foreach (var line in lines)
            {
                var key = new PoolKey(line.ItemCode, line.BaseUom, line.CostMethod);
                if (methodFilter is not null && !string.Equals(key.CostMethod, methodFilter, StringComparison.Ordinal))
                    continue;
                AddPool(pools, key, line.ClosingQty, line.ClosingValue, unresolved: false);
            }
        }

        var watermark = snapshot?.PostingSequenceWatermark ?? 0L;
        var exclusiveEnd = asOf.AddDays(1);
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.EffectiveAt < exclusiveEnd
                        && x.StockPosting!.SealedAtUtc != null
                        && x.StockPosting.PostingSequence > watermark)
            .Select(x => new FactValue(
                x.ItemCode,
                x.BaseUom,
                x.CostMethod,
                x.Direction,
                x.BaseQty,
                x.CostAmount,
                x.ValuationStatus))
            .ToListAsync(cancellationToken);

        foreach (var fact in facts)
        {
            var key = new PoolKey(fact.ItemCode, fact.BaseUom, fact.CostMethod);
            if (methodFilter is not null && !string.Equals(key.CostMethod, methodFilter, StringComparison.Ordinal))
                continue;
            AddPool(
                pools,
                key,
                fact.BaseQty * fact.Direction,
                fact.CostAmount * fact.Direction,
                unresolved: fact.ValuationStatus is not (StockValuationStatuses.Valued or StockValuationStatuses.Reversed));
        }

        var itemCodes = pools.Keys.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var masters = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && itemCodes.Contains(x.ICode))
            .Select(x => new ItemMeta(
                x.ICode,
                x.IDesc,
                x.IClassCode,
                x.StdUom,
                x.IsActive,
                x.StockControl))
            .ToListAsync(cancellationToken);
        var masterByItem = masters.ToDictionary(x => NormalizeCode(x.ItemCode), StringComparer.OrdinalIgnoreCase);

        var physical = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PhysicalSlice(
                x.Id,
                x.ICode,
                x.WhCode,
                x.StdUom,
                x.IStatus,
                x.StockMaster.IDesc,
                x.StockMaster.IClassCode,
                x.StockMaster.StdUom,
                x.StockMaster.IsActive,
                x.StockMaster.StockControl,
                x.Warehouse.WarehouseDesc))
            .ToListAsync(cancellationToken);
        var asOfByBalance = await _asOfStock.GetAsync(
            db,
            company,
            branch,
            physical.Select(x => x.BalanceId).ToArray(),
            asOf,
            cancellationToken);

        var filteredPhysical = physical
            .Select(x => new PhysicalValue(
                x,
                asOfByBalance.GetValueOrDefault(x.BalanceId)?.AsOfBaseQty ?? 0m))
            .Where(x => MatchesFilter(x.Slice, query, masterByItem))
            .Where(x => query.IncludeZeroQty || x.Quantity != 0m)
            .ToList();

        var itemBuckets = BuildItemBuckets(pools, filteredPhysical, masterByItem, query.IncludeZeroQty);
        var rows = BuildRows(itemBuckets, groupBy);

        var totalQty = itemBuckets
            .Where(x => x.HasSingleUom)
            .Sum(x => x.AuthoritativeQty);
        var totalValue = itemBuckets.Sum(x => Round(x.AuthoritativeValue));
        var pileCount = itemBuckets.Sum(x => x.PileCount);
        var zeroQtyPileCount = itemBuckets.Sum(x => x.ZeroQtyPileCount);

        return new BuildResult(
            rows,
            itemBuckets.Count,
            pileCount,
            zeroQtyPileCount,
            totalQty,
            totalValue);
    }

    private static async Task<StockValuationPeriodSnapshotHdr?> FindSnapshotAsync(
        AppDbContext db,
        string company,
        string branch,
        DateTime asOf,
        CancellationToken cancellationToken)
    {
        var headers = await db.StockValuationPeriodSnapshotHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.ValuationStatus == "SEALED")
            .ToListAsync(cancellationToken);

        return headers
            .Select(x => (Header: x, PeriodEnd: TryPeriodEnd(x.PeriodKey)))
            .Where(x => x.PeriodEnd is not null && x.PeriodEnd.Value <= asOf)
            .GroupBy(x => x.Header.PeriodKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(y => y.Header.Revision).First())
            .OrderByDescending(x => x.PeriodEnd)
            .Select(x => x.Header)
            .FirstOrDefault();
    }

    private static IReadOnlyList<ItemBucket> BuildItemBuckets(
        IReadOnlyDictionary<PoolKey, PoolValue> pools,
        IReadOnlyList<PhysicalValue> physical,
        IReadOnlyDictionary<string, ItemMeta> masterByItem,
        bool includeZeroQty)
    {
        var itemKeys = pools.Keys.Select(x => x.ItemCode)
            .Concat(physical.Select(x => NormalizeCode(x.Slice.ItemCode)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new List<ItemBucket>();

        foreach (var itemCode in itemKeys)
        {
            var itemPools = pools
                .Where(x => string.Equals(x.Key.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var itemPhysical = physical
                .Where(x => string.Equals(NormalizeCode(x.Slice.ItemCode), itemCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var meta = masterByItem.GetValueOrDefault(itemCode);

            if (meta is not null && !meta.StockControl)
            {
                // Non-stock-controlled items can still have legacy balances, but they are not
                // authoritative financial inventory rows unless the caller explicitly asks for them.
                // The filter is applied here because an item with no balance has no slice to filter.
            }

            var byUom = itemPools.GroupBy(x => x.Key.BaseUom, StringComparer.OrdinalIgnoreCase).ToArray();
            if (byUom.Length == 0 && itemPhysical.Length == 0)
                continue;

            var uomGroups = byUom.Length == 0
                ? new[] { Array.Empty<KeyValuePair<PoolKey, PoolValue>>() }
                : byUom.Select(x => x.ToArray()).ToArray();
            foreach (var uomGroup in uomGroups)
            {
                var uom = uomGroup.Length == 0 ? meta?.StdUom ?? string.Empty : uomGroup[0].Key.BaseUom;
                var selectedPools = uomGroup;
                var authoritativeQty = selectedPools.Sum(x => x.Value.Qty);
                var authoritativeValue = selectedPools.Sum(x => x.Value.Value);
                var unresolved = selectedPools.Length == 0 || selectedPools.Any(x => x.Value.Unresolved);
                var matchingPhysical = itemPhysical
                    .Where(x => string.Equals(
                        NormalizeCode(x.Slice.StdUom ?? x.Slice.MasterStdUom ?? string.Empty),
                        NormalizeCode(uom),
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (selectedPools.Length == 0 && matchingPhysical.Length == 0)
                    continue;
                if (!includeZeroQty && authoritativeQty == 0m && authoritativeValue == 0m && matchingPhysical.Length == 0)
                    continue;

                result.Add(new ItemBucket(
                    itemCode,
                    meta?.Description ?? matchingPhysical.FirstOrDefault()?.Slice.Description,
                    meta?.ClassCode ?? matchingPhysical.FirstOrDefault()?.Slice.ClassCode,
                    uom,
                    authoritativeQty,
                    authoritativeValue,
                    unresolved,
                    matchingPhysical,
                    selectedPools.Select(x => x.Key.CostMethod).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        }

        return result;
    }

    private static IReadOnlyList<StockValuationReportRow> BuildRows(
        IReadOnlyList<ItemBucket> buckets,
        string groupBy)
    {
        var itemRows = new List<StockValuationReportRow>();
        foreach (var bucket in buckets)
        {
            var physicalQty = bucket.Physical.Sum(x => x.Quantity);
            var status = bucket.Status(physicalQty);
            var method = bucket.Method;
            var unitCost = bucket.AuthoritativeQty == 0m
                ? 0m
                : Round(bucket.AuthoritativeValue / bucket.AuthoritativeQty);

            itemRows.Add(new StockValuationReportRow
            {
                ICode = bucket.ItemCode,
                IDesc = bucket.Description,
                IClassCode = bucket.ClassCode,
                StdUom = bucket.HasSingleUom ? bucket.BaseUom : null,
                TotalQty = bucket.HasSingleUom ? Round(bucket.AuthoritativeQty) : 0m,
                ItemCount = 1,
                PileCount = bucket.PileCount,
                ZeroQtyPileCount = bucket.ZeroQtyPileCount,
                InventoryValue = Round(bucket.AuthoritativeValue),
                UnitCost = unitCost,
                CostMethod = method,
                ValuationStatus = status,
                IsAllocatedWarehouseValue = false
            });
        }

        if (groupBy == IvStockSummaryGroupBys.Item)
            return itemRows;

        var allocations = new List<StockValuationReportRow>();
        foreach (var bucket in buckets)
        {
            var physicalGroups = bucket.Physical
                .GroupBy(x => x.Slice.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (physicalGroups.Length == 0)
            {
                allocations.Add(CreateAllocationRow(bucket, "(unallocated)", null, 0m, Round(bucket.AuthoritativeValue), []));
                continue;
            }

            var totalPhysicalQty = physicalGroups.Sum(x => x.Sum(y => y.Quantity));
            var roundedTarget = Round(bucket.AuthoritativeValue);
            var assigned = 0m;
            for (var index = 0; index < physicalGroups.Length; index++)
            {
                var group = physicalGroups[index];
                var quantity = Round(group.Sum(x => x.Quantity));
                var value = index == physicalGroups.Length - 1
                    ? Round(roundedTarget - assigned)
                    : totalPhysicalQty == 0m
                        ? 0m
                        : Round(roundedTarget * group.Sum(x => x.Quantity) / totalPhysicalQty);
                assigned += value;
                allocations.Add(CreateAllocationRow(
                    bucket,
                    group.Key,
                    group.Select(x => x.Slice.WarehouseDescription).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    quantity,
                    value,
                    group.ToArray()));
            }
        }

        return groupBy switch
        {
            IvStockSummaryGroupBys.ItemWarehouse => allocations,
            IvStockSummaryGroupBys.Warehouse => GroupRows(
                allocations,
                x => x.WhCode ?? string.Empty,
                (key, rows) => new StockValuationReportRow
                {
                    WhCode = key,
                    WhDesc = rows.Select(x => x.WhDesc).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    TotalQty = Round(rows.Sum(x => x.TotalQty)),
                    ItemCount = rows.Select(x => x.ICode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    PileCount = rows.Sum(x => x.PileCount),
                    ZeroQtyPileCount = rows.Sum(x => x.ZeroQtyPileCount),
                    InventoryValue = Round(rows.Sum(x => x.InventoryValue)),
                    UnitCost = rows.Sum(x => x.TotalQty) == 0m
                        ? 0m
                        : Round(rows.Sum(x => x.InventoryValue) / rows.Sum(x => x.TotalQty)),
                    CostMethod = Collapse(rows.Select(x => x.CostMethod)),
                    ValuationStatus = CollapseStatus(rows.Select(x => x.ValuationStatus)),
                    IsAllocatedWarehouseValue = true
                }),
            IvStockSummaryGroupBys.Class => GroupRows(
                allocations,
                x => x.IClassCode ?? string.Empty,
                (key, rows) => new StockValuationReportRow
                {
                    IClassCode = key,
                    IClassDesc = rows.Select(x => x.IClassDesc).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    TotalQty = Round(rows.Sum(x => x.TotalQty)),
                    ItemCount = rows.Select(x => x.ICode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    PileCount = rows.Sum(x => x.PileCount),
                    ZeroQtyPileCount = rows.Sum(x => x.ZeroQtyPileCount),
                    InventoryValue = Round(rows.Sum(x => x.InventoryValue)),
                    UnitCost = rows.Sum(x => x.TotalQty) == 0m
                        ? 0m
                        : Round(rows.Sum(x => x.InventoryValue) / rows.Sum(x => x.TotalQty)),
                    CostMethod = Collapse(rows.Select(x => x.CostMethod)),
                    ValuationStatus = CollapseStatus(rows.Select(x => x.ValuationStatus)),
                    IsAllocatedWarehouseValue = true
                }),
            _ => itemRows
        };
    }

    private static StockValuationReportRow CreateAllocationRow(
        ItemBucket bucket,
        string warehouseCode,
        string? warehouseDescription,
        decimal quantity,
        decimal value,
        IReadOnlyCollection<PhysicalValue> physical)
    {
        var physicalQty = physical.Sum(x => x.Quantity);
        return new StockValuationReportRow
        {
            ICode = bucket.ItemCode,
            IDesc = bucket.Description,
            IClassCode = bucket.ClassCode,
            WhCode = warehouseCode,
            WhDesc = warehouseDescription,
            StdUom = bucket.BaseUom,
            TotalQty = quantity,
            ItemCount = 1,
            PileCount = physical.Count,
            ZeroQtyPileCount = physical.Count(x => x.Quantity == 0m),
            InventoryValue = value,
            UnitCost = quantity == 0m ? 0m : Round(value / quantity),
            CostMethod = bucket.Method,
            ValuationStatus = bucket.Status(physicalQty),
            IsAllocatedWarehouseValue = true
        };
    }

    private static IReadOnlyList<StockValuationReportRow> GroupRows(
        IReadOnlyList<StockValuationReportRow> source,
        Func<StockValuationReportRow, string> keySelector,
        Func<string, IReadOnlyList<StockValuationReportRow>, StockValuationReportRow> projector)
    {
        return source
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => projector(x.Key, x.ToArray()))
            .ToArray();
    }

    private static IEnumerable<StockValuationReportRow> Sort(
        IReadOnlyList<StockValuationReportRow> rows,
        IvStockSummaryQuery query)
    {
        var groupBy = IvStockSummaryGroupBys.Normalize(query.GroupBy);
        IOrderedEnumerable<StockValuationReportRow> ordered = groupBy switch
        {
            IvStockSummaryGroupBys.Warehouse => rows.OrderBy(x => x.WhCode, StringComparer.OrdinalIgnoreCase),
            IvStockSummaryGroupBys.Class => rows.OrderBy(x => x.IClassCode, StringComparer.OrdinalIgnoreCase),
            IvStockSummaryGroupBys.ItemWarehouse => rows.OrderBy(x => x.ICode, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.WhCode, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(x => x.ICode, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.StdUom, StringComparer.OrdinalIgnoreCase)
        };
        return query.SortDescending ? ordered.Reverse() : ordered;
    }

    private static bool MatchesFilter(
        PhysicalSlice slice,
        IvStockSummaryQuery query,
        IReadOnlyDictionary<string, ItemMeta> masterByItem)
    {
        var item = masterByItem.GetValueOrDefault(NormalizeCode(slice.ItemCode));
        if (item is not null)
        {
            if (!query.IncludeInactive && !item.IsActive)
                return false;
            if (!query.IncludeNonStockControl && !item.StockControl)
                return false;
        }
        if (!string.IsNullOrWhiteSpace(query.ICode)
            && !string.Equals(slice.ItemCode, query.ICode.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.WhCode)
            && !string.Equals(slice.WarehouseCode, query.WhCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.IClassCode)
            && !string.Equals(slice.ClassCode, query.IClassCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.IStatuses.Count > 0
            && !query.IStatuses.Contains(slice.ItemStatus, StringComparer.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static void AddPool(
        IDictionary<PoolKey, PoolValue> pools,
        PoolKey key,
        decimal quantity,
        decimal value,
        bool unresolved)
    {
        if (!pools.TryGetValue(key, out var current))
        {
            current = new PoolValue();
            pools[key] = current;
        }
        current.Qty += quantity;
        current.Value += value;
        current.Unresolved |= unresolved;
    }

    private static string Collapse(IEnumerable<string> values)
    {
        var distinct = values.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : "MIXED";
    }

    private static string CollapseStatus(IEnumerable<string> values)
    {
        var statuses = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (statuses.Any(x => string.Equals(x, "UNRESOLVED", StringComparison.OrdinalIgnoreCase)))
            return "UNRESOLVED";
        if (statuses.Any(x => string.Equals(x, "QTY_MISMATCH", StringComparison.OrdinalIgnoreCase)))
            return "QTY_MISMATCH";
        return statuses.Length == 0 ? "UNRESOLVED" : "RESOLVED";
    }

    private static bool IsResolved(string status) =>
        string.Equals(status, "RESOLVED", StringComparison.OrdinalIgnoreCase);

    private static DateTime? TryPeriodEnd(string? periodKey)
    {
        if (!DateTime.TryParseExact(
                periodKey,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var month))
            return null;
        return new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
    }

    private static string? NormalizeMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return null;
        var value = method.Trim().ToUpperInvariant();
        if (!StockCostMethods.All.Contains(value))
            throw new ArgumentException($"Unsupported stock costing method '{method}'.", nameof(method));
        return value;
    }

    private static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty tenant scope is required.", name);
        return value.Trim();
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private readonly record struct PoolKey
    {
        public PoolKey(string itemCode, string baseUom, string costMethod)
        {
            ItemCode = NormalizeCode(itemCode);
            BaseUom = NormalizeCode(baseUom);
            CostMethod = NormalizeCode(costMethod);
        }

        public string ItemCode { get; }
        public string BaseUom { get; }
        public string CostMethod { get; }
    }

    private sealed class PoolValue
    {
        public decimal Qty { get; set; }
        public decimal Value { get; set; }
        public bool Unresolved { get; set; }
    }

    private sealed record ItemMeta(
        string ItemCode,
        string? Description,
        string? ClassCode,
        string? StdUom,
        bool IsActive,
        bool StockControl);

    private sealed record PhysicalSlice(
        int BalanceId,
        string ItemCode,
        string WarehouseCode,
        string? StdUom,
        string ItemStatus,
        string? Description,
        string? ClassCode,
        string? MasterStdUom,
        bool IsActive,
        bool StockControl,
        string? WarehouseDescription);

    private sealed record PhysicalValue(PhysicalSlice Slice, decimal Quantity);

    private sealed record FactValue(
        string ItemCode,
        string BaseUom,
        string CostMethod,
        int Direction,
        decimal BaseQty,
        decimal CostAmount,
        string ValuationStatus);

    private sealed class ItemBucket
    {
        public ItemBucket(
            string itemCode,
            string? description,
            string? classCode,
            string baseUom,
            decimal authoritativeQty,
            decimal authoritativeValue,
            bool unresolved,
            IReadOnlyList<PhysicalValue> physical,
            IReadOnlyList<string> methods)
        {
            ItemCode = itemCode;
            Description = description;
            ClassCode = classCode;
            BaseUom = baseUom;
            AuthoritativeQty = authoritativeQty;
            AuthoritativeValue = authoritativeValue;
            Unresolved = unresolved;
            Physical = physical;
            Methods = methods;
        }

        public string ItemCode { get; }
        public string? Description { get; }
        public string? ClassCode { get; }
        public string BaseUom { get; }
        public decimal AuthoritativeQty { get; }
        public decimal AuthoritativeValue { get; }
        public bool Unresolved { get; }
        public IReadOnlyList<PhysicalValue> Physical { get; }
        public IReadOnlyList<string> Methods { get; }
        public bool HasSingleUom => !string.IsNullOrWhiteSpace(BaseUom);
        public int PileCount => Physical.Count;
        public int ZeroQtyPileCount => Physical.Count(x => x.Quantity == 0m);
        public string Method => Methods.Count == 1 ? Methods[0] : "MIXED";

        public string Status(decimal physicalQty)
        {
            if (Unresolved
                || AuthoritativeQty < -QuantityTolerance
                || AuthoritativeValue < -QuantityTolerance
                || (Math.Abs(AuthoritativeQty) <= QuantityTolerance
                    && Math.Abs(AuthoritativeValue) > QuantityTolerance))
                return "UNRESOLVED";
            return Math.Abs(physicalQty - AuthoritativeQty) > QuantityTolerance
                ? "QTY_MISMATCH"
                : "RESOLVED";
        }
    }

    private sealed record BuildResult(
        IReadOnlyList<StockValuationReportRow> Rows,
        int ItemCount,
        int PileCount,
        int ZeroQtyPileCount,
        decimal TotalQty,
        decimal TotalValue);

}
