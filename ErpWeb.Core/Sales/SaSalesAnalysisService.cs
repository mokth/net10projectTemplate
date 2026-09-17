using System.Linq.Expressions;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales-analysis Phase 1 implementation.
///
/// Locked rules this class must keep (see the Phase 1 plan):
///   R1/M1  date range is the repository half-open pattern; target months are never prorated.
///   M2     a month with no target row is 0; target &lt;= 0 means attainment is N/A, not 0% or Infinity.
///   R2     targets are company-wide and are NOT affected by the branch filter.
///   R3     CN/DN are stored positive and are netted exactly once: Invoice + DN − CN.
///   R4/M4  Won is Status == CLOSED AND ClosedReason == CONVERTED (what the converter writes).
///   M5     quotation amounts are the header TotAmnt; lines are never re-totalled here.
///   R5     Source joins the LIVE customer master — current attribution, not a snapshot.
///   R6     amounts are named after their columns, never "revenue".
///   R7     company comes from the authenticated scope; the client cannot override it.
///   R9     the UI and its CSV export both call these methods, so they cannot disagree.
///
/// Nothing in this service writes to the transactional sales tables.
/// </summary>
public sealed class SaSalesAnalysisService : ISaSalesAnalysisService
{
    private static readonly string[] OpenStatuses =
        [SaQtStatuses.New, SaQtStatuses.Sent, SaQtStatuses.Accepted];

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public SaSalesAnalysisService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    // ============================ Sales Summary ============================

