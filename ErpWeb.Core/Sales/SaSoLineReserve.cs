using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Single source of truth for SO-line soft reservation (draft DOs / draft direct invoices)
/// and remaining deliverable/billable qty. Does not write <c>SaDocApplication</c>.
/// <para>
/// <b>DeliveredQty</b> = posted <c>SO_DO</c> ledger (physical fulfillment already applied).
/// <b>LiveDoQty</b> = DO-path qty that reserves direct SO-invoice capacity
/// (NEW + POSTED + CLOSED SO-linked DOs). Do not collapse these two — they serve
/// invariants A (deliverable) and B (billable) respectively.
/// </para>
/// </summary>
public static class SaSoLineReserve
{
    public readonly record struct DocIdentity(string CompanyCode, string BranchCode, string DocNo);

    public sealed class SoLineSums
    {
        public decimal NewDoQty { get; init; }
        public decimal LiveDoQty { get; init; }
        public decimal NewSoInvQty { get; init; }
        public decimal PostedSoInv { get; init; }
    }

    /// <summary>
    /// Structured evaluation for one SO line. Callers format <see cref="ErrorCode"/> for UI.
    /// </summary>
    public sealed class EvaluateResult
    {
        public required string SoNo { get; init; }
        public required short SoLine { get; init; }
        public decimal OrderQty { get; init; }
        public decimal DeliveredQty { get; init; }
        public decimal PostedSoInv { get; init; }
        public decimal NewDoQty { get; init; }
        public decimal LiveDoQty { get; init; }
        public decimal NewSoInvQty { get; init; }
        public decimal ThisDoQty { get; init; }
        public decimal ThisSoInvQty { get; init; }
        public decimal RemainingDeliverable { get; init; }
        public decimal RemainingBillable { get; init; }
        public string? ErrorCode { get; init; }

        public bool Succeeded => ErrorCode is null;
    }

    /// <summary>
    /// Invariant A: DeliveredQty + NewDoQty + thisDoQty &lt;= OrderQty.
    /// Invariant B: PostedSoInv + NewSoInvQty + LiveDoQty + thisDoQty + thisSoInvQty &lt;= OrderQty
    /// (thisDoQty fills the gap when LiveDoQty excludes the document being saved/posted).
    /// </summary>
    public static EvaluateResult Evaluate(
        string soNo,
        short soLine,
        decimal orderQty,
        decimal deliveredQty,
        SoLineSums sums,
        decimal thisDoQty,
        decimal thisSoInvQty)
    {
        var order = SaSoQty.RoundQty(orderQty);
        var delivered = SaSoQty.RoundQty(deliveredQty);
        var newDo = SaSoQty.RoundQty(sums.NewDoQty);
        var liveDo = SaSoQty.RoundQty(sums.LiveDoQty);
        var newSoInv = SaSoQty.RoundQty(sums.NewSoInvQty);
        var postedSoInv = SaSoQty.RoundQty(sums.PostedSoInv);
        var thisDo = SaSoQty.RoundQty(thisDoQty);
        var thisInv = SaSoQty.RoundQty(thisSoInvQty);

        var remainingDeliverable = SaSoQty.RoundQty(order - delivered - newDo - thisDo);
        // thisDo occupies DO-path billable headroom when excluded from LiveDoQty.
        var remainingBillable = SaSoQty.RoundQty(order - postedSoInv - newSoInv - liveDo - thisDo - thisInv);

        string? error = null;
        if (remainingDeliverable < 0m || remainingBillable < 0m)
        {
            error = SaDocAllocationReasonCodes.OverAllocate;
        }

        return new EvaluateResult
        {
            SoNo = soNo,
            SoLine = soLine,
            OrderQty = order,
            DeliveredQty = delivered,
            PostedSoInv = postedSoInv,
            NewDoQty = newDo,
            LiveDoQty = liveDo,
            NewSoInvQty = newSoInv,
            ThisDoQty = thisDo,
            ThisSoInvQty = thisInv,
            RemainingDeliverable = remainingDeliverable,
            RemainingBillable = remainingBillable,
            ErrorCode = error
        };
    }

