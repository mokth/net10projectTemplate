using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales Dashboard Phase 3 implementation (plan-salesReportsAndInquiries.prompt.md).
///
/// Locked rules this class must keep:
///   • Tenancy = the authenticated company scope; every aggregate is company-filtered and nothing is
///     ever accepted from the client (R7).
///   • KPI chips follow the locked Dashboard KPI definitions — sales today/month/year are POSTED
///     invoices only; Open QT is current + {NEW, SENT, ACCEPTED}; Open SO is current + not CLOSED;
///     pending delivery is <c>SUM(BalanceQty)</c> over open SOs and never adds open-DO quantity;
///     CN/DN are POSTED, stored positive, grouped by <c>SaCdnTypes</c> (current month).
///   • One bounded query per aggregate — no N+1, no per-row or per-customer/per-item database calls.
///   • Chart payloads reuse <see cref="ISaSalesAnalysisService"/> result shapes so the dashboard and
///     the analysis screens cannot disagree.
/// </summary>
public sealed class SaSalesDashboardService : ISaSalesDashboardService
{
    private static readonly string[] OpenQtStatuses = [SaQtStatuses.New, SaQtStatuses.Sent, SaQtStatuses.Accepted];

    private const int TopItemsCount = 10;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly ISaSalesAnalysisService _analysis;

    public SaSalesDashboardService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        ISaSalesAnalysisService analysis)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _analysis = analysis;
    }

    public async Task<IvMasterOperationResult<SaDashboardResult>> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return IvMasterOperationResult<SaDashboardResult>.Fail(
                IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesDashboard, PermissionCodes.Access, cancellationToken))
        {
            return IvMasterOperationResult<SaDashboardResult>.Fail(
                IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var company = scope.CompanyCode;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var today = _dates.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEndExclusive = monthStart.AddMonths(1);
        var yearStart = new DateTime(today.Year, 1, 1);
        var yearEndExclusive = yearStart.AddYears(1);

        // ── Sales chips: POSTED invoices only (R1), three half-open date windows. ──
        var postedInvoices = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.Status == SaInvoiceStatuses.Posted);

        var salesToday = await postedInvoices
            .Where(x => x.InvDate >= today && x.InvDate < today.AddDays(1))
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;
        var salesMonth = await postedInvoices
            .Where(x => x.InvDate >= monthStart && x.InvDate < monthEndExclusive)
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;
        var salesYear = await postedInvoices
            .Where(x => x.InvDate >= yearStart && x.InvDate < yearEndExclusive)
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        // ── Open QT chip: current revisions in an open status. ──
        var openQts = db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsCurrent && OpenQtStatuses.Contains(x.Status));
        var openQtCount = await openQts.CountAsync(cancellationToken);
        var openQtValue = await openQts.SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        // ── Open SO chips: current, not CLOSED. ──
        var openSos = db.SaSos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsCurrent && x.Status != SaSoStatuses.Closed);
        var openSoCount = await openSos.CountAsync(cancellationToken);
        var openSoValue = await openSos.SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        // Outstanding value = original − invoiced value via SaInvoiceDetail.SoNo links (never counted
        // twice with a DO: the line's SoNo is the single authority for what has been billed).
        var openSoNos = await openSos.Select(x => x.SoNo).ToListAsync(cancellationToken);
        decimal invoicedValue = 0m;
        if (openSoNos.Count > 0)
        {
            invoicedValue = await db.SaInvoiceDetails.AsNoTracking()
                .Where(x => x.CompanyCode == company && openSoNos.Contains(x.SoNo))
                .SumAsync(x => (decimal?)x.NetAmount, cancellationToken) ?? 0m;
        }

        var outstandingValue = openSoValue - invoicedValue;

        // ── Pending delivery: SUM(BalanceQty) over open SOs (the persisted outstanding quantity). ──
        var pendingDeliveryQty = await (
            from so in openSos
            from line in so.Details
            select line.BalanceQty
        ).SumAsync(cancellationToken);

        // ── Open DO quantity (separate chip): delivered but not fully billed and not written off. ──
        var openDoQty = await (
            from delivery in db.SaDos.AsNoTracking().Where(x =>
                x.CompanyCode == company
                && x.Status == SaDoStatuses.Posted
                && x.BillingStatus != SaDualStatuses.Full
                && x.BillingStatus != SaDualStatuses.WrittenOff)
            from line in delivery.Details
            select line.Qty
        ).SumAsync(cancellationToken);

        // ── CN/DN chips: POSTED, stored positive, current month. ──
        var postedCdns = db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.Status == SaCdnStatuses.Posted
                && x.DocDate >= monthStart
                && x.DocDate < monthEndExclusive);

        var creditNoteTotal = await postedCdns
            .Where(x => x.Type == SaCdnTypes.CreditNote)
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;
        var debitNoteTotal = await postedCdns
            .Where(x => x.Type == SaCdnTypes.DebitNote)
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        // ── Chart payloads: reuse the analysis service (same result shapes as the analysis screens).
        // A chart its caller is not authorised to see returns empty rather than failing the dashboard. ──
        var result = new SaDashboardResult
        {
            SalesToday = Money(salesToday),
            SalesMonth = Money(salesMonth),
            SalesYear = Money(salesYear),
            OpenQtCount = openQtCount,
            OpenQtValue = Money(openQtValue),
            OpenSoCount = openSoCount,
            OpenSoValue = Money(openSoValue),
            OpenSoOutstandingValue = Money(outstandingValue),
            PendingDeliveryQty = pendingDeliveryQty,
            OpenDoQty = openDoQty,
            CreditNoteTotal = Money(creditNoteTotal),
            DebitNoteTotal = Money(debitNoteTotal)
        };

        await FillChartsAsync(result, monthStart, today, cancellationToken);

        return IvMasterOperationResult<SaDashboardResult>.Ok(result);
    }

    private async Task FillChartsAsync(
        SaDashboardResult result,
        DateTime monthStart,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var byCustomer = await _analysis.GetSalesSummaryAsync(
            new SaSalesAnalysisQuery
            {
                DateFrom = monthStart,
                DateTo = today,
                Dimension = SaSalesSummaryDimension.Customer
            },
            cancellationToken);
        if (byCustomer.Succeeded)
        {
            result.ByCustomer = byCustomer.Data?.Rows ?? [];
        }

        var bySalesperson = await _analysis.GetSalesSummaryAsync(
            new SaSalesAnalysisQuery
            {
                DateFrom = monthStart,
                DateTo = today,
                Dimension = SaSalesSummaryDimension.Salesman
            },
            cancellationToken);
        if (bySalesperson.Succeeded)
        {
            result.BySalesperson = bySalesperson.Data?.Rows ?? [];
        }

        var detailQuery = new SaSalesAnalysisQuery { DateFrom = monthStart, DateTo = today };

        var byCategory = await _analysis.GetSalesDetailAsync(
            MenuCodes.SalesByCategory, detailQuery, SaSalesDetailDimension.Category, cancellationToken);
        if (byCategory.Succeeded)
        {
            result.ByCategory = byCategory.Data ?? [];
        }

        var byItem = await _analysis.GetSalesDetailAsync(
            MenuCodes.SalesByItem, detailQuery, SaSalesDetailDimension.Item, cancellationToken);
        if (byItem.Succeeded)
        {
            result.TopItems = (byItem.Data ?? []).Take(TopItemsCount).ToList();
        }
    }

    private static decimal Money(decimal value) => SaInvoiceCalc.Money(value);
}
