using System.Linq.Expressions;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales Inquiry Phase 1 implementation — read-only operational grids over the existing sales
/// entities (plan-salesReportsAndInquiries.prompt.md). Reuses the tenant + ACCESS gating order of the
/// inventory inquiry suite and the POSTED-only / CN-DN netting conventions of
/// <see cref="SaSalesAnalysisService"/>.
///
/// <para>
/// Locked rules this class must keep:
///   • Every method takes the caller's <c>menuCode</c> and is gated ACCESS-only on that menu.
///   • Tenant scope resolves FIRST (fail closed), then ACCESS — never the reverse.
///   • CN/DN are stored positive and netted exactly once: Net = Invoice + DN − CN.
///   • Header <c>TotAmnt</c> is authoritative; lines are never re-totalled here.
///   • <c>BalanceQty</c> / <c>ShippedQty</c> / <c>DeliveredQty</c> / <c>InvoicedQty</c> /
///     <c>WrittenOffQty</c> and the header <c>FulfillmentStatus</c> / <c>BillingStatus</c> are
///     persisted rollups — shown as-is, never recomputed.
///   • e-Invoice current status = the highest <c>ID</c> <c>EInvDocSubmission</c> row for
///     <c>(companyID, documentType, documentNo)</c>, normalised via <c>EInvoiceStatuses.Normalize</c>.
/// </para>
/// </summary>
public sealed partial class SaSalesInquiryService : ISaSalesInquiryService
{
    /// <summary>Menus this service may serve. A page cannot borrow another screen's rights.</summary>
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.SalesCustomerTransaction,
        MenuCodes.SalesQtStatus,
        MenuCodes.SalesSoOutstanding,
        MenuCodes.SalesDoStatus,
        MenuCodes.SalesInvVsDoc,
        MenuCodes.SalesCdnInquiry,
        MenuCodes.SalesEInvoiceInquiry,

