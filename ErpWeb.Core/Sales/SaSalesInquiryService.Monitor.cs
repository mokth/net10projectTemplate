using System.Linq.Expressions;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales Monitor Phase A (plan-salesDecisionSupport.prompt.md) — read-only decision-support reads over
/// the columns the ERP already persists.
///
/// <para>
/// Locked rules this partial must keep (the plan's §3 guardrails):
/// <list type="bullet">
/// <item>No new columns, statuses or business rules. Every filter is a direct check of a persisted
/// column; ageing/expiry differences are computed in memory <b>after</b> materialising the page,
/// because SQLite and SQL Server date functions differ.</item>
/// <item>All <i>filtering</i> stays server-side. The ageing bucket and the overdue test are expressible
/// as ranges over the persisted <c>SoDate</c> / <c>DeliveryDate</c>, so they are SQL predicates and
/// paging remains server-side — the page is never materialised wholesale for a filter.</item>
/// <item>Header rollups (<c>TotAmnt</c>) are counted once per order, never multiplied by the line count.</item>
/// <item>Monitoring thresholds (<see cref="SaMonitorLimits"/>) are presentation vocabulary. They never
/// modify transactional processing and are never written back to a document.</item>
/// <item>Every method takes <c>menuCode</c> and is gated tenant-first, then ACCESS, on that menu.</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class SaSalesInquiryService
{
    /// <summary>
    /// An SO header + one of its lines. A named class (not an anonymous type) so it can cross method
    /// boundaries — an anonymous <c>IQueryable</c> cannot be passed to a helper.
    /// </summary>
    private sealed class SoLineSlice
    {
        public SaSo Header { get; init; } = null!;
        public SaSoDetail Line { get; init; } = null!;
    }

    /// <summary>A DO header + one of its lines.</summary>
    private sealed class DoLineSlice
    {
        public SaDo Header { get; init; } = null!;
        public SaDoDetail Line { get; init; } = null!;
    }

    // ============================ A1 · SO ageing & overdue delivery ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaSoAgeingRow>>> GetSoAgeingAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaInquiryPage<SaSoAgeingRow>>(
            menuCode, MenuCodes.SalesSoAgeing, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);
        var asOf = AsOf(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildSoAgeSlice(db, gate.CompanyCode!, gate.BranchCode!, prepared, asOf);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await slice
            .OrderBy(x => x.Header.SoDate)
            .ThenBy(x => x.Header.SoNo)
            .ThenBy(x => x.Line.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaSoAgeingRow
            {
                SoNo = x.Header.SoNo,
                Rev = x.Header.CustRel,
                SoDate = x.Header.SoDate,
                Status = x.Header.Status,
                FulfillmentStatus = x.Header.FulfillmentStatus,
                BillingStatus = x.Header.BillingStatus,
                CustCode = x.Header.CustCode,
                CustName = x.Header.CustName,
                SalesRep = x.Header.SalesRep,
                TotAmnt = x.Header.TotAmnt,
                Line = x.Line.Line,
                ICode = x.Line.ICode,
                IDesc = x.Line.IDesc,
                OrderQty = x.Line.OrderQty,
                DeliveredQty = x.Line.DeliveredQty,
                InvoicedQty = x.Line.InvoicedQty,
                BalanceQty = x.Line.BalanceQty,
                WrittenOffQty = x.Line.WrittenOffQty,
                DeliveryDate = x.Line.DeliveryDate
            })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            DecorateAgeing(row, asOf);
        }

        return IvMasterOperationResult<SaInquiryPage<SaSoAgeingRow>>.Ok(
            new SaInquiryPage<SaSoAgeingRow> { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<SaSoAgeingSummary>> GetSoAgeingSummaryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaSoAgeingSummary>(
            menuCode, MenuCodes.SalesSoAgeing, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();
        var asOf = AsOf(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildSoAgeSlice(db, gate.CompanyCode!, gate.BranchCode!, prepared, asOf);

        // Count and value each ORDER once. A header rollup must never be multiplied by its line count,
        // so the distinct-header projection is the only authority for those two figures.
        var headers = await slice
            .Select(x => new { x.Header.SoNo, x.Header.CustRel, x.Header.TotAmnt, x.Header.SoDate })
            .Distinct()
            .ToListAsync(cancellationToken);

        var pendingQty = await slice.SumAsync(x => x.Line.BalanceQty, cancellationToken);
        var overdueQty = await slice
            .Where(BuildDeliveryOverdue<SoLineSlice>(x => x.Line.DeliveryDate, asOf))
            .SumAsync(x => x.Line.BalanceQty, cancellationToken);

        // A derived ratio of persisted columns (line net × outstanding share). Labelled "derived" in
        // the UI; the conditional keeps a zero ordered quantity out of the division.
        var outstanding = await slice.SumAsync(
            x => x.Line.OrderQty == 0m
                ? 0m
                : x.Line.NetAmount * x.Line.BalanceQty / x.Line.OrderQty,
            cancellationToken);

        var buckets = SaMonitorBuckets.AgeBuckets
            .Select(label =>
            {
                var inBucket = headers.Where(h => MatchAgeBucket(label, (asOf - h.SoDate.Date).Days)).ToList();

                return new SaAgeBucketRow
                {
                    Label = label,
                    Count = inBucket.Count,
                    Value = Money(inBucket.Sum(h => h.TotAmnt)),
                    Qty = Money(0m)
                };
            })
            .ToList();

        // Per-bucket outstanding quantity, one bounded aggregate per bucket (the bucket is a SoDate
        // range, so this stays server-side and never materialises the slice).
        for (var i = 0; i < SaMonitorBuckets.AgeBuckets.Count; i++)
        {
            var label = SaMonitorBuckets.AgeBuckets[i];
            var ranged = ApplyAgeBucketRange(
                slice, x => x.Header.SoDate, label, asOf, SaMonitorBuckets.AgeBuckets);

            buckets[i].Qty = await ranged.SumAsync(x => x.Line.BalanceQty, cancellationToken);
        }

        return IvMasterOperationResult<SaSoAgeingSummary>.Ok(new SaSoAgeingSummary
        {
            OpenCount = headers.Count,
            OriginalValue = Money(headers.Sum(h => h.TotAmnt)),
            OutstandingValue = Money(outstanding),
            PendingQty = pendingQty,
            OverdueQty = overdueQty,
            Buckets = buckets
        });
    }

    /// <summary>
    /// The A1 slice: current SO lines, already filtered server-side (date, customer, salesman, status,
    /// ageing bucket, overdue-only). Overdue uses the <b>line's</b> expected delivery date and is
    /// strictly in the past — a delivery due today is not overdue.
    /// </summary>
    private static IQueryable<SoLineSlice> BuildSoAgeSlice(
        AppDbContext db,
        string company,
        string branch,
        SaInquiryQuery query,
        DateTime asOf)
    {
        IQueryable<SoLineSlice> slice = db.SaSos.AsNoTracking()
            .Where(h => h.CompanyCode == company && h.BranchCode == branch && h.IsCurrent)
            .SelectMany(h => h.Details, (h, l) => new SoLineSlice { Header = h, Line = l });

        slice = ApplyDate(slice, x => x.Header.SoDate, query);
        slice = ApplyText(slice, x => x.Header.CustCode, query.CustCode);
        slice = ApplyText(slice, x => x.Header.SalesRep, query.SalesmanCode);
        slice = ApplyText(slice, x => x.Header.Status, query.Status);

        slice = ApplyAgeBucketRange(slice, x => x.Header.SoDate, query.Bucket, asOf, SaMonitorBuckets.AgeBuckets);

        if (query.OverdueOnly)
        {
            slice = slice.Where(BuildDeliveryOverdue<SoLineSlice>(x => x.Line.DeliveryDate, asOf));
        }

        return slice;
    }

    /// <summary>Fills the derived ageing columns for one A1 row.</summary>
    private static void DecorateAgeing(SaSoAgeingRow row, DateTime asOf)
    {
        // SO age is the age of the ORDER. It is never delivery ageing and is never derived from
        // DeliveryDate — the two columns answer different questions and the grid keeps them apart.
        row.AgeDays = (asOf - row.SoDate.Date).Days;
        row.AgeBucket = SaMonitorBuckets.AgeBucket(row.AgeDays);

        if (row.DeliveryDate is DateTime due)
        {
            var lateBy = (asOf - due.Date).Days;
            row.IsOverdueDelivery = lateBy > 0;
            row.OverdueDays = lateBy > 0 ? lateBy : 0;
        }
    }

    /// <summary>True when <paramref name="label"/> is the bucket that <paramref name="ageDays"/> falls in.</summary>
    private static bool MatchAgeBucket(string label, int ageDays) =>
        string.Equals(label, SaMonitorBuckets.AgeBucket(ageDays), StringComparison.Ordinal);

    // ============================ A2 · Delivered not fully invoiced ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaDoNotFullyInvoicedRow>>> GetDeliveredNotFullyInvoicedAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaInquiryPage<SaDoNotFullyInvoicedRow>>(
            menuCode, MenuCodes.SalesDoNotFullyInvoiced, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);
        var asOf = AsOf(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildNotFullyInvoicedSlice(db, gate.CompanyCode!, gate.BranchCode!, prepared);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await slice
            .OrderBy(x => x.Header.DoDate)
            .ThenBy(x => x.Header.DoNo)
            .ThenBy(x => x.Line.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaDoNotFullyInvoicedRow
            {
                DoNo = x.Header.DoNo,
                DoDate = x.Header.DoDate,
                PostedDate = x.Header.PostedDate,
                Status = x.Header.Status,
                BillingStatus = x.Header.BillingStatus,
                CustCode = x.Header.CustCode,
                CustName = x.Header.CustName,
                SalesRep = x.Header.SalesRep,
                TotAmnt = x.Header.TotAmnt,
                Line = x.Line.Line,
                ICode = x.Line.ICode,
                IDesc = x.Line.IDesc,
                Qty = x.Line.Qty,
                SoNo = x.Line.SoNo,
                LineInvNo = x.Line.InvNo
            })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            DecorateNotFullyInvoiced(row, asOf);
        }

        return IvMasterOperationResult<SaInquiryPage<SaDoNotFullyInvoicedRow>>.Ok(
            new SaInquiryPage<SaDoNotFullyInvoicedRow> { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<SaDoNotFullyInvoicedSummary>> GetDeliveredNotFullyInvoicedSummaryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaDoNotFullyInvoicedSummary>(
            menuCode, MenuCodes.SalesDoNotFullyInvoiced, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildNotFullyInvoicedSlice(db, gate.CompanyCode!, gate.BranchCode!, prepared);

        var total = await slice.CountAsync(cancellationToken);
        if (total > MaxExportRows)
        {
            return IvMasterOperationResult<SaDoNotFullyInvoicedSummary>.Fail(
                IvMasterErrorCode.Validation,
                $"Too many delivery lines ({total:N0}) to summarise. Narrow the date range.");
        }

        // The four-state split must use the SAME rule the grid uses, so it is counted in memory from one
        // light projection of the already-filtered slice — never re-expressed in SQL, which is how a
        // summary and its grid drift apart.
        var lines = await slice
            .Select(x => new
            {
                x.Header.DoNo,
                x.Header.BillingStatus,
                x.Line.InvNo,
                x.Line.Qty,
                x.Line.NetAmount
            })
            .ToListAsync(cancellationToken);

        var states = SaDoInvoiceStateOrder
            .Select(state =>
            {
                var inState = lines
                    .Where(l => string.Equals(
                        ResolveInvoiceState(l.BillingStatus, l.InvNo), state, StringComparison.Ordinal))
                    .ToList();

                return new SaInvoiceStateRow
                {
                    State = state,
                    Count = inState.Count,
                    Qty = inState.Sum(l => l.Qty),
                    Value = Money(inState.Sum(l => l.NetAmount)),
                    IsPending = SaDoInvoiceStates.PendingStates.Contains(state, StringComparer.Ordinal)
                };
            })
            .ToList();

        var pending = lines
            .Where(l => SaDoInvoiceStates.PendingStates.Contains(
                ResolveInvoiceState(l.BillingStatus, l.InvNo), StringComparer.Ordinal))
            .ToList();

        return IvMasterOperationResult<SaDoNotFullyInvoicedSummary>.Ok(new SaDoNotFullyInvoicedSummary
        {
            DoCount = lines
                .Select(l => l.DoNo)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            LineCount = lines.Count,
            NotFullyInvoicedQty = pending.Sum(l => l.Qty),
            NotFullyInvoicedValue = Money(pending.Sum(l => l.NetAmount)),
            States = states
        });
    }

    /// <summary>Display order of the four <see cref="SaDoInvoiceStates"/> values.</summary>
    private static readonly IReadOnlyList<string> SaDoInvoiceStateOrder =
    [
        SaDoInvoiceStates.NoInvoice,
        SaDoInvoiceStates.Partial,
        SaDoInvoiceStates.Invoiced,
        SaDoInvoiceStates.WrittenOff
    ];

    /// <summary>
    /// The A2 slice: DO lines whose billing is incomplete. <c>WRITTEN_OFF</c> is terminal for billing, so
    /// it is excluded from "pending" along with <c>FULL</c>.
    /// <para>
    /// The header rollup decides full/written-off; the <b>line</b> carries the invoice number
    /// (<c>SaDo</c> has no header <c>InvNo</c> at all), so the pending predicate reads both.
    /// </para>
    /// </summary>
    private static IQueryable<DoLineSlice> BuildNotFullyInvoicedSlice(
        AppDbContext db,
        string company,
        string branch,
        SaInquiryQuery query)
    {
        IQueryable<DoLineSlice> slice = db.SaDos.AsNoTracking()
            .Where(h => h.CompanyCode == company
                && h.BranchCode == branch
                && h.Status == SaDoStatuses.Posted)
            .SelectMany(h => h.Details, (h, l) => new DoLineSlice { Header = h, Line = l });

        slice = ApplyDate(slice, x => x.Header.DoDate, query);
        slice = ApplyText(slice, x => x.Header.CustCode, query.CustCode);
        slice = ApplyText(slice, x => x.Header.SalesRep, query.SalesmanCode);
        slice = ApplyText(slice, x => x.Header.Status, query.Status);

        var state = Normalize(query.InvoiceState);
        if (state is not null)
        {
            slice = slice.Where(BuildInvoiceState<DoLineSlice>(state));
        }
        else if (query.PendingOnly)
        {
            slice = slice.Where(BuildPendingInvoice<DoLineSlice>());
        }

        return slice;
    }

    /// <summary>Fills the derived A2 columns for one row.</summary>
    private static void DecorateNotFullyInvoiced(SaDoNotFullyInvoicedRow row, DateTime asOf)
    {
        // PostedDate is the delivery acceptance stamp; DoDate is the documented fallback when a legacy
        // row has no posted stamp (plan D3).
        var delivered = (row.PostedDate ?? row.DoDate).Date;
        row.DaysSinceDelivered = (asOf - delivered).Days;

        row.InvoiceState = ResolveInvoiceState(row.BillingStatus, row.LineInvNo);
        row.IsPendingInvoice = SaDoInvoiceStates.PendingStates
            .Contains(row.InvoiceState, StringComparer.Ordinal);
    }

    /// <summary>
    /// The one definition of the per-line invoice state (plan §4). The header rollup wins where it is
    /// terminal; otherwise a line with no invoice number is pending, and one with a number is invoiced.
    /// </summary>
    private static string ResolveInvoiceState(string? billingStatus, string? lineInvNo)
    {
        var billing = (billingStatus ?? string.Empty).Trim().ToUpperInvariant();

        if (billing == SaDualStatuses.WrittenOff)
        {
            return SaDoInvoiceStates.WrittenOff;
        }

        if (!string.IsNullOrWhiteSpace(lineInvNo))
        {
            return SaDoInvoiceStates.Invoiced;
        }

        return billing == SaDualStatuses.Partial
            ? SaDoInvoiceStates.Partial
            : SaDoInvoiceStates.NoInvoice;
    }

    /// <summary>
    /// "This line still owes billing" — the exact complement of <see cref="ResolveInvoiceState"/>'s
    /// non-pending values: the header is not terminal for billing and the line carries no invoice
    /// number. A <c>FULL</c> header whose line has no invoice number is therefore still listed, which is
    /// the real inconsistency the exception check reports rather than a false negative here.
    /// </summary>
    private static Expression<Func<T, bool>> BuildPendingInvoice<T>()
    {
        var header = Expression.Parameter(typeof(T), "x");
        var sliceHeader = Expression.Property(header, "Header");
        var billing = Expression.Property(sliceHeader, "BillingStatus");
        var invNo = Expression.Property(Expression.Property(header, "Line"), "InvNo");

        var writtenOff = Expression.NotEqual(
            billing, Expression.Constant(SaDualStatuses.WrittenOff, typeof(string)));

        var blankInvNo = Expression.OrElse(
            Expression.Equal(invNo, Expression.Constant(null, typeof(string))),
            Expression.Equal(invNo, Expression.Constant(string.Empty, typeof(string))));

        return Expression.Lambda<Func<T, bool>>(
            Expression.AndAlso(writtenOff, blankInvNo), header);
    }

    /// <summary>Builds the SQL predicate for one <see cref="SaDoInvoiceStates"/> value.</summary>
    private static Expression<Func<T, bool>> BuildInvoiceState<T>(string state)
    {
        var header = Expression.Parameter(typeof(T), "x");
        var billing = Expression.Property(Expression.Property(header, "Header"), "BillingStatus");
        var invNo = Expression.Property(Expression.Property(header, "Line"), "InvNo");

        var blank = Expression.OrElse(
            Expression.Equal(invNo, Expression.Constant(null, typeof(string))),
            Expression.Equal(invNo, Expression.Constant(string.Empty, typeof(string))));

        Expression body = state switch
        {
            SaDoInvoiceStates.WrittenOff => Expression.Equal(
                billing, Expression.Constant(SaDualStatuses.WrittenOff, typeof(string))),

            SaDoInvoiceStates.Invoiced => Expression.Not(blank),

            SaDoInvoiceStates.Partial => Expression.AndAlso(
                blank,
                Expression.Equal(billing, Expression.Constant(SaDualStatuses.Partial, typeof(string)))),

            // NO_INVOICE (and the default for an unknown token): no line invoice number and the header
            // is not already PARTIAL — i.e. nothing on this order was billed through this line.
            _ => Expression.AndAlso(
                blank,
                Expression.NotEqual(billing, Expression.Constant(SaDualStatuses.Partial, typeof(string))))
        };

        return Expression.Lambda<Func<T, bool>>(body, header);
    }

    // ============================ A3 · Quotation expiry watch ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaQtExpiryRow>>> GetQtExpiryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaInquiryPage<SaQtExpiryRow>>(
            menuCode, MenuCodes.SalesQtExpiry, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);
        var asOf = AsOf(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildQtExpirySlice(db, gate.CompanyCode!, gate.BranchCode!, prepared, asOf);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await slice
            .OrderBy(x => x.ValidUntil)
            .ThenBy(x => x.QtNo)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaQtExpiryRow
            {
                QtNo = x.QtNo,
                Rev = x.CustRel,
                QtDate = x.QtDate,
                ValidUntil = x.ValidUntil,
                Status = x.Status,
                ConversionStatus = x.ConversionStatus,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesRep = x.SalesRep,
                TotAmnt = x.TotAmnt
            })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            DecorateQtExpiry(row, asOf);
        }

        return IvMasterOperationResult<SaInquiryPage<SaQtExpiryRow>>.Ok(
            new SaInquiryPage<SaQtExpiryRow> { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<SaQtExpirySummary>> GetQtExpirySummaryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaQtExpirySummary>(
            menuCode, MenuCodes.SalesQtExpiry, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();
        var asOf = AsOf(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var slice = BuildQtExpirySlice(db, gate.CompanyCode!, gate.BranchCode!, prepared, asOf);

        // Quotations are header-level, so the whole filtered set is order-sized and can be summarised
        // in memory from one projection — no line fan-out, no repeated aggregate.
        var live = await slice
            .Select(x => new { x.Status, x.SalesRep, x.ValidUntil, x.TotAmnt })
            .ToListAsync(cancellationToken);

        var decorated = live
            .Select(x =>
            {
                var expired = IsQtExpired(x.Status, x.ValidUntil, asOf);
                var days = (x.ValidUntil.Date - asOf).Days;
                var soon = !expired && IsQtExpiringSoon(x.Status, days);

                return (x.Status, x.SalesRep, x.TotAmnt, Expired: expired, Soon: soon, Revisable: IsQtRevisable(x.Status));
            })
            .ToList();

        var open = decorated.Where(x => x.Revisable).ToList();
        var soon = decorated.Where(x => x.Soon).ToList();
        var expired = decorated.Where(x => x.Expired).ToList();

        var byRep = decorated
            .GroupBy(x => x.SalesRep)
            .Select(g => new SaSalesRepValueRow
            {
                SalesRep = g.Key,
                Count = g.Count(),
                Value = Money(g.Sum(x => x.TotAmnt))
            })
            .OrderByDescending(x => x.Value)
            .ToList();

        return IvMasterOperationResult<SaQtExpirySummary>.Ok(new SaQtExpirySummary
        {
            OpenCount = open.Count,
            OpenValue = Money(open.Sum(x => x.TotAmnt)),
            ExpiringSoonCount = soon.Count,
            ExpiringSoonValue = Money(soon.Sum(x => x.TotAmnt)),
            ExpiredCount = expired.Count,
            ExpiredValue = Money(expired.Sum(x => x.TotAmnt)),
            BySalesRep = byRep
        });
    }

    /// <summary>
    /// The A3 slice: live (<c>IsCurrent</c>) quotation revisions. The expiry windows are ranges over the
    /// persisted <c>ValidUntil</c>, so they are SQL predicates and paging stays server-side.
    /// </summary>
    private static IQueryable<SaQt> BuildQtExpirySlice(
        AppDbContext db,
        string company,
        string branch,
        SaInquiryQuery query,
        DateTime asOf)
    {
        var slice = db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsCurrent);

        slice = ApplyDate(slice, x => x.QtDate, query);
        slice = ApplyText(slice, x => x.CustCode, query.CustCode);
        slice = ApplyText(slice, x => x.SalesRep, query.SalesmanCode);
        slice = ApplyText(slice, x => x.Status, query.Status);

        if (query.ExpiringSoonOnly)
        {
            slice = slice.Where(x => x.ValidUntil >= asOf
                && x.ValidUntil <= asOf.AddDays(SaMonitorLimits.QtExpiringSoonDays)
                && (x.Status == SaQtStatuses.New || x.Status == SaQtStatuses.Sent));
        }

        if (query.ExpiredOnly)
        {
            // The lazy sweep only ever expires NEW/SENT; ACCEPTED is never auto-expired, so an accepted
            // quotation past its date is not "expired" here.
            slice = slice.Where(x => x.ValidUntil < asOf
                && (x.Status == SaQtStatuses.New || x.Status == SaQtStatuses.Sent));
        }

        var bucket = Normalize(query.Bucket);
        if (bucket is not null)
        {
            slice = bucket switch
            {
                SaMonitorBuckets.Expired => slice.Where(x => x.ValidUntil < asOf),
                SaMonitorBuckets.Days0To7 => slice.Where(x => x.ValidUntil >= asOf
                    && x.ValidUntil <= asOf.AddDays(SaMonitorLimits.QtExpiringSoonDays)),
                SaMonitorBuckets.Days8To30 => slice.Where(x => x.ValidUntil > asOf.AddDays(SaMonitorLimits.QtExpiringSoonDays)
                    && x.ValidUntil <= asOf.AddDays(30)),
                SaMonitorBuckets.Over30 => slice.Where(x => x.ValidUntil > asOf.AddDays(30)),
                _ => slice
            };
        }

        return slice;
    }

    /// <summary>Fills the derived A3 columns for one row.</summary>
    private static void DecorateQtExpiry(SaQtExpiryRow row, DateTime asOf)
    {
        row.DaysToExpiry = (row.ValidUntil.Date - asOf).Days;
        row.IsExpired = IsQtExpired(row.Status, row.ValidUntil, asOf);
        row.IsExpiringSoon = !row.IsExpired && IsQtExpiringSoon(row.Status, row.DaysToExpiry);
        row.ExpiryBucket = SaMonitorBuckets.ExpiryBucket(row.DaysToExpiry, row.IsExpired);
    }

    /// <summary>
    /// Past validity. The shipped sweep only moves <see cref="SaQtStatuses.Expirable"/> statuses, so an
    /// <c>ACCEPTED</c> quotation past its date is deliberately not flagged as expired.
    /// </summary>
    private static bool IsQtExpired(string? status, DateTime validUntil, DateTime asOf) =>
        asOf > validUntil.Date
        && !string.Equals(status, SaQtStatuses.Accepted, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sweep will expire this revision: an expirable status inside the monitoring window.</summary>
    private static bool IsQtExpiringSoon(string? status, int daysToExpiry) =>
        daysToExpiry >= 0
        && daysToExpiry <= SaMonitorLimits.QtExpiringSoonDays
        && (string.Equals(status, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, SaQtStatuses.Sent, StringComparison.OrdinalIgnoreCase));

    /// <summary>Open = the documented revisable set (NEW/SENT/ACCEPTED), i.e. not yet won, lost or closed.</summary>
    private static bool IsQtRevisable(string? status) =>
        status is not null && SaQtStatuses.Revisable.Contains(status);

    // ============================ A4 · e-Invoice action queue ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceActionRow>>> GetEInvoiceActionQueueAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaInquiryPage<SaEInvoiceActionRow>>(
            menuCode, MenuCodes.SalesEInvoiceAction, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);
        var asOf = AsOf(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var union = EInvoiceStatusUnion(db, company, branch, prepared);
        var total = await union.CountAsync(cancellationToken);
        var rows = await union
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        // The registry is the authority for the current status; one batched lookup decorates the page.
        await DecorateEInvoiceRowsAsync(db, company, rows, cancellationToken);

        var actions = rows.Select(x => BuildActionRow(x, asOf)).ToList();

        return IvMasterOperationResult<SaInquiryPage<SaEInvoiceActionRow>>.Ok(
            new SaInquiryPage<SaEInvoiceActionRow> { Rows = actions, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<SaEInvoiceStatusBreakdown>> GetEInvoiceStatusBreakdownAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var (gate, failure) = await GateOwnAsync<SaEInvoiceStatusBreakdown>(
            menuCode, MenuCodes.SalesEInvoiceAction, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var prepared = query ?? new SaInquiryQuery();
        var asOf = AsOf(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var union = EInvoiceStatusUnion(db, company, branch, prepared);
        var total = await union.CountAsync(cancellationToken);

        // The strip counts every document in the window, so the whole filtered set is projected once and
        // decorated. Refused rather than truncated past the shared export cap — a header that silently
        // counted only part of the window would be worse than no header.
        if (total > MaxExportRows)
        {
            return IvMasterOperationResult<SaEInvoiceStatusBreakdown>.Fail(
                IvMasterErrorCode.Validation,
                $"Too many documents ({total:N0}) to summarise. Narrow the date range.");
        }

        var rows = await union
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .ToListAsync(cancellationToken);

        await DecorateEInvoiceRowsAsync(db, company, rows, cancellationToken);

        var actions = rows.Select(x => BuildActionRow(x, asOf)).ToList();

        var statuses = actions
            .GroupBy(x => x.StatusLabel)
            .Select(g => new SaEInvoiceStatusCountRow
            {
                Status = g.Key,
                Count = g.Count(),
                Value = Money(g.Sum(x => x.TotAmnt))
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        return IvMasterOperationResult<SaEInvoiceStatusBreakdown>.Ok(new SaEInvoiceStatusBreakdown
        {
            TotalCount = total,
            NeedsActionCount = actions.Count(x => x.ActionReason is not null),
            Statuses = statuses
        });
    }

    /// <summary>
    /// One definition of "why is this document on the queue", used by both the queue and the breakdown so
    /// the two can never disagree. Each reason is a single persisted-field comparison; the plan's
    /// "cancellation pending" reason was dropped at Gate 1 because no persisted field distinguishes it.
    /// </summary>
    private static SaEInvoiceActionRow BuildActionRow(SaEInvoiceStatusRow row, DateTime asOf)
    {
        var action = new SaEInvoiceActionRow
        {
            DocType = row.DocType,
            DocNo = row.DocNo,
            DocDate = row.DocDate,
            CustCode = row.CustCode,
            CustName = row.CustName,
            TotAmnt = row.TotAmnt,
            IrbmStatus = row.IrbmStatus,
            NormalizedStatus = row.NormalizedStatus,
            LatestSubmissionStatus = row.LatestSubmissionStatus,
            StatusLabel = row.StatusLabel,
            IrbmSentOn = row.IrbmSentOn
        };

        if (row.IrbmSentOn is DateTime sent)
        {
            action.DaysSinceSubmitted = (asOf - sent.Date).Days;
        }

        action.ActionReason = ResolveActionReason(row, action.DaysSinceSubmitted);
        return action;
    }

    private static string? ResolveActionReason(SaEInvoiceStatusRow row, int? daysSinceSubmitted)
    {
        if (row.IsNotSubmitted)
        {
            return SaEInvoiceActionReasons.NotSubmitted;
        }

        var status = row.LatestSubmissionStatus ?? row.NormalizedStatus;

        if (string.Equals(status, EInvoiceStatuses.Invalid, StringComparison.OrdinalIgnoreCase))
        {
            return SaEInvoiceActionReasons.Invalid;
        }

        if (string.Equals(status, EInvoiceStatuses.Failed, StringComparison.OrdinalIgnoreCase))
        {
            return SaEInvoiceActionReasons.Failed;
        }

        if (row.LatestSubmissionStatus is not null
            && !string.Equals(row.LatestSubmissionStatus, row.NormalizedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return SaEInvoiceActionReasons.StatusMismatch;
        }

        if (string.Equals(row.NormalizedStatus, EInvoiceStatuses.Submitted, StringComparison.OrdinalIgnoreCase)
            && daysSinceSubmitted is int days
            && days > SaMonitorLimits.EInvoicePendingDays)
        {
            // Presentation-only: the window names what the queue highlights. It never re-submits,
            // cancels or otherwise touches the document.
            return SaEInvoiceActionReasons.PendingTooLong;
        }

        return null;
    }

    // ============================ Shared helpers ============================

    /// <summary>
    /// Tenant scope resolves first (fail closed), then ACCESS, then the caller's menu is checked against
    /// THIS method's own screen. Without the last step any caller holding one inquiry menu could read
    /// every other screen — the "wrong menuCode" row of the plan's authorization matrix.
    /// </summary>
    private async Task<(IvInquiryScopeContext Gate, IvMasterOperationResult<T>? Failure)> GateOwnAsync<T>(
        string menuCode,
        string ownMenu,
        CancellationToken cancellationToken)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return (default, Fail<T>(gate));
        }

        if (!string.Equals(gate.MenuCode, ownMenu, StringComparison.OrdinalIgnoreCase))
        {
            return (default, IvMasterOperationResult<T>.Fail(
                IvMasterErrorCode.Validation,
                "That menu code does not belong to this screen."));
        }

        return (gate, null);
    }

    /// <summary>The company-local date every derived figure is measured against.</summary>
    private static DateTime AsOf(SaInquiryQuery query) =>
        (query.AsOfDate ?? DateTime.Today).Date;

    /// <summary>
    /// Restricts a slice to one ageing bucket. Because <c>AgeDays = asOf − SoDate</c>, a bucket is
    /// exactly a range over the persisted <c>SoDate</c> — which keeps the filter (and therefore paging)
    /// server-side.
    /// </summary>
    private static IQueryable<T> ApplyAgeBucketRange<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTime>> soDate,
        string? bucket,
        DateTime asOf,
        IReadOnlyList<string> knownBuckets)
    {
        var label = Normalize(bucket);
        if (label is null || !knownBuckets.Contains(label, StringComparer.Ordinal))
        {
            return source;
        }

        return label switch
        {
            SaMonitorBuckets.Days0To30 => source.Where(
                BuildCompare(soDate, ExpressionType.GreaterThanOrEqual, asOf.AddDays(-30))),

            SaMonitorBuckets.Days31To60 => source
                .Where(BuildCompare(soDate, ExpressionType.GreaterThanOrEqual, asOf.AddDays(-60)))
                .Where(BuildCompare(soDate, ExpressionType.LessThan, asOf.AddDays(-30))),

            SaMonitorBuckets.Days61To90 => source
                .Where(BuildCompare(soDate, ExpressionType.GreaterThanOrEqual, asOf.AddDays(-90)))
                .Where(BuildCompare(soDate, ExpressionType.LessThan, asOf.AddDays(-60))),

            SaMonitorBuckets.Over90 => source.Where(
                BuildCompare(soDate, ExpressionType.LessThan, asOf.AddDays(-90))),

            _ => source
        };
    }

    /// <summary>
    /// <c>DeliveryDate</c> strictly before <paramref name="asOf"/> — i.e. the expected delivery lapsed.
    /// A delivery due <b>today</b> is not overdue. Built as an expression tree with the selector's
    /// nullable type preserved, because <see cref="Expression.Constant(object, Type)"/> cannot hold a
    /// <c>DateTime</c> under a <c>DateTime?</c> type.
    /// </summary>
    private static Expression<Func<T, bool>> BuildDeliveryOverdue<T>(
        Expression<Func<T, DateTime?>> selector,
        DateTime asOf)
    {
        var parameter = selector.Parameters[0];
        var value = Expression.Convert(
            Expression.Constant(asOf, typeof(DateTime)),
            typeof(DateTime?));

        var body = Expression.MakeBinary(ExpressionType.LessThan, selector.Body, value);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }
}