    public static decimal RemainingForNewDo(EvaluateResult withZeroThis) =>
        Math.Min(withZeroThis.RemainingDeliverable, withZeroThis.RemainingBillable);

    public static decimal RemainingForNewSoInv(EvaluateResult withZeroThis) =>
        withZeroThis.RemainingBillable;

    public static string FormatOverAllocate(EvaluateResult result) =>
        $"Sales Order {result.SoNo} line {result.SoLine} does not have enough remaining quantity.";

    /// <summary>
    /// Batch SQL SUM of live DO qty and NEW direct invoice qty per SO line.
    /// <paramref name="excludeDo"/> / <paramref name="excludeInv"/> are Company+Branch+DocNo scoped.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(string SoNo, short CustRel, short SoLine), SoLineSums>> SumBySoLinesAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<string> soNos,
        DocIdentity? excludeDo = null,
        DocIdentity? excludeInv = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var company = (companyCode ?? string.Empty).Trim();
        var branch = (branchCode ?? string.Empty).Trim();
        var keys = soNos
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<(string, short, short), SoLineSums>(new SoLineKeyComparer());
        if (company.Length == 0 || branch.Length == 0 || keys.Count == 0)
        {
            return result;
        }

        var newDo = await SumDoQtyAsync(
            db, company, branch, keys, newOnly: true, excludeDo, cancellationToken);
        var liveDo = await SumDoQtyAsync(
            db, company, branch, keys, newOnly: false, excludeDo, cancellationToken);
        var newSoInv = await SumNewSoInvQtyAsync(
            db, company, branch, keys, excludeInv, cancellationToken);
        var postedSoInv = await SumPostedSoInvAsync(
            db, company, branch, keys, cancellationToken);

        var allLines = newDo.Keys
            .Concat(liveDo.Keys)
            .Concat(newSoInv.Keys)
            .Concat(postedSoInv.Keys)
            .Distinct();

        foreach (var key in allLines)
        {
            result[key] = new SoLineSums
            {
                NewDoQty = newDo.GetValueOrDefault(key),
                LiveDoQty = liveDo.GetValueOrDefault(key),
                NewSoInvQty = newSoInv.GetValueOrDefault(key),
                PostedSoInv = postedSoInv.GetValueOrDefault(key)
            };
        }