    public async Task<IvMasterOperationResult<SaSalesSummaryResult>> GetSalesSummaryAsync(
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(MenuCodes.SalesAnalysisSummary, cancellationToken);
        if (gate.Error is not null)
        {
            return IvMasterOperationResult<SaSalesSummaryResult>.Fail(gate.Error.Value, gate.Message!);
        }

        var range = ResolveRange(query);
        if (range.Error is not null)
        {
            return IvMasterOperationResult<SaSalesSummaryResult>.Fail(IvMasterErrorCode.Validation, range.Error);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var totals = await BuildPeriodTotalsAsync(db, gate.CompanyCode!, range, query, cancellationToken);
        var rows = await BuildSummaryRowsAsync(db, gate.CompanyCode!, range, query, cancellationToken);

        var grandTotal = rows.Sum(x => x.InvoiceTotal);
        if (grandTotal > 0m)
        {
            rows = rows
                .Select(x => new SaSalesSummaryRow
                {
                    Key = x.Key,
                    Description = x.Description,
                    InvoiceCount = x.InvoiceCount,
                    InvoiceTotal = x.InvoiceTotal,
                    GrossAmount = x.GrossAmount,
                    TaxAmount = x.TaxAmount,
                    SharePercent = Math.Round(x.InvoiceTotal / grandTotal * 100m, 2, MidpointRounding.AwayFromZero)
                })
                .ToList();
        }

        return IvMasterOperationResult<SaSalesSummaryResult>.Ok(new SaSalesSummaryResult
        {
            Rows = rows,
            Totals = totals
        });
    }

    private async Task<List<SaSalesSummaryRow>> BuildSummaryRowsAsync(
        AppDbContext db,
        string company,
        AnalysisRange range,
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        var invoices = ApplyDocumentFilters(PostedInvoices(db, company, range), query);

        // Source is the one dimension that is not an invoice snapshot: it joins the LIVE customer
        // master, so sales-by-source is current attribution (R5). The join is also the filter.
        if (!string.IsNullOrWhiteSpace(query.CustSource))
        {
            var source = query.CustSource.Trim();
            invoices =
                from i in invoices
                join c in db.SaCusts.AsNoTracking()
                    on new { i.CompanyCode, i.CustCode } equals new { c.CompanyCode, c.CustCode }
                where c.CustSource == source
                select i;
        }

        var raw = query.Dimension == SaSalesSummaryDimension.Source
            ? await GroupBySourceAsync(db, invoices, cancellationToken)
            : await GroupAsync(invoices, KeySelector(query.Dimension), cancellationToken);

        var keys = raw
            .Select(x => x.Key ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var descriptions = await ResolveDescriptionsAsync(db, company, query.Dimension, keys, cancellationToken);

        return raw
            .Select(x => new SaSalesSummaryRow
            {
                Key = string.IsNullOrWhiteSpace(x.Key) ? BlankLabel : x.Key!,
                Description = x.Key is not null && descriptions.TryGetValue(x.Key, out var desc) ? desc : null,
                InvoiceCount = x.Count,
                InvoiceTotal = Money(x.InvoiceTotal),
                GrossAmount = Money(x.GrossAmount),
                TaxAmount = Money(x.TaxAmount),
                SharePercent = 0m
            })
            .OrderByDescending(x => x.InvoiceTotal)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<SaSalesPeriodTotals> BuildPeriodTotalsAsync(
        AppDbContext db,
        string company,
        AnalysisRange range,
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken)
    {
        var invoices = ApplyDocumentFilters(PostedInvoices(db, company, range), query);

        var invoiceCount = await invoices.CountAsync(cancellationToken);
        var invoiceTotal = await invoices.SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;
        var gross = await invoices.SumAsync(x => (decimal?)x.GrossAmnt, cancellationToken) ?? 0m;
        var tax = await invoices.SumAsync(x => (decimal?)x.Taxes, cancellationToken) ?? 0m;

        // CN/DN keep their stored POSITIVE totals. Apply only the filters SaCdn actually carries:
        // branch / customer / salesman. Dimensional filters cannot apply (no snapshot on SaCdn).
        var cdns = db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.Status == SaCdnStatuses.Posted
                && x.DocDate >= range.From
                && x.DocDate < range.ToExclusive);

        if (!string.IsNullOrWhiteSpace(query.BranchCode))
        {
            var branch = query.BranchCode.Trim();
            cdns = cdns.Where(x => x.BranchCode == branch);
        }

        if (!string.IsNullOrWhiteSpace(query.CustCode))
        {
            var cust = query.CustCode.Trim();
            cdns = cdns.Where(x => x.CustCode == cust);
        }

        if (!string.IsNullOrWhiteSpace(query.SalesmanCode))
        {
            var rep = query.SalesmanCode.Trim();
            cdns = cdns.Where(x => x.SalesRep == rep);
        }

        var creditNotes = cdns.Where(x => x.Type == SaCdnTypes.CreditNote);
        var debitNotes = cdns.Where(x => x.Type == SaCdnTypes.DebitNote);

        var cnCount = await creditNotes.CountAsync(cancellationToken);
        var cnTotal = await creditNotes.SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;
        var dnCount = await debitNotes.CountAsync(cancellationToken);
        var dnTotal = await debitNotes.SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        var invoice = Money(invoiceTotal);
        var credit = Money(cnTotal);
        var debit = Money(dnTotal);

        return new SaSalesPeriodTotals
        {
            InvoiceCount = invoiceCount,
            InvoiceTotal = invoice,
            GrossAmount = Money(gross),
            TaxAmount = Money(tax),
            CreditNoteCount = cnCount,
            CreditNoteTotal = credit,
            DebitNoteCount = dnCount,
            DebitNoteTotal = debit,
            NetSalesAmount = Money(invoice + debit - credit)
        };
    }

    // ======================= Sales Rep Attainment =======================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaSalesRepAttainmentRow>>> GetSalesRepAttainmentAsync(
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(MenuCodes.SalesAnalysisAttainment, cancellationToken);
        if (gate.Error is not null)
        {
            return IvMasterOperationResult<IReadOnlyList<SaSalesRepAttainmentRow>>.Fail(gate.Error.Value, gate.Message!);
        }

        var range = ResolveRange(query);
        if (range.Error is not null)
        {
            return IvMasterOperationResult<IReadOnlyList<SaSalesRepAttainmentRow>>.Fail(
                IvMasterErrorCode.Validation, range.Error);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // R2: attainment is company-wide. The optional branch filter is intentionally NOT applied here,
        // so a branch-restricted view can never silently change a rep's target or attainment.
        var invoices = PostedInvoices(db, gate.CompanyCode!, range);
        if (!string.IsNullOrWhiteSpace(query.SalesmanCode))
        {
            var rep = query.SalesmanCode.Trim();
            invoices = invoices.Where(x => x.SalesmanCode == rep);
        }

        var actualRaw = await GroupAsync(invoices, x => x.SalesmanCode, cancellationToken);
        var actuals = actualRaw
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key!.Trim(), x => Money(x.InvoiceTotal), StringComparer.OrdinalIgnoreCase);

        var months = IntersectingMonths(range);
        var years = months.Select(m => m.Year).Distinct().ToList();
        var monthSet = months.ToHashSet();

        var targetRows = await db.SaSalesRepTargets.AsNoTracking()
            .Where(x => x.CompanyCode == gate.CompanyCode && years.Contains(x.Year))
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.SalesmanCode))
        {
            var rep = query.SalesmanCode.Trim();
            targetRows = targetRows
                .Where(x => string.Equals(x.SrepCode, rep, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var targetRowsInRange = targetRows.Where(x => monthSet.Contains((x.Year, x.Month))).ToList();

        var targets = targetRowsInRange
            .GroupBy(x => x.SrepCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (Amount: Money(g.Sum(x => x.TargetAmount)), Months: g.Select(x => x.Month).Distinct().Count()),
                StringComparer.OrdinalIgnoreCase);

        // M2: appear when there is sales OR a target; a month with no row contributes nothing to the sum.
        var codes = actuals.Keys
            .Union(targets.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = await db.SaSalesReps.AsNoTracking()
            .Where(x => x.CompanyCode == gate.CompanyCode && codes.Contains(x.SrepCode))
            .Select(x => new { x.SrepCode, x.SrepName })
            .ToListAsync(cancellationToken);
        var nameByCode = names.ToDictionary(x => x.SrepCode, x => x.SrepName, StringComparer.OrdinalIgnoreCase);

        var result = codes.Select(code =>
        {
            var actual = actuals.TryGetValue(code, out var a) ? a : 0m;
            var target = targets.TryGetValue(code, out var t) ? t.Amount : 0m;
            var monthsWithTarget = targets.TryGetValue(code, out var tm) ? tm.Months : 0;

            return new SaSalesRepAttainmentRow
            {
                Code = code,
                Name = nameByCode.TryGetValue(code, out var name) ? name : null,
                ActualAmount = actual,
                TargetAmount = target,
                // M2: a zero (or missing) target is N/A — never Infinity, NaN or a misleading 0%.
                AttainmentPercent = target > 0m
                    ? Math.Round(actual / target * 100m, 2, MidpointRounding.AwayFromZero)
                    : null,
                MonthsWithTarget = monthsWithTarget
            };
        }).ToList();

        return IvMasterOperationResult<IReadOnlyList<SaSalesRepAttainmentRow>>.Ok(result);
    }

    // ======================= Quotation Conversion =======================

    public async Task<IvMasterOperationResult<SaQtConversionResult>> GetQtConversionAsync(
        SaSalesAnalysisQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(MenuCodes.SalesAnalysisQtConversion, cancellationToken);
        if (gate.Error is not null)
        {
            return IvMasterOperationResult<SaQtConversionResult>.Fail(gate.Error.Value, gate.Message!);
        }

        var range = ResolveRange(query);
        if (range.Error is not null)
        {
            return IvMasterOperationResult<SaQtConversionResult>.Fail(IvMasterErrorCode.Validation, range.Error);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // R4: CURRENT revisions only — superseded revisions must never be counted twice for one QtNo.
        var qts = db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == gate.CompanyCode
                && x.IsCurrent
                && x.QtDate >= range.From
                && x.QtDate < range.ToExclusive);

        if (!string.IsNullOrWhiteSpace(query.SalesmanCode))
        {
            var rep = query.SalesmanCode.Trim();
            qts = qts.Where(x => x.SalesRep == rep);
        }

        var total = await BucketAsync(qts, cancellationToken);
        var open = await BucketAsync(qts.Where(x => OpenStatuses.Contains(x.Status)), cancellationToken);
        var won = await BucketAsync(
            qts.Where(x => x.Status == SaQtStatuses.Closed && x.ClosedReason == SaQtClosedReasons.Converted),
            cancellationToken);
        var lost = await BucketAsync(qts.Where(x => x.Status == SaQtStatuses.Lost), cancellationToken);
        var expired = await BucketAsync(qts.Where(x => x.Status == SaQtStatuses.Expired), cancellationToken);
        var cancelled = await BucketAsync(qts.Where(x => x.Status == SaQtStatuses.Cancelled), cancellationToken);

        var lostRows = await qts
            .Where(x => x.Status == SaQtStatuses.Lost)
            .Select(x => new { x.LostReason, x.TotAmnt })
            .ToListAsync(cancellationToken);

        var lostReasons = lostRows
            .GroupBy(x => string.IsNullOrWhiteSpace(x.LostReason) ? BlankLabel : x.LostReason!.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(g => new SaQtLostReasonRow
            {
                Reason = g.Key,
                Count = g.Count(),
                Amount = Money(g.Sum(x => x.TotAmnt))
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<SaQtConversionBySalesRepRow> byRep = query.GroupQtBySalesRep
            ? await BuildQtByRepAsync(qts, cancellationToken)
            : [];

        return IvMasterOperationResult<SaQtConversionResult>.Ok(new SaQtConversionResult
        {
            Total = total,
            Open = open,
            Won = won,
            Lost = lost,
            Expired = expired,
            Cancelled = cancelled,
            WinRatePercent = WinRate(won.Count, lost.Count),
            LostReasons = lostReasons,
            BySalesRep = byRep
        });
    }

    private static async Task<IReadOnlyList<SaQtConversionBySalesRepRow>> BuildQtByRepAsync(
        IQueryable<SaQt> qts,
        CancellationToken cancellationToken)
    {
        var rows = await qts
            .GroupBy(x => x.SalesRep)
            .Select(g => new
            {
                SalesRep = g.Key,
                TotalCount = g.Count(),
                TotalAmount = g.Sum(x => x.TotAmnt),
                OpenCount = g.Count(x => x.Status == SaQtStatuses.New
                    || x.Status == SaQtStatuses.Sent
                    || x.Status == SaQtStatuses.Accepted),
                OpenAmount = g.Sum(x => x.Status == SaQtStatuses.New
                    || x.Status == SaQtStatuses.Sent
                    || x.Status == SaQtStatuses.Accepted ? x.TotAmnt : 0m),
                WonCount = g.Count(x => x.Status == SaQtStatuses.Closed
                    && x.ClosedReason == SaQtClosedReasons.Converted),
                WonAmount = g.Sum(x => x.Status == SaQtStatuses.Closed
                    && x.ClosedReason == SaQtClosedReasons.Converted ? x.TotAmnt : 0m),
                LostCount = g.Count(x => x.Status == SaQtStatuses.Lost),
                LostAmount = g.Sum(x => x.Status == SaQtStatuses.Lost ? x.TotAmnt : 0m),
                ExpiredCount = g.Count(x => x.Status == SaQtStatuses.Expired),
                ExpiredAmount = g.Sum(x => x.Status == SaQtStatuses.Expired ? x.TotAmnt : 0m),
                CancelledCount = g.Count(x => x.Status == SaQtStatuses.Cancelled),
                CancelledAmount = g.Sum(x => x.Status == SaQtStatuses.Cancelled ? x.TotAmnt : 0m)
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new SaQtConversionBySalesRepRow
            {
                SalesRep = string.IsNullOrWhiteSpace(x.SalesRep) ? BlankLabel : x.SalesRep!.Trim(),
                Total = new SaQtConversionBucket { Count = x.TotalCount, Amount = Money(x.TotalAmount) },
                Open = new SaQtConversionBucket { Count = x.OpenCount, Amount = Money(x.OpenAmount) },
                Won = new SaQtConversionBucket { Count = x.WonCount, Amount = Money(x.WonAmount) },
                Lost = new SaQtConversionBucket { Count = x.LostCount, Amount = Money(x.LostAmount) },
                Expired = new SaQtConversionBucket { Count = x.ExpiredCount, Amount = Money(x.ExpiredAmount) },
                Cancelled = new SaQtConversionBucket { Count = x.CancelledCount, Amount = Money(x.CancelledAmount) },
                WinRatePercent = WinRate(x.WonCount, x.LostCount)
            })
            .OrderByDescending(x => x.Won.Amount)
            .ThenBy(x => x.SalesRep, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<SaQtConversionBucket> BucketAsync(
        IQueryable<SaQt> qts,
        CancellationToken cancellationToken) =>
        new()
        {
            Count = await qts.CountAsync(cancellationToken),
            Amount = Money(await qts.SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m)
        };

    /// <summary>Win Rate = Won / (Won + Lost). Open, Expired and Cancelled are shown but excluded.</summary>
    private static decimal? WinRate(int wonCount, int lostCount)
    {
        var denominator = wonCount + lostCount;
        return denominator > 0
            ? Math.Round((decimal)wonCount / denominator * 100m, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    // ============================ Shared helpers ============================

    private async Task<UserGate> GateAsync(string menuCode, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return UserGate.Fail(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _accessRights.CanAsync(menuCode, PermissionCodes.Access, cancellationToken))
        {
            return UserGate.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return UserGate.Ok(scope.CompanyCode);
    }

    private static IQueryable<SaInvoice> PostedInvoices(AppDbContext db, string company, AnalysisRange range) =>
        db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.Status == SaInvoiceStatuses.Posted
                && x.InvDate >= range.From
                && x.InvDate < range.ToExclusive);

    /// <summary>Filters every document shares: branch / customer / salesman and the invoice snapshots.</summary>
    private static IQueryable<SaInvoice> ApplyDocumentFilters(IQueryable<SaInvoice> invoices, SaSalesAnalysisQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.BranchCode))
        {
            var branch = query.BranchCode.Trim();
            invoices = invoices.Where(x => x.BranchCode == branch);
        }

        if (!string.IsNullOrWhiteSpace(query.CustCode))
        {
            var cust = query.CustCode.Trim();
            invoices = invoices.Where(x => x.CustCode == cust);
        }

        if (!string.IsNullOrWhiteSpace(query.SalesmanCode))
        {
            var rep = query.SalesmanCode.Trim();
            invoices = invoices.Where(x => x.SalesmanCode == rep);
        }

        if (!string.IsNullOrWhiteSpace(query.AreaCode))
        {
            var area = query.AreaCode.Trim();
            invoices = invoices.Where(x => x.AreaCode == area);
        }

        if (!string.IsNullOrWhiteSpace(query.IndustryCode))
        {
            var industry = query.IndustryCode.Trim();
            invoices = invoices.Where(x => x.IndustryCode == industry);
        }

        if (!string.IsNullOrWhiteSpace(query.ChannelCode))
        {
            var channel = query.ChannelCode.Trim();
            invoices = invoices.Where(x => x.ChannelCode == channel);
        }

        if (!string.IsNullOrWhiteSpace(query.CustType))
        {
            var type = query.CustType.Trim();
            invoices = invoices.Where(x => x.CustType == type);
        }

        if (!string.IsNullOrWhiteSpace(query.CustGroupCode))
        {
            var group = query.CustGroupCode.Trim();
            invoices = invoices.Where(x => x.CustGroupCode == group);
        }

        return invoices;
    }

    private static Expression<Func<SaInvoice, string?>> KeySelector(SaSalesSummaryDimension dimension) =>
        dimension switch
        {
            SaSalesSummaryDimension.Customer => x => x.CustCode,
            SaSalesSummaryDimension.Area => x => x.AreaCode,
            SaSalesSummaryDimension.Industry => x => x.IndustryCode,
            SaSalesSummaryDimension.Channel => x => x.ChannelCode,
            SaSalesSummaryDimension.CustType => x => x.CustType,
            SaSalesSummaryDimension.CustGroup => x => x.CustGroupCode,
            // Source is not an invoice snapshot, so it cannot be grouped from SaInvoice alone.
            // BuildSummaryRowsAsync routes it through GroupBySourceAsync instead.
            SaSalesSummaryDimension.Source => throw new InvalidOperationException(
                "The Source dimension is grouped from the live customer master, not from SaInvoice."),
            _ => x => x.SalesmanCode
        };

    /// <summary>
    /// R5: groups POSTED invoices by the LIVE <c>SaCust.CustSource</c>, so the label must say
    /// "current". A customer with no source (or a deleted customer) groups under the blank label
    /// rather than vanishing from the grid.
    /// </summary>
    private static async Task<List<RawGroup>> GroupBySourceAsync(
        AppDbContext db,
        IQueryable<SaInvoice> invoices,
        CancellationToken cancellationToken)
    {
        var joined =
            from i in invoices
            join c in db.SaCusts.AsNoTracking()
                on new { i.CompanyCode, i.CustCode } equals new { c.CompanyCode, c.CustCode } into gj
            from c in gj.DefaultIfEmpty()
            select new { Invoice = i, Source = c != null ? c.CustSource : null };

        return await joined
            .GroupBy(x => x.Source)
            .Select(g => new RawGroup
            {
                Key = g.Key,
                Count = g.Count(),
                InvoiceTotal = g.Sum(x => x.Invoice.TotAmnt),
                GrossAmount = g.Sum(x => x.Invoice.GrossAmnt),
                TaxAmount = g.Sum(x => x.Invoice.Taxes)
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Groups POSTED invoices by one key. The key selector is an expression so EF can translate the
    /// grouping to SQL instead of materialising the invoice rows.
    /// </summary>
    private static async Task<List<RawGroup>> GroupAsync(
        IQueryable<SaInvoice> invoices,
        Expression<Func<SaInvoice, string?>> keySelector,
        CancellationToken cancellationToken)
    {
        var grouped = await invoices
            .GroupBy(keySelector)
            .Select(g => new RawGroup
            {
                Key = g.Key,
                Count = g.Count(),
                InvoiceTotal = g.Sum(x => x.TotAmnt),
                GrossAmount = g.Sum(x => x.GrossAmnt),
                TaxAmount = g.Sum(x => x.Taxes)
            })
            .ToListAsync(cancellationToken);

        return grouped;
    }

    /// <summary>
    /// Master descriptions for the grouping keys, so the grid shows a name instead of a bare code.
    /// A dimension with no name table (Source falls back to the SOURCE code list) simply returns none.
    /// </summary>
    private static async Task<Dictionary<string, string>> ResolveDescriptionsAsync(
        AppDbContext db,
        string company,
        SaSalesSummaryDimension dimension,
        List<string> keys,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return result;
        }

        switch (dimension)
        {
            case SaSalesSummaryDimension.Salesman:
                foreach (var row in await db.SaSalesReps.AsNoTracking()
                    .Where(x => x.CompanyCode == company && keys.Contains(x.SrepCode))
                    .Select(x => new { x.SrepCode, x.SrepName })
                    .ToListAsync(cancellationToken))
                {
                    Add(result, row.SrepCode, row.SrepName);
                }
                break;

            case SaSalesSummaryDimension.Customer:
                foreach (var row in await db.SaCusts.AsNoTracking()
                    .Where(x => x.CompanyCode == company && keys.Contains(x.CustCode))
                    .Select(x => new { x.CustCode, x.CustName })
                    .ToListAsync(cancellationToken))
                {
                    Add(result, row.CustCode, row.CustName);
                }
                break;

            case SaSalesSummaryDimension.Area:
                foreach (var row in await db.IvAreaCodes.AsNoTracking()
                    .Where(x => x.CompanyCode == company && keys.Contains(x.AreaCode))
                    .Select(x => new { x.AreaCode, x.AreaDesc })
                    .ToListAsync(cancellationToken))
                {
                    Add(result, row.AreaCode, row.AreaDesc);
                }
                break;

            case SaSalesSummaryDimension.CustType:
                foreach (var row in await db.SaCustTypes.AsNoTracking()
                    .Where(x => x.CompanyCode == company && keys.Contains(x.CustTypeCode))
                    .Select(x => new { Code = x.CustTypeCode, x.CustTypeDesc })
                    .ToListAsync(cancellationToken))
                {
                    Add(result, row.Code, row.CustTypeDesc);
                }
                break;

            case SaSalesSummaryDimension.CustGroup:
                foreach (var row in await db.SaCustGroups.AsNoTracking()
                    .Where(x => x.CompanyCode == company && keys.Contains(x.CustGroupCode))
                    .Select(x => new { Code = x.CustGroupCode, x.CustGroupDesc })
                    .ToListAsync(cancellationToken))
                {
                    Add(result, row.Code, row.CustGroupDesc);
                }
                break;

            case SaSalesSummaryDimension.Industry:
            case SaSalesSummaryDimension.Channel:
            case SaSalesSummaryDimension.Source:
                var codeType = dimension switch
                {
                    SaSalesSummaryDimension.Industry => IvMsCodeTypes.Industry,
                    SaSalesSummaryDimension.Channel => IvMsCodeTypes.Channel,
                    _ => IvMsCodeTypes.Source
                };
                foreach (var row in await db.IvMsCodes.AsNoTracking()
                    .Where(x => x.CodeType == codeType && keys.Contains(x.Code))
                    .Select(x => new { x.Code, x.Name })
                    .ToListAsync(cancellationToken))
                {
                    Add(result, row.Code, row.Name);
                }
                break;
        }

        return result;
    }

    private static void Add(Dictionary<string, string> target, string? code, string? description)
    {
        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(description))
        {
            target[code.Trim()] = description.Trim();
        }
    }

    private static AnalysisRange ResolveRange(SaSalesAnalysisQuery? query)
    {
        if (query?.DateFrom is null || query.DateTo is null)
        {
            return AnalysisRange.Invalid("A from date and a to date are required.");
        }

        var from = query.DateFrom.Value.Date;
        var toInclusive = query.DateTo.Value.Date;
        if (toInclusive < from)
        {
            return AnalysisRange.Invalid("The to date cannot be before the from date.");
        }

        return new AnalysisRange(from, toInclusive, toInclusive.AddDays(1), null);
    }

    /// <summary>
    /// Every calendar month the inclusive range touches (R1). The full monthly target of each of these
    /// months is consumed — a range that ends mid-month still uses that month's whole target.
    /// </summary>
    private static List<(int Year, int Month)> IntersectingMonths(AnalysisRange range)
    {
        var months = new List<(int Year, int Month)>();
        var cursor = new DateTime(range.From.Year, range.From.Month, 1);
        var last = new DateTime(range.ToInclusive.Year, range.ToInclusive.Month, 1);
        while (cursor <= last)
        {
            months.Add((cursor.Year, cursor.Month));
            cursor = cursor.AddMonths(1);
        }

        return months;
    }

    private static decimal Money(decimal value) => SaInvoiceCalc.Money(value);

    /// <summary>Placeholder shown for a null / blank grouping key, so those rows are visible not hidden.</summary>
    private const string BlankLabel = "(blank)";

    private sealed class RawGroup
    {
        public string? Key { get; set; }
        public int Count { get; set; }
        public decimal InvoiceTotal { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal TaxAmount { get; set; }
    }

    private readonly record struct AnalysisRange(
        DateTime From,
        DateTime ToInclusive,
        DateTime ToExclusive,
        string? Error)
    {
        public static AnalysisRange Invalid(string error) => new(default, default, default, error);
    }

    private readonly record struct UserGate(string? CompanyCode, IvMasterErrorCode? Error, string? Message)
    {
        public static UserGate Ok(string companyCode) => new(companyCode, null, null);

        public static UserGate Fail(IvMasterErrorCode error, string message) => new(null, error, message);
    }
}