        // Sales Monitor (plan-salesDecisionSupport.prompt.md Phase A). Each screen carries its own
        // grant, so holding SA_SO_AGEING must not open SA_EINV_ACTION.
        MenuCodes.SalesSoAgeing,
        MenuCodes.SalesDoNotFullyInvoiced,
        MenuCodes.SalesQtExpiry,
        MenuCodes.SalesEInvoiceAction
    };

    private const int DefaultPageSize = 50;
    private const int MaxExportRows = 50_000;

    private static readonly string[] EInvoiceDocTypeFilter = [EInvoiceDocumentTypes.Invoice, EInvoiceDocumentTypes.CreditNote, EInvoiceDocumentTypes.DebitNote];

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public SaSalesInquiryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    // ============================ Customer Transaction ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaCustomerTransactionRow>>> GetCustomerTransactionsAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaCustomerTransactionRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var union = CustomerTransactionUnion(db, company, branch, prepared);

        var total = await union.CountAsync(cancellationToken);
        var rows = await union
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<SaInquiryPage<SaCustomerTransactionRow>>.Ok(
            new SaInquiryPage<SaCustomerTransactionRow> { Rows = rows, TotalCount = total });
    }

    private static IQueryable<SaCustomerTransactionRow> CustomerTransactionUnion(
        AppDbContext db,
        string company,
        string branch,
        SaInquiryQuery query)
    {
        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);
        var cust = Normalize(query.CustCode);
        var rep = Normalize(query.SalesmanCode);
        var status = Normalize(query.Status);
        var search = Normalize(query.SearchText);

        IQueryable<SaCustomerTransactionRow> qts = db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new SaCustomerTransactionRow
            {
                DocType = "QT",
                DocNo = x.QtNo,
                DocDate = x.QtDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesRep = x.SalesRep,
                Status = x.Status,
                TotAmnt = x.TotAmnt
            });
        qts = ApplyCommon(qts, from, toExclusive, cust, rep, status, search);

        IQueryable<SaCustomerTransactionRow> sos = db.SaSos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new SaCustomerTransactionRow
            {
                DocType = "SO",
                DocNo = x.SoNo,
                DocDate = x.SoDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesRep = x.SalesRep,
                Status = x.Status,
                TotAmnt = x.TotAmnt
            });
        sos = ApplyCommon(sos, from, toExclusive, cust, rep, status, search);

        IQueryable<SaCustomerTransactionRow> dos = db.SaDos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new SaCustomerTransactionRow
            {
                DocType = "DO",
                DocNo = x.DoNo,
                DocDate = x.DoDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesRep = x.SalesRep,
                Status = x.Status,
                TotAmnt = x.TotAmnt
            });
        dos = ApplyCommon(dos, from, toExclusive, cust, rep, status, search);

        IQueryable<SaCustomerTransactionRow> invs = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new SaCustomerTransactionRow
            {
                DocType = "INV",
                DocNo = x.InvNo,
                DocDate = x.InvDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesRep = x.SalesmanCode,
                Status = x.Status,
                TotAmnt = x.TotAmnt
            });
        invs = ApplyCommon(invs, from, toExclusive, cust, rep, status, search);

        IQueryable<SaCustomerTransactionRow> cdns = db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new SaCustomerTransactionRow
            {
                DocType = x.Type == SaCdnTypes.DebitNote ? "DN" : "CN",
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesRep = x.SalesRep,
                Status = x.Status,
                TotAmnt = x.TotAmnt
            });
        cdns = ApplyCommon(cdns, from, toExclusive, cust, rep, status, search);

        return qts.Concat(sos).Concat(dos).Concat(invs).Concat(cdns);
    }

    private static IQueryable<SaCustomerTransactionRow> ApplyCommon(
        IQueryable<SaCustomerTransactionRow> rows,
        DateTime? from,
        DateTime? toExclusive,
        string? cust,
        string? rep,
        string? status,
        string? search)
    {
        if (from is DateTime f)
        {
            rows = rows.Where(x => x.DocDate >= f);
        }

        if (toExclusive is DateTime t)
        {
            rows = rows.Where(x => x.DocDate < t);
        }

        if (cust is not null)
        {
            rows = rows.Where(x => x.CustCode == cust);
        }

        if (rep is not null)
        {
            rows = rows.Where(x => x.SalesRep == rep);
        }

        if (status is not null)
        {
            rows = rows.Where(x => x.Status == status);
        }

        if (search is not null)
        {
            rows = rows.Where(x => x.DocNo.Contains(search) || (x.CustName != null && x.CustName.Contains(search)));
        }

        return rows;
    }

    // ============================ Customer Sales History ============================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaCustomerSalesHistoryRow>>> GetCustomerSalesHistoryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<IReadOnlyList<SaCustomerSalesHistoryRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var from = prepared.DateFrom?.Date;
        var toExclusive = prepared.DateTo?.Date.AddDays(1);
        var cust = Normalize(prepared.CustCode);
        var rep = Normalize(prepared.SalesmanCode);

        var invoices = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Status == SaInvoiceStatuses.Posted);
        var cdns = db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Status == SaCdnStatuses.Posted);

        if (from is DateTime f)
        {
            invoices = invoices.Where(x => x.InvDate >= f);
            cdns = cdns.Where(x => x.DocDate >= f);
        }

        if (toExclusive is DateTime t)
        {
            invoices = invoices.Where(x => x.InvDate < t);
            cdns = cdns.Where(x => x.DocDate < t);
        }

        if (cust is not null)
        {
            invoices = invoices.Where(x => x.CustCode == cust);
            cdns = cdns.Where(x => x.CustCode == cust);
        }

        if (rep is not null)
        {
            invoices = invoices.Where(x => x.SalesmanCode == rep);
            cdns = cdns.Where(x => x.SalesRep == rep);
        }

        var invGroups = await invoices
            .GroupBy(x => new { x.InvDate.Year, x.InvDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Count = g.Count(),
                Total = g.Sum(x => x.TotAmnt)
            })
            .ToListAsync(cancellationToken);

        var cnGroups = await cdns
            .Where(x => x.Type == SaCdnTypes.CreditNote)
            .GroupBy(x => new { x.DocDate.Year, x.DocDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Count = g.Count(),
                Total = g.Sum(x => x.TotAmnt)
            })
            .ToListAsync(cancellationToken);

        var dnGroups = await cdns
            .Where(x => x.Type == SaCdnTypes.DebitNote)
            .GroupBy(x => new { x.DocDate.Year, x.DocDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Count = g.Count(),
                Total = g.Sum(x => x.TotAmnt)
            })
            .ToListAsync(cancellationToken);

        var months = invGroups.Select(x => (x.Year, x.Month))
            .Concat(cnGroups.Select(x => (x.Year, x.Month)))
            .Concat(dnGroups.Select(x => (x.Year, x.Month)))
            .Distinct()
            .OrderBy(x => x.Year)
            .ThenBy(x => x.Month)
            .ToList();

        var invByMonth = invGroups.ToDictionary(x => (x.Year, x.Month));
        var cnByMonth = cnGroups.ToDictionary(x => (x.Year, x.Month));
        var dnByMonth = dnGroups.ToDictionary(x => (x.Year, x.Month));

        var rows = months.Select(m =>
        {
            var inv = invByMonth.TryGetValue(m, out var iv) ? iv : null;
            var cn = cnByMonth.TryGetValue(m, out var c) ? c : null;
            var dn = dnByMonth.TryGetValue(m, out var d) ? d : null;

            var invoiceTotal = Money(inv?.Total ?? 0m);
            var cnTotal = Money(cn?.Total ?? 0m);
            var dnTotal = Money(dn?.Total ?? 0m);

            return new SaCustomerSalesHistoryRow
            {
                Year = m.Year,
                Month = m.Month,
                InvoiceCount = inv?.Count ?? 0,
                InvoiceTotal = invoiceTotal,
                CreditNoteCount = cn?.Count ?? 0,
                CreditNoteTotal = cnTotal,
                DebitNoteCount = dn?.Count ?? 0,
                DebitNoteTotal = dnTotal,
                NetSales = Money(invoiceTotal + dnTotal - cnTotal)
            };
        }).ToList();

        return IvMasterOperationResult<IReadOnlyList<SaCustomerSalesHistoryRow>>.Ok(rows);
    }

    // ============================ Quotation Status / Expiry ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaQtStatusRow>>> GetQtStatusAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaQtStatusRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var qts = db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsCurrent);

        qts = ApplyDate(qts, x => x.QtDate, prepared);
        qts = ApplyText(qts, x => x.CustCode, prepared.CustCode);
        qts = ApplyText(qts, x => x.SalesRep, prepared.SalesmanCode);
        qts = ApplyText(qts, x => x.Status, prepared.Status);

        var total = await qts.CountAsync(cancellationToken);
        var projected = await qts
            .OrderByDescending(x => x.QtDate)
            .ThenByDescending(x => x.QtNo)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaQtStatusRow
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

        var today = DateTime.Today;
        foreach (var row in projected)
        {
            // Accepted quotations never auto-expire; the sweep only expires NEW/SENT. Mirror that.
            row.IsExpired = row.Status is not SaQtStatuses.Accepted
                && today > row.ValidUntil.Date;
        }

        return IvMasterOperationResult<SaInquiryPage<SaQtStatusRow>>.Ok(
            new SaInquiryPage<SaQtStatusRow> { Rows = projected, TotalCount = total });
    }

    // ============================ Sales Order Outstanding ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaSoOutstandingRow>>> GetSoOutstandingAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaSoOutstandingRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var slice =
            from h in db.SaSos.AsNoTracking()
            from l in h.Details
            where h.CompanyCode == company && h.BranchCode == branch && h.IsCurrent
            select new { h, l };

        slice = ApplyDate(slice, x => x.h.SoDate, prepared);
        slice = ApplyText(slice, x => x.h.CustCode, prepared.CustCode);
        slice = ApplyText(slice, x => x.h.SalesRep, prepared.SalesmanCode);
        slice = ApplyText(slice, x => x.h.Status, prepared.Status);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await slice
            .OrderBy(x => x.h.SoNo)
            .ThenBy(x => x.l.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaSoOutstandingRow
            {
                SoNo = x.h.SoNo,
                Rev = x.h.CustRel,
                SoDate = x.h.SoDate,
                Status = x.h.Status,
                FulfillmentStatus = x.h.FulfillmentStatus,
                BillingStatus = x.h.BillingStatus,
                CustCode = x.h.CustCode,
                CustName = x.h.CustName,
                SalesRep = x.h.SalesRep,
                TotAmnt = x.h.TotAmnt,
                Line = x.l.Line,
                ICode = x.l.ICode,
                IDesc = x.l.IDesc,
                OrderQty = x.l.OrderQty,
                ShippedQty = x.l.ShippedQty,
                DeliveredQty = x.l.DeliveredQty,
                InvoicedQty = x.l.InvoicedQty,
                BalanceQty = x.l.BalanceQty,
                WrittenOffQty = x.l.WrittenOffQty,
                DeliveryDate = x.l.DeliveryDate
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<SaInquiryPage<SaSoOutstandingRow>>.Ok(
            new SaInquiryPage<SaSoOutstandingRow> { Rows = rows, TotalCount = total });
    }

    // ============================ Delivery Order Status ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaDoStatusRow>>> GetDoStatusAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaDoStatusRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var slice =
            from h in db.SaDos.AsNoTracking()
            from l in h.Details
            where h.CompanyCode == company && h.BranchCode == branch
            select new { h, l };

        slice = ApplyDate(slice, x => x.h.DoDate, prepared);
        slice = ApplyText(slice, x => x.h.CustCode, prepared.CustCode);
        slice = ApplyText(slice, x => x.h.SalesRep, prepared.SalesmanCode);
        slice = ApplyText(slice, x => x.h.Status, prepared.Status);

        var total = await slice.CountAsync(cancellationToken);
        var rows = await slice
            .OrderByDescending(x => x.h.DoDate)
            .ThenByDescending(x => x.h.DoNo)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaDoStatusRow
            {
                DoNo = x.h.DoNo,
                DoDate = x.h.DoDate,
                Status = x.h.Status,
                BillingStatus = x.h.BillingStatus,
                CustCode = x.h.CustCode,
                CustName = x.h.CustName,
                SalesRep = x.h.SalesRep,
                TotAmnt = x.h.TotAmnt,
                Line = x.l.Line,
                ICode = x.l.ICode,
                IDesc = x.l.IDesc,
                Qty = x.l.Qty,
                SoNo = x.l.SoNo,
                InvNo = x.l.InvNo
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<SaInquiryPage<SaDoStatusRow>>.Ok(
            new SaInquiryPage<SaDoStatusRow> { Rows = rows, TotalCount = total });
    }

    // ============================ Document Relationship ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaDocumentRelationshipRow>>> GetDocumentRelationshipAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaDocumentRelationshipRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var all = new List<SaDocumentRelationshipRow>();

        // SO → DO and DO → INV come from the allocation ledger (authoritative allocation quantity).
        var soDo = await (
            from a in db.SaDocApplications.AsNoTracking()
            join so in db.SaSos.AsNoTracking()
                on new { a.CompanyCode, a.BranchCode, a.SourceDocId, a.SourceCustRel }
                equals new { so.CompanyCode, so.BranchCode, SourceDocId = so.SoNo, SourceCustRel = so.CustRel }
            where a.CompanyCode == company && a.BranchCode == branch
                  && a.SourceDocType == SaDocTypes.So && a.TargetDocType == SaDocTypes.Do
            select new SaDocumentRelationshipRow
            {
                Relation = "SO→DO",
                SourceDocNo = a.SourceDocId,
                TargetDocNo = a.TargetDocId,
                Qty = a.AppliedQty,
                RelatedSoNo = a.RelatedSoNo,
                DocDate = so.SoDate,
                CustCode = so.CustCode,
                CustName = so.CustName
            }).ToListAsync(cancellationToken);
        all.AddRange(soDo);

        var doInv = await (
            from a in db.SaDocApplications.AsNoTracking()
            join d in db.SaDos.AsNoTracking()
                on new { a.CompanyCode, a.BranchCode, a.SourceDocId }
                equals new { d.CompanyCode, d.BranchCode, SourceDocId = d.DoNo }
            where a.CompanyCode == company && a.BranchCode == branch
                  && a.SourceDocType == SaDocTypes.Do && a.TargetDocType == SaDocTypes.Inv
            select new SaDocumentRelationshipRow
            {
                Relation = "DO→INV",
                SourceDocNo = a.SourceDocId,
                TargetDocNo = a.TargetDocId,
                Qty = a.AppliedQty,
                RelatedSoNo = a.RelatedSoNo,
                DocDate = d.DoDate,
                CustCode = d.CustCode,
                CustName = d.CustName
            }).ToListAsync(cancellationToken);
        all.AddRange(doInv);

        // INV → DO and INV → SO come from the invoice detail's persisted DoNo / SoNo links.
        var invDo = await (
            from d in db.SaInvoiceDetails.AsNoTracking()
            join i in db.SaInvoices.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.InvNo }
                equals new { i.CompanyCode, i.BranchCode, i.InvNo }
            where d.CompanyCode == company && d.BranchCode == branch && !string.IsNullOrWhiteSpace(d.DoNo)
            select new SaDocumentRelationshipRow
            {
                Relation = "INV→DO",
                SourceDocNo = d.InvNo,
                TargetDocNo = d.DoNo,
                Qty = d.Qty,
                RelatedSoNo = d.SoNo,
                DocDate = i.InvDate,
                CustCode = i.CustCode,
                CustName = i.CustName
            }).ToListAsync(cancellationToken);
        all.AddRange(invDo);

        var invSo = await (
            from d in db.SaInvoiceDetails.AsNoTracking()
            join i in db.SaInvoices.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.InvNo }
                equals new { i.CompanyCode, i.BranchCode, i.InvNo }
            where d.CompanyCode == company && d.BranchCode == branch && !string.IsNullOrWhiteSpace(d.SoNo)
            select new SaDocumentRelationshipRow
            {
                Relation = "INV→SO",
                SourceDocNo = d.InvNo,
                TargetDocNo = d.SoNo,
                Qty = d.Qty,
                RelatedSoNo = d.SoNo,
                DocDate = i.InvDate,
                CustCode = i.CustCode,
                CustName = i.CustName
            }).ToListAsync(cancellationToken);
        all.AddRange(invSo);

        // The allocation ledger per branch is bounded, so the post-union filters run in memory over the
        // already tenant-scoped rows. Customer and date filters apply to the joined header values.
        var from = prepared.DateFrom?.Date;
        var toExclusive = prepared.DateTo?.Date.AddDays(1);
        var cust = Normalize(prepared.CustCode);

        IEnumerable<SaDocumentRelationshipRow> filtered = all;
        if (from is DateTime f)
        {
            filtered = filtered.Where(x => x.DocDate >= f);
        }

        if (toExclusive is DateTime t)
        {
            filtered = filtered.Where(x => x.DocDate < t);
        }

        if (cust is not null)
        {
            filtered = filtered.Where(x => x.CustCode == cust);
        }

        var ordered = filtered
            .OrderByDescending(x => x.DocDate)
            .ThenBy(x => x.Relation)
            .ThenBy(x => x.SourceDocNo)
            .ToList();

        var page = ordered.Skip(skip).Take(take).ToList();

        return IvMasterOperationResult<SaInquiryPage<SaDocumentRelationshipRow>>.Ok(
            new SaInquiryPage<SaDocumentRelationshipRow> { Rows = page, TotalCount = ordered.Count });
    }

    // ============================ CN/DN Inquiry ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaCdnInquiryRow>>> GetCdnInquiryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaCdnInquiryRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var cdns = db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        cdns = ApplyDate(cdns, x => x.DocDate, prepared);
        cdns = ApplyText(cdns, x => x.CustCode, prepared.CustCode);
        cdns = ApplyText(cdns, x => x.SalesRep, prepared.SalesmanCode);
        cdns = ApplyText(cdns, x => x.Status, prepared.Status);
        cdns = ApplyText(cdns, x => x.Type, prepared.Type);

        var search = Normalize(prepared.SearchText);
        if (search is not null)
        {
            cdns = cdns.Where(x => x.DocNo.Contains(search)
                || (x.InvNo != null && x.InvNo.Contains(search))
                || (x.RefNo != null && x.RefNo.Contains(search))
                || (x.Remarks != null && x.Remarks.Contains(search)));
        }

        var total = await cdns.CountAsync(cancellationToken);
        var rows = await cdns
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaCdnInquiryRow
            {
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                Type = x.Type,
                Status = x.Status,
                InvNo = x.InvNo,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesRep = x.SalesRep,
                RefNo = x.RefNo,
                Remarks = x.Remarks,
                TotAmnt = x.TotAmnt
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<SaInquiryPage<SaCdnInquiryRow>>.Ok(
            new SaInquiryPage<SaCdnInquiryRow> { Rows = rows, TotalCount = total });
    }

    // ============================ e-Invoice ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceStatusRow>>> GetEInvoiceStatusAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaEInvoiceStatusRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

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

        await DecorateEInvoiceRowsAsync(db, company, rows, cancellationToken);

        return IvMasterOperationResult<SaInquiryPage<SaEInvoiceStatusRow>>.Ok(
            new SaInquiryPage<SaEInvoiceStatusRow> { Rows = rows, TotalCount = total });
    }

    private static IQueryable<SaEInvoiceStatusRow> EInvoiceStatusUnion(
        AppDbContext db,
        string company,
        string branch,
        SaInquiryQuery query)
    {
        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);
        var cust = Normalize(query.CustCode);

        IQueryable<SaEInvoiceStatusRow> invs = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new SaEInvoiceStatusRow
            {
                DocType = EInvoiceDocumentTypes.Invoice,
                DocNo = x.InvNo,
                DocDate = x.InvDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                IrbmStatus = x.IrbmStatus,
                IrbmUuid = x.IrbmUuid,
                IrbmSubmitId = x.IrbmSubmitId,
                IrbmSentOn = x.IrbmSentOn,
                TotAmnt = x.TotAmnt
            });
        invs = ApplyCommonEInvoice(invs, from, toExclusive, cust);

        IQueryable<SaEInvoiceStatusRow> cdns = db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new SaEInvoiceStatusRow
            {
                DocType = x.Type == SaCdnTypes.DebitNote ? EInvoiceDocumentTypes.DebitNote : EInvoiceDocumentTypes.CreditNote,
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                IrbmStatus = x.IrbmStatus,
                IrbmUuid = x.IrbmUuid,
                IrbmSubmitId = x.IrbmSubmitId,
                IrbmSentOn = x.IrbmSentOn,
                TotAmnt = x.TotAmnt
            });
        cdns = ApplyCommonEInvoice(cdns, from, toExclusive, cust);

        return invs.Concat(cdns);
    }

    private static IQueryable<SaEInvoiceStatusRow> ApplyCommonEInvoice(
        IQueryable<SaEInvoiceStatusRow> rows,
        DateTime? from,
        DateTime? toExclusive,
        string? cust)
    {
        if (from is DateTime f)
        {
            rows = rows.Where(x => x.DocDate >= f);
        }

        if (toExclusive is DateTime t)
        {
            rows = rows.Where(x => x.DocDate < t);
        }

        if (cust is not null)
        {
            rows = rows.Where(x => x.CustCode == cust);
        }

        return rows;
    }

    private async Task DecorateEInvoiceRowsAsync(
        AppDbContext db,
        string company,
        List<SaEInvoiceStatusRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var keys = rows
            .Select(x => (Type: x.DocType, No: x.DocNo))
            .Distinct()
            .ToList();
        var docNos = keys.Select(k => k.No).Distinct().ToList();

        var registry = await db.EInvDocSubmissions.AsNoTracking()
            .Where(x => x.CompanyId == company
                && x.DocumentNo != null
                && x.DocumentType != null
                && docNos.Contains(x.DocumentNo)
                && EInvoiceDocTypeFilter.Contains(x.DocumentType))
            .OrderByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.DocumentType,
                x.DocumentNo,
                x.Status,
                x.DateTimeIssued
            })
            .ToListAsync(cancellationToken);

        var latestByKey = new Dictionary<(string Type, string No), (string Status, DateTime? Issued)>();
        foreach (var r in registry)
        {
            var key = (r.DocumentType!.ToUpperInvariant(), r.DocumentNo!.ToUpperInvariant());
            if (!latestByKey.ContainsKey(key))
            {
                latestByKey[key] = (r.Status, r.DateTimeIssued);
            }
        }

        foreach (var row in rows)
        {
            var key = (row.DocType.ToUpperInvariant(), row.DocNo.ToUpperInvariant());
            var hasRegistry = latestByKey.TryGetValue(key, out var latest);

            row.IsSubmitted = hasRegistry;
            row.IsNotSubmitted = !hasRegistry && string.IsNullOrWhiteSpace(row.IrbmStatus);

            if (hasRegistry)
            {
                var normalized = EInvoiceStatuses.Normalize(latest.Status);
                row.LatestSubmissionStatus = normalized;
                row.LatestSubmittedOn = latest.Issued;
                // A submission row whose status is blank or not in the vocabulary reads as NEW after
                // normalisation, which is indistinguishable from "not submitted" — so flag it.
                row.IsUnknownStatus = normalized == EInvoiceStatuses.New
                    && !string.IsNullOrWhiteSpace(latest.Status)
                    && !EInvoiceStatuses.All.Contains(latest.Status);
            }

            row.NormalizedStatus = EInvoiceStatuses.Normalize(row.IrbmStatus);
            row.StatusLabel = ResolveEInvoiceLabel(row);
        }
    }

    private static string ResolveEInvoiceLabel(SaEInvoiceStatusRow row)
    {
        if (row.IsNotSubmitted)
        {
            return "Not submitted";
        }

        if (row.IsUnknownStatus)
        {
            return "Unknown (unmapped)";
        }

        if (row.IsSubmitted && row.LatestSubmissionStatus is not null)
        {
            return row.LatestSubmissionStatus;
        }

        if (!string.IsNullOrWhiteSpace(row.IrbmStatus))
        {
            return row.NormalizedStatus;
        }

        return "Not submitted";
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<SaEInvoiceSubmissionRow>>> GetEInvoiceSubmissionHistoryAsync(
        string menuCode,
        string docType,
        string docNo,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<IReadOnlyList<SaEInvoiceSubmissionRow>>(gate);
        }

        var type = (docType ?? string.Empty).Trim().ToUpperInvariant();
        var no = (docNo ?? string.Empty).Trim();
        if (type.Length == 0 || no.Length == 0)
        {
            return IvMasterOperationResult<IReadOnlyList<SaEInvoiceSubmissionRow>>.Fail(
                IvMasterErrorCode.Validation, "A document type and number are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;

        var rows = await db.EInvDocSubmissions.AsNoTracking()
            .Where(x => x.CompanyId == company
                && x.DocumentType == type
                && x.DocumentNo == no)
            .OrderBy(x => x.Id)
            .Select(x => new SaEInvoiceSubmissionRow
            {
                Id = x.Id,
                SubmittedOn = x.DateTimeIssued,
                Status = EInvoiceStatuses.Normalize(x.Status),
                SubmissionUuid = x.SubmissionUuid,
                Uuid = x.Uuid,
                LongId = x.LongId
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaEInvoiceSubmissionRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<SaInquiryPage<SaEInvoiceReconciliationRow>>> GetEInvoiceReconciliationAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaEInvoiceReconciliationRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var union = EInvoiceStatusUnion(db, company, branch, prepared);

        var total = await union.CountAsync(cancellationToken);
        var statusRows = await union
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        await DecorateEInvoiceRowsAsync(db, company, statusRows, cancellationToken);

        var rows = statusRows.Select(x =>
        {
            var erpStatus = EInvoiceStatuses.Normalize(x.IrbmStatus);
            var finding = ReconciliationFinding(erpStatus, x.IrbmUuid, x.LatestSubmissionStatus, x.IsNotSubmitted, x.IsUnknownStatus);

            return new SaEInvoiceReconciliationRow
            {
                DocType = x.DocType,
                DocNo = x.DocNo,
                ErpStatus = string.IsNullOrWhiteSpace(x.IrbmStatus) ? null : erpStatus,
                ErpUuid = x.IrbmUuid,
                RegistryStatus = x.LatestSubmissionStatus,
                RegistryUuid = null,
                Finding = finding
            };
        }).ToList();

        return IvMasterOperationResult<SaInquiryPage<SaEInvoiceReconciliationRow>>.Ok(
            new SaInquiryPage<SaEInvoiceReconciliationRow> { Rows = rows, TotalCount = total });
    }

    private static string ReconciliationFinding(
        string erpStatus,
        string? erpUuid,
        string? registryStatus,
        bool isNotSubmitted,
        bool isUnknown)
    {
        if (isNotSubmitted)
        {
            return "not submitted";
        }

        if (isUnknown)
        {
            return "submission status unknown/unmapped";
        }

        if (registryStatus is null)
        {
            return "submitted (no registry row)";
        }

        if (erpStatus == EInvoiceStatuses.New)
        {
            return "registry has a status but the document is not submitted";
        }

        if (registryStatus != erpStatus)
        {
            return "status differs";
        }

        return "match";
    }

    // ============================ Shared helpers ============================

    private async Task<IvInquiryScopeContext> GateAsync(string menuCode, CancellationToken cancellationToken) =>
        await IvInquiryScopeResolver.ResolveAsync(
            _tenant, _accessRights, menuCode, KnownMenus, cancellationToken);

    private static IvMasterOperationResult<T> Fail<T>(IvInquiryScopeContext gate) =>
        IvMasterOperationResult<T>.Fail(gate.ErrorCode, gate.Error ?? "Not authorized.");

    private static (int Skip, int Take) Page(SaInquiryQuery query)
    {
        var skip = Math.Max(0, query.Skip);
        var take = query.Take <= 0 ? DefaultPageSize : Math.Min(query.Take, MaxExportRows);
        return (skip, take);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IQueryable<T> ApplyText<T>(
        IQueryable<T> source,
        Expression<Func<T, string?>> selector,
        string? value)
    {
        var v = Normalize(value);
        return v is null ? source : source.Where(BuildEquals(selector, v));
    }

    private static IQueryable<T> ApplyDate<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTime>> selector,
        SaInquiryQuery query)
    {
        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);

        if (from is DateTime f)
        {
            source = source.Where(BuildCompare(selector, ExpressionType.GreaterThanOrEqual, f));
        }

        if (toExclusive is DateTime t)
        {
            source = source.Where(BuildCompare(selector, ExpressionType.LessThan, t));
        }

        return source;
    }

    /// <summary>
    /// Builds a translatable <c>==</c> predicate from a member selector. Expression trees are composable
    /// by the provider, unlike a compiled delegate, so the predicate stays server-side.
    /// </summary>
    private static Expression<Func<T, bool>> BuildEquals<T>(
        Expression<Func<T, string?>> selector,
        string value)
    {
        var parameter = selector.Parameters[0];
        var body = Expression.Equal(selector.Body, Expression.Constant(value, typeof(string)));
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>Builds a translatable comparison predicate from a member selector.</summary>
    private static Expression<Func<T, bool>> BuildCompare<T, TValue>(
        Expression<Func<T, TValue>> selector,
        ExpressionType comparison,
        TValue value)
    {
        var parameter = selector.Parameters[0];
        var body = Expression.MakeBinary(comparison, selector.Body, Expression.Constant(value, typeof(TValue)));
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static decimal Money(decimal value) => SaInvoiceCalc.Money(value);
}