        return result;
    }

    public static SoLineSums GetSums(
        IReadOnlyDictionary<(string SoNo, short CustRel, short SoLine), SoLineSums> map,
        string soNo,
        short custRel,
        short soLine) =>
        map.TryGetValue((soNo, custRel, soLine), out var sums) ? sums : new SoLineSums();

    private static async Task<Dictionary<(string, short, short), decimal>> SumDoQtyAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<string> soNos,
        bool newOnly,
        DocIdentity? excludeDo,
        CancellationToken cancellationToken)
    {
        var excludeNo = excludeDo is { } ex
            && string.Equals(ex.CompanyCode, company, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ex.BranchCode, branch, StringComparison.OrdinalIgnoreCase)
            ? (ex.DocNo ?? string.Empty).Trim()
            : string.Empty;

        var query =
            from d in db.SaDoDetails.AsNoTracking()
            join h in db.SaDos.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.DoNo }
                equals new { h.CompanyCode, h.BranchCode, h.DoNo }
            where d.CompanyCode == company
                  && d.BranchCode == branch
                  && soNos.Contains(d.SoNo)
                  && d.SoNo != string.Empty
                  && d.SoLine != null
                  && d.SoLine > 0
                  && (newOnly
                      ? h.Status == SaDoStatuses.New
                      : (h.Status == SaDoStatuses.New
                         || h.Status == SaDoStatuses.Posted
                         || h.Status == SaDoStatuses.Closed))
                  && (excludeNo.Length == 0 || d.DoNo != excludeNo)
            group d by new
            {
                d.SoNo,
                CustRel = d.CustRel > 0 ? d.CustRel!.Value : (short)1,
                SoLine = d.SoLine!.Value
            }
            into g
            select new
            {
                g.Key.SoNo,
                g.Key.CustRel,
                g.Key.SoLine,
                Qty = g.Sum(x => x.Qty)
            };

        var rows = await query.ToListAsync(cancellationToken);
        return rows.ToDictionary(
            x => (x.SoNo, x.CustRel, x.SoLine),
            x => SaSoQty.RoundQty(x.Qty),
            new SoLineKeyComparer());
    }

    private static async Task<Dictionary<(string, short, short), decimal>> SumNewSoInvQtyAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<string> soNos,
        DocIdentity? excludeInv,
        CancellationToken cancellationToken)
    {
        var excludeNo = excludeInv is { } ex
            && string.Equals(ex.CompanyCode, company, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ex.BranchCode, branch, StringComparison.OrdinalIgnoreCase)
            ? (ex.DocNo ?? string.Empty).Trim()
            : string.Empty;

        var query =
            from d in db.SaInvoiceDetails.AsNoTracking()
            join h in db.SaInvoices.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.InvNo }
                equals new { h.CompanyCode, h.BranchCode, h.InvNo }
            where d.CompanyCode == company
                  && d.BranchCode == branch
                  && soNos.Contains(d.SoNo)
                  && d.SoNo != string.Empty
                  && d.SoLine != null
                  && d.SoLine > 0
                  && !d.LinkDo
                  && h.Status == SaInvoiceStatuses.New
                  && (excludeNo.Length == 0 || d.InvNo != excludeNo)
            group d by new
            {
                d.SoNo,
                CustRel = d.CustRel > 0 ? d.CustRel!.Value : (short)1,
                SoLine = d.SoLine!.Value
            }
            into g
            select new
            {
                g.Key.SoNo,
                g.Key.CustRel,
                g.Key.SoLine,
                Qty = g.Sum(x => x.Qty)
            };

        var rows = await query.ToListAsync(cancellationToken);
        return rows.ToDictionary(
            x => (x.SoNo, x.CustRel, x.SoLine),
            x => SaSoQty.RoundQty(x.Qty),
            new SoLineKeyComparer());
    }

    private static async Task<Dictionary<(string, short, short), decimal>> SumPostedSoInvAsync(
        AppDbContext db,
        string company,
        string branch,
        IReadOnlyList<string> soNos,
        CancellationToken cancellationToken)
    {
        var rows = await db.SaDocApplications.AsNoTracking()
            .Where(x =>
                x.CompanyCode == company
                && x.BranchCode == branch
                && x.SourceDocType == SaDocTypes.So
                && soNos.Contains(x.SourceDocId)
                && x.TargetDocType == SaDocTypes.Inv)
            .GroupBy(x => new
            {
                x.SourceDocId,
                CustRel = x.SourceCustRel > 0 ? x.SourceCustRel : (short)1,
                x.SourceLineId
            })
            .Select(g => new
            {
                SoNo = g.Key.SourceDocId,
                g.Key.CustRel,
                SoLine = g.Key.SourceLineId,
                Qty = g.Sum(x => x.AppliedQty)
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            x => (x.SoNo, x.CustRel, x.SoLine),
            x => SaSoQty.RoundQty(x.Qty),
            new SoLineKeyComparer());
    }

    private sealed class SoLineKeyComparer : IEqualityComparer<(string SoNo, short CustRel, short SoLine)>
    {
        public bool Equals((string SoNo, short CustRel, short SoLine) x, (string SoNo, short CustRel, short SoLine) y) =>
            x.SoLine == y.SoLine
            && x.CustRel == y.CustRel
            && string.Equals(x.SoNo, y.SoNo, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string SoNo, short CustRel, short SoLine) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SoNo ?? string.Empty), obj.CustRel, obj.SoLine);
    }
}
