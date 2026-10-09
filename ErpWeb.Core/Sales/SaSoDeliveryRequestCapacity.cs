using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>Exact Sales Order revision/line identity used by DR capacity queries.</summary>
public readonly record struct SaSoDrLineKey(string SoNo, short CustRel, short SoLine)
{
    public SaSoDrLineKey Normalize() =>
        new((SoNo ?? string.Empty).Trim(), CustRel, SoLine);
}

public enum SaSoDrCapacityMode
{
    /// <summary>All active source rows, including DRAFT Delivery Requests.</summary>
    AllActiveSources,

    /// <summary>Only committed source rows owned by RELEASED/IN_PRODUCTION DRs.</summary>
    CommittedSources
}

/// <summary>
/// Capacity facts for one exact SO revision/line.  Allocated quantity is the
/// full active DR commitment; outstanding quantity is source-clamped after
/// subtracting linked DO standard quantity.
/// </summary>
public sealed record SaSoDrCapacityFacts(
    decimal ActiveAllocatedProductionQty,
    decimal LinkedDoProductionQty,
    decimal OutstandingProductionQty);

/// <summary>
/// Shared database-backed authority for DR reservation math.
///
/// The linked DO quantity is evaluated per source before aggregation.  That is
/// important because SaSoLineReserve already includes DR-linked DOs in the SO
/// baseline, so subtracting full DR allocation or independently aggregating DO
/// quantity would double-count demand.
/// </summary>
public static class SaSoDeliveryRequestCapacity
{
    public static async Task<IReadOnlyDictionary<SaSoDrLineKey, SaSoDrCapacityFacts>> LoadAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<SaSoDrLineKey> lineKeys,
        SaSoDrCapacityMode mode = SaSoDrCapacityMode.AllActiveSources,
        long? excludeDeliveryRequestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var keys = lineKeys
            .Select(x => x.Normalize())
            .Where(x => x.SoNo.Length > 0 && x.CustRel > 0 && x.SoLine > 0)
            .Distinct(SaSoDrLineKeyComparer.Instance)
            .ToList();

        var empty = new Dictionary<SaSoDrLineKey, SaSoDrCapacityFacts>(SaSoDrLineKeyComparer.Instance);
        if (company.Length == 0 || branch.Length == 0 || keys.Count == 0)
        {
            return empty;
        }

        var soNos = keys.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var custRels = keys.Select(x => x.CustRel).Distinct().ToList();
        var soLines = keys.Select(x => x.SoLine).Distinct().ToList();

        var sourcesQuery =
            from source in db.SaDeliveryRequestSources.AsNoTracking()
            join header in db.SaDeliveryRequests.AsNoTracking()
                on source.DeliveryRequestId equals header.Uid
            where source.CompanyCode == company
                && source.BranchCode == branch
                && header.CompanyCode == company
                && header.BranchCode == branch
                && source.IsActive
                && (excludeDeliveryRequestId == null
                    || source.DeliveryRequestId != excludeDeliveryRequestId.Value)
                && soNos.Contains(source.SoNo)
                && custRels.Contains(source.CustRel)
                && soLines.Contains(source.SoLine)
                && (mode == SaSoDrCapacityMode.AllActiveSources
                    || header.Status == ErpWeb.Model.Entities.Sales.SaDeliveryRequestStatuses.Released
                    || header.Status == ErpWeb.Model.Entities.Sales.SaDeliveryRequestStatuses.InProduction)
            select new
            {
                SourceId = source.Uid,
                source.SoNo,
                source.CustRel,
                source.SoLine,
                source.AllocatedProductionQty
            };

        var sources = await sourcesQuery.ToListAsync(cancellationToken);
        var requestedKeys = keys.ToHashSet(SaSoDrLineKeyComparer.Instance);
        sources = sources
            .Where(source => requestedKeys.Contains(
                new SaSoDrLineKey(source.SoNo, source.CustRel, source.SoLine).Normalize()))
            .ToList();
        if (sources.Count == 0)
        {
            return empty;
        }

        var sourceIds = sources.Select(x => x.SourceId).Distinct().ToList();
        var linkedDoRows = await (
                from detail in db.SaDoDetails.AsNoTracking()
                join deliveryOrder in db.SaDos.AsNoTracking()
                    on new { detail.CompanyCode, detail.BranchCode, detail.DoNo }
                    equals new { deliveryOrder.CompanyCode, deliveryOrder.BranchCode, deliveryOrder.DoNo }
                where detail.CompanyCode == company
                    && detail.BranchCode == branch
                    && detail.DeliveryRequestSourceId.HasValue
                    && sourceIds.Contains(detail.DeliveryRequestSourceId.Value)
                    && deliveryOrder.DeletedAtUtc == null
                    && (deliveryOrder.Status == SaDoStatuses.New
                        || deliveryOrder.Status == SaDoStatuses.Posted
                        || deliveryOrder.Status == SaDoStatuses.Closed)
                group detail by detail.DeliveryRequestSourceId!.Value
                into grouped
                select new
                {
                    SourceId = grouped.Key,
                    LinkedDoProductionQty = grouped.Sum(x => x.StdQty)
                })
            .ToListAsync(cancellationToken);

        var linkedBySource = linkedDoRows.ToDictionary(
            x => x.SourceId,
            x => SaSoQty.RoundQty(x.LinkedDoProductionQty));

        var facts = new Dictionary<SaSoDrLineKey, SaSoDrCapacityFacts>(SaSoDrLineKeyComparer.Instance);
        foreach (var source in sources)
        {
            var key = new SaSoDrLineKey(source.SoNo, source.CustRel, source.SoLine).Normalize();
            var allocated = SaSoQty.RoundQty(Math.Max(source.AllocatedProductionQty, 0m));
            var linked = linkedBySource.GetValueOrDefault(source.SourceId);
            var outstanding = OutstandingForSource(allocated, linked);

            if (facts.TryGetValue(key, out var current))
            {
                facts[key] = new SaSoDrCapacityFacts(
                    SaSoQty.RoundQty(current.ActiveAllocatedProductionQty + allocated),
                    SaSoQty.RoundQty(current.LinkedDoProductionQty + linked),
                    SaSoQty.RoundQty(current.OutstandingProductionQty + outstanding));
            }
            else
            {
                facts[key] = new SaSoDrCapacityFacts(allocated, linked, outstanding);
            }
        }

        return facts;
    }

    /// <summary>Clamps each source before line-level aggregation.</summary>
    public static decimal OutstandingForSource(decimal allocatedProductionQty, decimal linkedDoProductionQty) =>
        SaSoQty.RoundQty(Math.Max(SaSoQty.RoundQty(allocatedProductionQty)
            - SaSoQty.RoundQty(Math.Max(linkedDoProductionQty, 0m)), 0m));

    private sealed class SaSoDrLineKeyComparer : IEqualityComparer<SaSoDrLineKey>
    {
        public static readonly SaSoDrLineKeyComparer Instance = new();

        public bool Equals(SaSoDrLineKey x, SaSoDrLineKey y) =>
            x.CustRel == y.CustRel
            && x.SoLine == y.SoLine
            && string.Equals(x.SoNo, y.SoNo, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(SaSoDrLineKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SoNo ?? string.Empty),
                obj.CustRel,
                obj.SoLine);
    }
}
