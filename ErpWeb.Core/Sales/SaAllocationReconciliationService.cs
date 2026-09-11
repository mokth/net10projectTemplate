using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

public static class SaAllocationFindingCodes
{
    public const string OverDelivered = "OVER_DELIVERED";
    public const string OverBilled = "OVER_BILLED";
    public const string AllocationMismatch = "ALLOCATION_MISMATCH";
    public const string StrandedLine = "STRANDED_LINE";
    public const string WrittenOffQtyMismatch = "WRITTEN_OFF_QTY_MISMATCH";
    public const string WrittenOffNoAudit = "WRITTEN_OFF_NO_AUDIT";
    /// <summary>I9: the §3.3 billable accounting identity did not reconcile.</summary>
    public const string BillableIdentityMismatch = "BILLABLE_IDENTITY_MISMATCH";
}

public static class SaAllocationSeverities
{
    public const string Error = "ERROR";
    public const string Warning = "WARNING";
    public const string Info = "INFO";
}

/// <summary>
/// E8 finding. Actionable, not merely diagnostic — every finding carries enough context to locate
/// and explain the defect (R3 plan §8.3).
/// </summary>
public sealed class SaAllocationFinding
{
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = SaAllocationSeverities.Error;
    public string? SoNo { get; init; }
    public short? SoLine { get; init; }
    public string? DoNo { get; init; }
    public string? InvNo { get; init; }
    public decimal? Expected { get; init; }
    public decimal? Actual { get; init; }
    public string Explanation { get; init; } = string.Empty;
}

public sealed class SaAllocationReconcileResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<SaAllocationFinding> Findings { get; init; } = [];

    public bool HasErrors => Findings.Any(x => x.Severity == SaAllocationSeverities.Error);
    public bool HasIntegrityErrors => Findings.Count > 0;
    public string Status => Findings.Count == 0
        ? "OK"
        : HasErrors
            ? "SALES DATA INTEGRITY ERROR"
            : "SALES DATA WARNINGS";

    public static SaAllocationReconcileResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };

    public static SaAllocationReconcileResult Ok(IReadOnlyList<SaAllocationFinding> findings) =>
        new() { Succeeded = true, Findings = findings };
}

/// <summary>
/// E8 — read-only reconciliation of the SO/DO/INV allocation ledger against the persisted SO
/// projections and the force-close write-off. <b>Never mutates.</b>
/// </summary>
public interface ISaAllocationReconciliationService
{
    Task<SaAllocationReconcileResult> ReconcileAsync(
        string? soNo = null,
        CancellationToken cancellationToken = default);
}

