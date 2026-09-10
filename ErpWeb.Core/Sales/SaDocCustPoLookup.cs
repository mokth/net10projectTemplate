using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Batch-loads customer PO snapshots from sales-order headers for DO/invoice line display.
/// </summary>
internal static class SaDocCustPoLookup
{
    public static async Task<IReadOnlyDictionary<(string SoNo, short CustRel), string?>> LoadAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IEnumerable<(string SoNo, short? CustRel)> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(keys);

        var wanted = keys
            .Where(x => !string.IsNullOrWhiteSpace(x.SoNo))
            .Select(x => (SoNo: x.SoNo.Trim(), CustRel: x.CustRel is > 0 ? x.CustRel.Value : (short)1))
            .Distinct()
            .ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<(string SoNo, short CustRel), string?>(KeyComparer.Instance);
        }

        var soNos = wanted.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var custRels = wanted.Select(x => x.CustRel).Distinct().ToList();
        var rows = await db.SaSos.AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && soNos.Contains(x.SoNo)
                && custRels.Contains(x.CustRel))
            .Select(x => new { x.SoNo, x.CustRel, x.CustPo })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<(string SoNo, short CustRel), string?>(wanted.Count, KeyComparer.Instance);
        foreach (var row in rows)
        {
            map[(row.SoNo, row.CustRel)] = row.CustPo;
        }

        return map;
    }

    public static string? Resolve(
        IReadOnlyDictionary<(string SoNo, short CustRel), string?>? map,
        string? soNo,
        short? custRel,
        string? persisted = null)
    {
        if (!string.IsNullOrWhiteSpace(persisted))
        {
            return persisted;
        }

        if (map is null || string.IsNullOrWhiteSpace(soNo))
        {
            return null;
        }

        var rel = custRel is > 0 ? custRel.Value : (short)1;
        return map.TryGetValue((soNo.Trim(), rel), out var po) ? po : null;
    }

    private sealed class KeyComparer : IEqualityComparer<(string SoNo, short CustRel)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((string SoNo, short CustRel) x, (string SoNo, short CustRel) y) =>
            x.CustRel == y.CustRel
            && string.Equals(x.SoNo, y.SoNo, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string SoNo, short CustRel) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SoNo ?? string.Empty), obj.CustRel);
    }
}