public sealed class SaAllocationReconciliationService : ISaAllocationReconciliationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public SaAllocationReconciliationService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<SaAllocationReconcileResult> ReconcileAsync(
        string? soNo = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return SaAllocationReconcileResult.Fail("Invalid company or branch context.");
        }

        var company = scope.CompanyCode;
        var branch = scope.BranchCode!;
        var filter = string.IsNullOrWhiteSpace(soNo) ? null : soNo.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var headers = await db.SaSos.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.IsCurrent
                && (filter == null || x.SoNo == filter))
            .Select(x => new { x.SoNo, x.CustRel })
            .ToListAsync(cancellationToken);

        var findings = new List<SaAllocationFinding>();
        foreach (var header in headers)
        {
            await ReconcileSalesOrderAsync(db, company, branch, header.SoNo, header.CustRel, findings, cancellationToken);
        }

        return SaAllocationReconcileResult.Ok(findings);
    }

    private static async Task ReconcileSalesOrderAsync(
        AppDbContext db,
        string company,
        string branch,
        string soNo,
        short custRel,
        List<SaAllocationFinding> findings,
        CancellationToken cancellationToken)
    {
        var details = await db.SaSoDetails.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.SoNo == soNo && x.CustRel == custRel)
            .Select(x => new
            {
                x.Line,
                x.OrderQty,
                x.DeliveredQty,
                x.InvoicedQty,
                x.WrittenOffQty
            })
            .ToListAsync(cancellationToken);

        if (details.Count == 0)
        {
            return;
        }

        var reserveMap = await SaSoLineReserve.SumBySoLinesAsync(db, company, branch, [soNo], cancellationToken: cancellationToken);

        // Ledger aggregates, keyed by SO line.
        var deliveredByLine = await db.SaDocApplications.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.SourceDocType == SaDocTypes.So && x.SourceDocId == soNo
                && x.TargetDocType == SaDocTypes.Do
                && (x.SourceCustRel <= 0 || x.SourceCustRel == custRel)
                && x.SourceLineId > 0)
            .GroupBy(x => x.SourceLineId)
            .Select(g => new { Line = g.Key, Qty = g.Sum(x => x.AppliedQty) })
            .ToListAsync(cancellationToken);

        var invoicedDirect = await db.SaDocApplications.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.SourceDocType == SaDocTypes.So && x.SourceDocId == soNo
                && x.TargetDocType == SaDocTypes.Inv
                && (x.SourceCustRel <= 0 || x.SourceCustRel == custRel)
                && x.SourceLineId > 0)
            .GroupBy(x => x.SourceLineId)
            .Select(g => new { Line = g.Key, Qty = g.Sum(x => x.AppliedQty) })
            .ToListAsync(cancellationToken);

        var invoicedViaDo = await db.SaDocApplications.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.SourceDocType == SaDocTypes.Do && x.TargetDocType == SaDocTypes.Inv
                && x.RelatedSoNo == soNo
                && (x.RelatedCustRel <= 0 || x.RelatedCustRel == custRel)
                && x.RelatedSoLine > 0)
            .GroupBy(x => x.RelatedSoLine)
            .Select(g => new { Line = g.Key, Qty = g.Sum(x => x.AppliedQty) })
            .ToListAsync(cancellationToken);

        var deliveredMap = ToLineMap(deliveredByLine, x => x.Line, x => x.Qty);
        var invoicedDirectMap = ToLineMap(invoicedDirect, x => x.Line, x => x.Qty);
        var invoicedViaDoMap = ToLineMap(invoicedViaDo, x => x.Line, x => x.Qty);

        // Force-closed DO lines linked to this SO revision — the immutable origin of a write-off.
        var closedDoLines = await (
                from h in db.SaDos.AsNoTracking()
                join d in db.SaDoDetails.AsNoTracking()
                    on new { h.CompanyCode, h.BranchCode, h.DoNo }
                    equals new { d.CompanyCode, d.BranchCode, d.DoNo }
                where h.CompanyCode == company && h.BranchCode == branch
                    && h.Status == SaDoStatuses.Closed
                    && d.SoNo == soNo
                    && d.SoLine != null && d.SoLine > 0
                    && (d.CustRel <= 0 || d.CustRel == custRel)
                select new { h.DoNo, Line = d.Line, SoLine = d.SoLine!.Value, d.Qty })
            .ToListAsync(cancellationToken);

        var closedDoAllocByDoLine = await db.SaDocApplications.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.SourceDocType == SaDocTypes.Do && x.TargetDocType == SaDocTypes.Inv)
            .GroupBy(x => new { x.SourceDocId, x.SourceLineId })
            .Select(g => new { g.Key.SourceDocId, g.Key.SourceLineId, Qty = g.Sum(x => x.AppliedQty) })
            .ToListAsync(cancellationToken);

        var allocByDoLine = closedDoAllocByDoLine
            .GroupBy(x => (x.SourceDocId, x.SourceLineId))
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.Qty));

        var stampedBatchRefs = await db.IvTrxBatches.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ForceCloseDate != null
                && x.RefNo != null)
            .Select(x => x.RefNo!)
            .ToListAsync(cancellationToken);
        var stampedRefs = stampedBatchRefs.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var detail in details)
        {
            var sums = SaSoLineReserve.GetSums(reserveMap, soNo, custRel, detail.Line);
            var eval = SaSoLineReserve.Evaluate(soNo, detail.Line, detail.OrderQty, detail.DeliveredQty, sums, 0m, 0m);

            var deliveredLedger = SaSoQty.RoundQty(deliveredMap.GetValueOrDefault(detail.Line));
            var invoicedLedger = SaSoQty.RoundQty(
                invoicedDirectMap.GetValueOrDefault(detail.Line) + invoicedViaDoMap.GetValueOrDefault(detail.Line));
            var order = SaSoQty.RoundQty(detail.OrderQty);

            if (deliveredLedger > order)
            {
                findings.Add(new SaAllocationFinding
                {
                    Code = SaAllocationFindingCodes.OverDelivered,
                    Severity = SaAllocationSeverities.Error,
                    SoNo = soNo,
                    SoLine = detail.Line,
                    Expected = order,
                    Actual = deliveredLedger,
                    Explanation = "Posted SO→DO allocation exceeds OrderQty."
                });
            }

            if (invoicedLedger > order)
            {
                findings.Add(new SaAllocationFinding
                {
                    Code = SaAllocationFindingCodes.OverBilled,
                    Severity = SaAllocationSeverities.Error,
                    SoNo = soNo,
                    SoLine = detail.Line,
                    Expected = order,
                    Actual = invoicedLedger,
                    Explanation = "Posted SO→INV plus DO→INV allocation exceeds OrderQty."
                });
            }

            if (SaSoQty.RoundQty(detail.DeliveredQty) != deliveredLedger
                || SaSoQty.RoundQty(detail.InvoicedQty) != invoicedLedger)
            {
                findings.Add(new SaAllocationFinding
                {
                    Code = SaAllocationFindingCodes.AllocationMismatch,
                    Severity = SaAllocationSeverities.Error,
                    SoNo = soNo,
                    SoLine = detail.Line,
                    Expected = invoicedLedger,
                    Actual = detail.InvoicedQty,
                    Explanation =
                        $"Persisted projections disagree with the ledger (DeliveredQty {detail.DeliveredQty} vs {deliveredLedger})."
                });
            }

            // ── Write-off reconciliation (§5.6) ──────────────────────────────────────────
            var lineDoLines = closedDoLines.Where(x => x.SoLine == detail.Line).ToList();
            var expectedWrittenOff = SaSoQty.RoundQty(lineDoLines.Sum(x =>
            {
                var alloc = allocByDoLine.GetValueOrDefault((x.DoNo, x.Line));
                return Math.Max(0m, SaSoQty.RoundQty(x.Qty) - alloc);
            }));
            var actualWrittenOff = SaSoQty.RoundQty(detail.WrittenOffQty);

            // WRITTEN_OFF_NO_AUDIT must be reported first: it invalidates the mismatch comparison.
            var unstamped = lineDoLines
                .Where(x => !stampedRefs.Contains(SaDoSpRefs.ToRefNo(x.DoNo)))
                .Select(x => x.DoNo)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (actualWrittenOff > 0m && (lineDoLines.Count == 0 || unstamped.Count > 0))
            {
                findings.Add(new SaAllocationFinding
                {
                    Code = SaAllocationFindingCodes.WrittenOffNoAudit,
                    Severity = SaAllocationSeverities.Warning,
                    SoNo = soNo,
                    SoLine = detail.Line,
                    DoNo = unstamped.FirstOrDefault(),
                    Expected = expectedWrittenOff,
                    Actual = actualWrittenOff,
                    Explanation = lineDoLines.Count == 0
                        ? "WrittenOffQty > 0 but no force-closed DO resolves as its origin."
                        : $"WrittenOffQty > 0 but DO {unstamped[0]} has no force-close batch stamp."
                });
            }
            else if (expectedWrittenOff != actualWrittenOff)
            {
                findings.Add(new SaAllocationFinding
                {
                    Code = SaAllocationFindingCodes.WrittenOffQtyMismatch,
                    Severity = SaAllocationSeverities.Warning,
                    SoNo = soNo,
                    SoLine = detail.Line,
                    Expected = expectedWrittenOff,
                    Actual = actualWrittenOff,
                    Explanation = "WrittenOffQty does not reconcile to the force-closed DO contributions."
                });
            }

            // ── STRANDED_LINE ────────────────────────────────────────────────────────────
            // A line is stranded only when it is billing-terminal in no sense AND nothing is in
            // flight that could still move it: no draft DO, no open posted DO, no draft invoice,
            // and no remaining deliverable or billable headroom. A line merely reserved by a draft
            // DO is NOT stranded — posting that DO moves it.
            var billingTerminal = SaSoQty.RoundQty(detail.InvoicedQty + detail.WrittenOffQty) >= order;
            var nothingInFlight = eval.NewDoQty == 0m
                && eval.PostedDoOpenQty <= 0m
                && eval.NewSoInvQty == 0m;
            if (!billingTerminal
                && nothingInFlight
                && eval.RemainingBillable <= 0m
                && eval.RemainingDeliverable <= 0m)
            {
                findings.Add(new SaAllocationFinding
                {
                    Code = SaAllocationFindingCodes.StrandedLine,
                    Severity = SaAllocationSeverities.Warning,
                    SoNo = soNo,
                    SoLine = detail.Line,
                    Expected = order,
                    Actual = SaSoQty.RoundQty(detail.InvoicedQty + detail.WrittenOffQty),
                    Explanation = "Line has no remaining deliverable or billable quantity but is not billing-terminal — force-close the blocking DO or SO."
                });
            }

            // ── I9: §3.3 billable identity ───────────────────────────────────────────────
            if (!eval.BillableIdentityHolds)
            {
                findings.Add(new SaAllocationFinding
                {
                    Code = SaAllocationFindingCodes.BillableIdentityMismatch,
                    Severity = SaAllocationSeverities.Error,
                    SoNo = soNo,
                    SoLine = detail.Line,
                    Expected = eval.OrderQty,
                    Actual = eval.BillableIdentityTotal,
                    Explanation = "§3.3 identity failed: "
                        + $"invoiced={eval.InvoicedQty} writtenOff={eval.WrittenOffQty} "
                        + $"postedDoOpen={eval.PostedDoOpenQty} newDo={eval.NewDoQty} "
                        + $"newSoInv={eval.NewSoInvQty} remainingBillable={eval.RemainingBillable}."
                });
            }
        }
    }

    private static Dictionary<short, decimal> ToLineMap<T>(
        IEnumerable<T> rows,
        Func<T, short> line,
        Func<T, decimal> qty) =>
        rows.GroupBy(line).ToDictionary(g => g.Key, g => g.Sum(qty));
}
