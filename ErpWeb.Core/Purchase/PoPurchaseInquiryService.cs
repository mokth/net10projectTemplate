using System.Linq.Expressions;
using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Purchase Inquiry Phase 1 — read-only operational grids over Purchase + Inventory GR entities.
/// Mirrors <see cref="Sales.SaSalesInquiryService"/> gating (tenant first, then ACCESS) and the
/// locked Step 0.5 rules in <c>plans/Procurement-Inquiry-Assessment.md</c>.
/// </summary>
public sealed partial class PoPurchaseInquiryService : IPoPurchaseInquiryService
{
    private static readonly HashSet<string> KnownMenus = new(StringComparer.OrdinalIgnoreCase)
    {
        MenuCodes.PurchaseOrderOutstanding,
        MenuCodes.PurchasePrStatus,
        MenuCodes.PurchaseSupplierTransaction,
        MenuCodes.PurchaseInvoiceInquiry,
        MenuCodes.PurchaseCdnInquiry,
        MenuCodes.PurchaseDocRelationship,
        MenuCodes.PurchaseSbEInvoiceInquiry,
        MenuCodes.PurchasePriceHistory,
        MenuCodes.PurchaseMatching,
        MenuCodes.PurchaseDeliveryPerformance
    };

    private const int DefaultPageSize = 50;
    private const int MaxExportRows = 50_000;

    private static readonly string[] SbEInvoiceDocTypeFilter =
    [
        EInvoiceDocumentTypes.SelfBilledInvoice,
        EInvoiceDocumentTypes.SelfBilledCreditNote,
        EInvoiceDocumentTypes.SelfBilledDebitNote
    ];

    private static readonly string[] GoodsReceiptTrxTypes =
    [
        IvTrxTypes.GoodsReceive,
        IvTrxTypes.NonStockGoodsReceive
    ];

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public PoPurchaseInquiryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    // ============================ PO Outstanding ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoOrderOutstandingRow>>> GetPoOutstandingAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoOrderOutstandingRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);
        var asOf = prepared.AsOfDate?.Date ?? DateTime.Today;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        // Canonical pipeline: base filters → ACCESS (gate) → latest revision → WorkbenchPreset.
        var slice = BuildPoOutstandingSlice(db, company, branch, prepared, applyPreset: true);

        var total = await slice.CountAsync(cancellationToken);
        var projected = await slice
            .OrderByDescending(x => x.Header.PoDate)
            .ThenByDescending(x => x.Header.PoNo)
            .ThenBy(x => x.Line.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new
            {
                x.Header.PoNo,
                x.Header.PoRelNo,
                x.Header.PoDate,
                x.Header.Status,
                x.Header.VendCode,
                x.Header.VendName,
                x.Header.Buyer,
                x.Line.Line,
                x.Line.ICode,
                x.Line.IDesc,
                x.Line.PoPurQty,
                x.Line.RecvQty,
                x.Line.ReturnQty,
                x.Line.BalanceQty,
                x.Line.InvoicedQty,
                x.Line.NetAmount,
                x.Line.EtaDate,
                x.Line.ToWarehouse
            })
            .ToListAsync(cancellationToken);

        var rows = projected.Select(x => MapPoOutstandingRow(
            x.PoNo, x.PoRelNo, x.PoDate, x.Status,
            x.VendCode, x.VendName, x.Buyer, x.Line, x.ICode, x.IDesc,
            x.PoPurQty, x.RecvQty, x.ReturnQty, x.BalanceQty, x.InvoicedQty, x.NetAmount, x.EtaDate,
            x.ToWarehouse, asOf)).ToList();

        return IvMasterOperationResult<PoInquiryPage<PoOrderOutstandingRow>>.Ok(
            new PoInquiryPage<PoOrderOutstandingRow> { Rows = rows, TotalCount = total });
    }

    /// <summary>
    /// Chips use the same base filters + latest-revision rules as the grid, but deliberately omit
    /// WorkbenchPreset so Open / Overdue / Partial / Unbilled remain visible as exception navigation.
    /// When a preset is active, grid TotalCount matches the corresponding chip count.
    /// </summary>
    public async Task<IvMasterOperationResult<PoOrderOutstandingSummary>> GetPoOutstandingSummaryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoOrderOutstandingSummary>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var asOf = prepared.AsOfDate?.Date ?? DateTime.Today;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var baseSlice = BuildPoOutstandingSlice(db, company, branch, prepared, applyPreset: false);

        var openSlice = baseSlice.Where(x => x.Line.BalanceQty > 0m);
        var openCount = await openSlice.CountAsync(cancellationToken);
        var overdueCount = await openSlice
            .Where(x => x.Line.EtaDate != null && x.Line.EtaDate < asOf)
            .CountAsync(cancellationToken);
        var partialCount = await openSlice
            .Where(x => x.Line.RecvQty > 0m)
            .CountAsync(cancellationToken);

        // OutstandingValue: allocate NetAmount by Balance/PoPurQty (server-side sum).
        // Evidence: PoOrderDetail.NetAmount is commercial net after discounts (PoOrderCalc.ApplyTwoLevelDiscount).
        var outstandingValue = await openSlice
            .Where(x => x.Line.PoPurQty > 0m)
            .SumAsync(x => (decimal?)(x.Line.NetAmount * x.Line.BalanceQty / x.Line.PoPurQty), cancellationToken) ?? 0m;

        // UNBILLED = ComputeInvoiceable > 0 (RecvQty - ReturnQty - InvoicedQty > 0).
        var unbilledCount = await baseSlice
            .Where(x => (x.Line.RecvQty - x.Line.ReturnQty - x.Line.InvoicedQty) > 0m)
            .CountAsync(cancellationToken);

        return IvMasterOperationResult<PoOrderOutstandingSummary>.Ok(new PoOrderOutstandingSummary
        {
            OpenLineCount = openCount,
            OverdueLineCount = overdueCount,
            PartialLineCount = partialCount,
            UnbilledLineCount = unbilledCount,
            OutstandingValue = PoOrderCalc.RoundMoney(outstandingValue)
        });
    }

    /// <summary>
    /// Shared PO outstanding pipeline.
    /// Latest revision = MAX(PoRelNo) per PoNo — same as PoOrderRepository.SearchLatestPagedAsync.
    /// PARTIAL = RecvQty &gt; 0 AND BalanceQty &gt; 0 (line-level; PoOrderCalc.AnyReceived is header-level).
    /// UNBILLED = ComputeInvoiceable &gt; 0 (RecvQty - ReturnQty - InvoicedQty &gt; 0).
    /// </summary>
    private static IQueryable<PoOutstandingJoin> BuildPoOutstandingSlice(
        AppDbContext db,
        string company,
        string branch,
        PoInquiryQuery prepared,
        bool applyPreset)
    {
        var maxRelQuery = db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .GroupBy(x => x.PoNo)
            .Select(g => new { PoNo = g.Key, MaxRel = g.Max(x => x.PoRelNo) });

        IQueryable<PoOutstandingJoin> slice =
            from h in db.PoOrders.AsNoTracking()
            join m in maxRelQuery
                on new { h.PoNo, Rel = h.PoRelNo } equals new { m.PoNo, Rel = m.MaxRel }
            from l in h.Details
            where h.CompanyCode == company
                  && h.BranchCode == branch
                  && h.Status != PoOrderStatuses.Cancelled
            select new PoOutstandingJoin { Header = h, Line = l };

        slice = ApplyNullableDate(slice, x => x.Header.PoDate, prepared);
        slice = ApplyText(slice, x => x.Header.VendCode, prepared.SuppCode);
        slice = ApplyText(slice, x => x.Header.Buyer, prepared.BuyerCode);
        slice = ApplyText(slice, x => x.Header.Status, prepared.Status);

        var search = Normalize(prepared.SearchText);
        if (search is not null)
        {
            slice = slice.Where(x =>
                x.Header.PoNo.Contains(search)
                || (x.Header.VendName != null && x.Header.VendName.Contains(search))
                || (x.Line.ICode != null && x.Line.ICode.Contains(search))
                || (x.Line.IDesc != null && x.Line.IDesc.Contains(search))
                || (x.Line.PrNo != null && x.Line.PrNo.Contains(search)));
        }

        if (!applyPreset)
        {
            return slice;
        }

        var preset = Normalize(prepared.WorkbenchPreset)?.ToUpperInvariant();
        var asOf = prepared.AsOfDate?.Date ?? DateTime.Today;

        return preset switch
        {
            PoInquiryWorkbenchPresets.Overdue => slice.Where(x =>
                x.Line.BalanceQty > 0m && x.Line.EtaDate != null && x.Line.EtaDate < asOf),
            PoInquiryWorkbenchPresets.Partial => slice.Where(x =>
                x.Line.BalanceQty > 0m && x.Line.RecvQty > 0m),
            PoInquiryWorkbenchPresets.Unbilled => slice.Where(x =>
                (x.Line.RecvQty - x.Line.ReturnQty - x.Line.InvoicedQty) > 0m),
            _ => slice.Where(x => x.Line.BalanceQty > 0m)
        };
    }

    private sealed class PoOutstandingJoin
    {
        public PoOrder Header { get; set; } = null!;
        public PoOrderDetail Line { get; set; } = null!;
    }

    private static PoOrderOutstandingRow MapPoOutstandingRow(
        string poNo, short poRelNo, DateTime? poDate, string? status,
        string? vendCode, string? vendName, string? buyer, short line,
        string? iCode, string? iDesc, decimal poPurQty, decimal recvQty, decimal returnQty,
        decimal balanceQty, decimal invoicedQty, decimal netAmount, DateTime? etaDate,
        string? toWarehouse, DateTime asOf)
    {
        var invoiceable = PoOrderCalc.ComputeInvoiceable(recvQty, returnQty, invoicedQty);
        var outstandingValue = poPurQty > 0m
            ? PoOrderCalc.RoundMoney(netAmount * balanceQty / poPurQty)
            : 0m;
        int? daysOverdue = null;
        if (balanceQty > 0m && etaDate is DateTime eta && eta.Date < asOf)
        {
            daysOverdue = (asOf - eta.Date).Days;
        }

        return new PoOrderOutstandingRow
        {
            PoNo = poNo,
            PoRelNo = poRelNo,
            PoDate = poDate,
            Status = status,
            VendCode = vendCode,
            VendName = vendName,
            Buyer = buyer,
            Line = line,
            ICode = iCode,
            IDesc = iDesc,
            PoPurQty = poPurQty,
            RecvQty = recvQty,
            ReturnQty = returnQty,
            BalanceQty = balanceQty,
            InvoicedQty = invoicedQty,
            InvoiceableQty = invoiceable,
            OutstandingValue = outstandingValue,
            EtaDate = etaDate,
            DaysOverdue = daysOverdue,
            ToWarehouse = toWarehouse
        };
    }

    // ============================ PR Status ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoPrStatusRow>>> GetPrStatusAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoPrStatusRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;
        var cancelled = PoOrderStatuses.Cancelled;

        var slice = BuildPrStatusSlice(db, company, branch, prepared, cancelled, applyPreset: true);

        var total = await slice.CountAsync(cancellationToken);
        var projected = await slice
            .OrderByDescending(x => x.Header.CreateDt)
            .ThenByDescending(x => x.Header.PrNo)
            .ThenBy(x => x.Detail.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new
            {
                x.Header.PrNo,
                x.Header.CreateDt,
                HeaderStatus = x.Header.Status,
                x.Header.Requester,
                x.Header.DeptCode,
                x.Detail.Line,
                x.Detail.ICode,
                x.Detail.IDesc,
                x.Detail.PurchaseQty,
                // LivePoConsumedForPrAsync: all non-cancelled PoRelNo rows for this PrNo/PrLineNo.
                ConsumedQty = db.PoOrderDetails
                    .Where(od => od.CompanyCode == company
                        && od.BranchCode == branch
                        && od.PrNo == x.Detail.PrNo
                        && od.PrLineNo == x.Detail.Line
                        && od.Order.Status != cancelled)
                    .Sum(od => (decimal?)od.PoPurQty) ?? 0m,
                x.Detail.VendorCd,
                x.Detail.ToWarehouse,
                x.Detail.EtaDt,
                LineStatus = x.Detail.Status
            })
            .ToListAsync(cancellationToken);

        var rows = projected.Select(x =>
        {
            var consumed = PoOrderCalc.RoundQty(x.ConsumedQty);
            return new PoPrStatusRow
            {
                PrNo = x.PrNo,
                CreateDt = x.CreateDt,
                HeaderStatus = x.HeaderStatus,
                Requester = x.Requester,
                DeptCode = x.DeptCode,
                Line = x.Line,
                ICode = x.ICode,
                IDesc = x.IDesc,
                PurchaseQty = x.PurchaseQty,
                ConsumedQty = consumed,
                RemainingQty = PoOrderCalc.RoundQty(x.PurchaseQty - consumed),
                VendorCd = x.VendorCd,
                ToWarehouse = x.ToWarehouse,
                EtaDt = x.EtaDt,
                LineStatus = x.LineStatus
            };
        }).ToList();

        await AttachLinkedPoNosAsync(db, company, branch, cancelled, rows, cancellationToken);

        return IvMasterOperationResult<PoInquiryPage<PoPrStatusRow>>.Ok(
            new PoInquiryPage<PoPrStatusRow> { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<PoPrStatusSummary>> GetPrStatusSummaryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoPrStatusSummary>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;
        var cancelled = PoOrderStatuses.Cancelled;

        // Chips omit WorkbenchPreset — Unconverted count uses RemainingQty > 0 via SQL (same as preset).
        var unconvertedQuery = new PoInquiryQuery
        {
            DateFrom = prepared.DateFrom,
            DateTo = prepared.DateTo,
            SuppCode = prepared.SuppCode,
            BuyerCode = prepared.BuyerCode,
            Status = prepared.Status,
            Type = prepared.Type,
            SearchText = prepared.SearchText,
            BranchCode = prepared.BranchCode,
            WorkbenchPreset = PoInquiryWorkbenchPresets.Unconverted
        };
        var unconverted = BuildPrStatusSlice(db, company, branch, unconvertedQuery, cancelled, applyPreset: true);

        var unconvertedCount = await unconverted.CountAsync(cancellationToken);
        var totalRemaining = await unconverted
            .SumAsync(x => (decimal?)(
                x.Detail.PurchaseQty
                - (db.PoOrderDetails
                    .Where(od => od.CompanyCode == company
                        && od.BranchCode == branch
                        && od.PrNo == x.Detail.PrNo
                        && od.PrLineNo == x.Detail.Line
                        && od.Order.Status != cancelled)
                    .Sum(od => (decimal?)od.PoPurQty) ?? 0m)), cancellationToken) ?? 0m;

        return IvMasterOperationResult<PoPrStatusSummary>.Ok(new PoPrStatusSummary
        {
            UnconvertedLineCount = unconvertedCount,
            TotalRemainingQty = PoOrderCalc.RoundQty(totalRemaining)
        });
    }

    private static IQueryable<PoPrStatusJoin> BuildPrStatusSlice(
        AppDbContext db,
        string company,
        string branch,
        PoInquiryQuery prepared,
        string cancelled,
        bool applyPreset)
    {
        IQueryable<PoPrStatusJoin> slice =
            from d in db.PoPrDetails.AsNoTracking()
            join h in db.PoPrs.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.PrNo }
                equals new { h.CompanyCode, h.BranchCode, h.PrNo }
            where h.CompanyCode == company && h.BranchCode == branch
            select new PoPrStatusJoin { Header = h, Detail = d };

        slice = ApplyDate(slice, x => x.Header.CreateDt, prepared);
        slice = ApplyText(slice, x => x.Detail.VendorCd, prepared.SuppCode);
        slice = ApplyText(slice, x => x.Header.Status, prepared.Status);

        var search = Normalize(prepared.SearchText);
        if (search is not null)
        {
            slice = slice.Where(x =>
                x.Header.PrNo.Contains(search)
                || (x.Header.Requester != null && x.Header.Requester.Contains(search))
                || (x.Detail.ICode != null && x.Detail.ICode.Contains(search))
                || (x.Detail.IDesc != null && x.Detail.IDesc.Contains(search)));
        }

        if (applyPreset
            && string.Equals(
                Normalize(prepared.WorkbenchPreset),
                PoInquiryWorkbenchPresets.Unconverted,
                StringComparison.OrdinalIgnoreCase))
        {
            // UNCONVERTED = RemainingQty > 0 — RemainingQty from LivePoConsumedForPrAsync semantics.
            slice = slice.Where(x =>
                x.Detail.PurchaseQty
                - (db.PoOrderDetails
                    .Where(od => od.CompanyCode == company
                        && od.BranchCode == branch
                        && od.PrNo == x.Detail.PrNo
                        && od.PrLineNo == x.Detail.Line
                        && od.Order.Status != cancelled)
                    .Sum(od => (decimal?)od.PoPurQty) ?? 0m) > 0m);
        }

        return slice;
    }

    private sealed class PoPrStatusJoin
    {
        public PoPr Header { get; set; } = null!;
        public PoPrDetail Detail { get; set; } = null!;
    }

    private static async Task AttachLinkedPoNosAsync(
        AppDbContext db,
        string company,
        string branch,
        string cancelled,
        List<PoPrStatusRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var prNos = rows.Select(x => x.PrNo).Distinct().ToList();
        var links = await db.PoOrderDetails.AsNoTracking()
            .Where(od => od.CompanyCode == company
                && od.BranchCode == branch
                && od.PrNo != null
                && prNos.Contains(od.PrNo)
                && od.Order.Status != cancelled)
            .Select(od => new { od.PrNo, od.PrLineNo, od.PoNo })
            .ToListAsync(cancellationToken);

        var byKey = links
            .GroupBy(x => (x.PrNo!, x.PrLineNo ?? (short)0))
            .ToDictionary(
                g => g.Key,
                g => string.Join(",", g.Select(x => x.PoNo).Distinct().OrderBy(n => n)));

        foreach (var row in rows)
        {
            if (byKey.TryGetValue((row.PrNo, row.Line), out var nos))
            {
                row.LinkedPoNos = nos;
            }
        }
    }

    // ============================ Supplier Transactions ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoSupplierTransactionRow>>> GetSupplierTransactionsAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoSupplierTransactionRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var union = SupplierTransactionUnion(db, company, branch, prepared);

        var total = await union.CountAsync(cancellationToken);
        var rows = await union
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<PoInquiryPage<PoSupplierTransactionRow>>.Ok(
            new PoInquiryPage<PoSupplierTransactionRow> { Rows = rows, TotalCount = total });
    }

    private static IQueryable<PoSupplierTransactionRow> SupplierTransactionUnion(
        AppDbContext db,
        string company,
        string branch,
        PoInquiryQuery query)
    {
        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);
        var supp = Normalize(query.SuppCode);
        var buyer = Normalize(query.BuyerCode);
        var status = Normalize(query.Status);
        var search = Normalize(query.SearchText);

        // PR amount = Sum((IsInclusive ? Amount-TaxAmount : Amount) + TaxAmount) — same as PoPr list Total.
        IQueryable<PoSupplierTransactionRow> prs = db.PoPrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSupplierTransactionRow
            {
                DocType = "PR",
                DocNo = x.PrNo,
                DocDate = x.CreateDt,
                SuppCode = x.Details.OrderBy(d => d.Line).Select(d => d.VendorCd).FirstOrDefault(),
                SuppName = x.Details.OrderBy(d => d.Line).Select(d => d.VendNm).FirstOrDefault(),
                Buyer = x.Requester,
                Status = x.Status,
                TotAmnt = x.Details.Sum(d =>
                    (d.IsInclusive ? d.Amount - d.TaxAmount : d.Amount) + d.TaxAmount),
                PoRelNo = null,
                Extra = x.DeptCode
            });
        prs = ApplySupplierCommon(prs, from, toExclusive, supp, buyer, status, search);

        // PO amount = PoOrderCalc.SumTotals of detail (NetAmount, TaxAmount) → Total.
        IQueryable<PoSupplierTransactionRow> pos = db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSupplierTransactionRow
            {
                DocType = "PO",
                DocNo = x.PoNo,
                DocDate = x.PoDate ?? x.CreatedDate ?? DateTime.MinValue,
                SuppCode = x.VendCode,
                SuppName = x.VendName,
                Buyer = x.Buyer,
                Status = x.Status,
                TotAmnt = x.Details.Sum(d => d.NetAmount + d.TaxAmount),
                PoRelNo = x.PoRelNo,
                Extra = null
            });
        pos = ApplySupplierCommon(pos, from, toExclusive, supp, buyer, status, search);

        IQueryable<PoSupplierTransactionRow> invs = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSupplierTransactionRow
            {
                DocType = x.Type == PoInvoiceTypes.CreditNote ? "QTYCN" : "INV",
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                SuppCode = x.VendorCode,
                SuppName = x.VendorName,
                Buyer = x.SalesRep,
                Status = x.Status,
                TotAmnt = x.TotAmnt,
                PoRelNo = null,
                Extra = x.ExternalDocNo
            });
        invs = ApplySupplierCommon(invs, from, toExclusive, supp, buyer, status, search);

        IQueryable<PoSupplierTransactionRow> cdns = db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSupplierTransactionRow
            {
                DocType = x.Type == PoCdnTypes.DebitNote ? "DN" : "CN",
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                SuppCode = x.VendorCode,
                SuppName = x.VendorName,
                Buyer = x.BuyerCode,
                Status = x.Status,
                TotAmnt = x.TotAmnt,
                PoRelNo = null,
                Extra = x.InvNo
            });
        cdns = ApplySupplierCommon(cdns, from, toExclusive, supp, buyer, status, search);

        IQueryable<PoSupplierTransactionRow> sbInvs = db.PoSbInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSupplierTransactionRow
            {
                DocType = EInvoiceDocumentTypes.SelfBilledInvoice,
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                SuppCode = x.VendorCode,
                SuppName = x.VendorName,
                Buyer = null,
                Status = x.Status,
                TotAmnt = x.TotAmnt,
                PoRelNo = null,
                Extra = null
            });
        sbInvs = ApplySupplierCommon(sbInvs, from, toExclusive, supp, buyer, status, search);

        IQueryable<PoSupplierTransactionRow> sbCdns = db.PoSbCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSupplierTransactionRow
            {
                DocType = x.Type == PoCdnTypes.DebitNote
                    ? EInvoiceDocumentTypes.SelfBilledDebitNote
                    : EInvoiceDocumentTypes.SelfBilledCreditNote,
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                SuppCode = x.VendorCode,
                SuppName = x.VendorName,
                Buyer = null,
                Status = x.Status,
                TotAmnt = x.TotAmnt,
                PoRelNo = null,
                Extra = x.OriginSbInvNo
            });
        sbCdns = ApplySupplierCommon(sbCdns, from, toExclusive, supp, buyer, status, search);

        return prs.Concat(pos).Concat(invs).Concat(cdns).Concat(sbInvs).Concat(sbCdns);
    }

    private static IQueryable<PoSupplierTransactionRow> ApplySupplierCommon(
        IQueryable<PoSupplierTransactionRow> rows,
        DateTime? from,
        DateTime? toExclusive,
        string? supp,
        string? buyer,
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

        if (supp is not null)
        {
            rows = rows.Where(x => x.SuppCode == supp);
        }

        if (buyer is not null)
        {
            rows = rows.Where(x => x.Buyer == buyer);
        }

        if (status is not null)
        {
            rows = rows.Where(x => x.Status == status);
        }

        if (search is not null)
        {
            rows = rows.Where(x =>
                x.DocNo.Contains(search)
                || (x.SuppName != null && x.SuppName.Contains(search)));
        }

        return rows;
    }

    // ============================ Supplier Purchase History (Tab B) ============================

    public async Task<IvMasterOperationResult<IReadOnlyList<PoSupplierPurchaseHistoryRow>>> GetSupplierPurchaseHistoryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<IReadOnlyList<PoSupplierPurchaseHistoryRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var from = prepared.DateFrom?.Date;
        var toExclusive = prepared.DateTo?.Date.AddDays(1);
        var supp = Normalize(prepared.SuppCode);
        var buyer = Normalize(prepared.BuyerCode);

        // Tab B: POSTED INV only (omit qty-CN) + POSTED PoCdn; omit self-billed.
        var invoices = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.Status == PoInvoiceStatuses.Posted
                && x.Type == PoInvoiceTypes.Invoice);

        var cdns = db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.Status == PoCdnStatuses.Posted);

        if (from is DateTime f)
        {
            invoices = invoices.Where(x => x.DocDate >= f);
            cdns = cdns.Where(x => x.DocDate >= f);
        }

        if (toExclusive is DateTime t)
        {
            invoices = invoices.Where(x => x.DocDate < t);
            cdns = cdns.Where(x => x.DocDate < t);
        }

        if (supp is not null)
        {
            invoices = invoices.Where(x => x.VendorCode == supp);
            cdns = cdns.Where(x => x.VendorCode == supp);
        }

        if (buyer is not null)
        {
            invoices = invoices.Where(x => x.SalesRep == buyer);
            cdns = cdns.Where(x => x.BuyerCode == buyer);
        }

        var invGroups = await invoices
            .GroupBy(x => new { x.DocDate.Year, x.DocDate.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Count = g.Count(),
                Total = g.Sum(x => x.TotAmnt)
            })
            .ToListAsync(cancellationToken);

        var cnGroups = await cdns
            .Where(x => x.Type == PoCdnTypes.CreditNote)
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
            .Where(x => x.Type == PoCdnTypes.DebitNote)
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

            return new PoSupplierPurchaseHistoryRow
            {
                Year = m.Year,
                Month = m.Month,
                InvoiceCount = inv?.Count ?? 0,
                InvoiceTotal = invoiceTotal,
                CreditNoteCount = cn?.Count ?? 0,
                CreditNoteTotal = cnTotal,
                DebitNoteCount = dn?.Count ?? 0,
                DebitNoteTotal = dnTotal,
                NetPurchase = Money(invoiceTotal + dnTotal - cnTotal)
            };
        }).ToList();

        return IvMasterOperationResult<IReadOnlyList<PoSupplierPurchaseHistoryRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<PoSupplierPurchaseHistoryTotals>> GetSupplierPurchaseHistoryTotalsAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var history = await GetSupplierPurchaseHistoryAsync(menuCode, query, cancellationToken);
        if (!history.Succeeded)
        {
            return IvMasterOperationResult<PoSupplierPurchaseHistoryTotals>.Fail(
                history.ErrorCode, history.Message ?? "Not authorized.");
        }

        var rows = history.Data ?? [];
        return IvMasterOperationResult<PoSupplierPurchaseHistoryTotals>.Ok(new PoSupplierPurchaseHistoryTotals
        {
            InvoiceCount = rows.Sum(x => x.InvoiceCount),
            InvoiceTotal = Money(rows.Sum(x => x.InvoiceTotal)),
            CreditNoteCount = rows.Sum(x => x.CreditNoteCount),
            CreditNoteTotal = Money(rows.Sum(x => x.CreditNoteTotal)),
            DebitNoteCount = rows.Sum(x => x.DebitNoteCount),
            DebitNoteTotal = Money(rows.Sum(x => x.DebitNoteTotal)),
            NetPurchase = Money(rows.Sum(x => x.NetPurchase))
        });
    }

    // ============================ Invoice Inquiry ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoInvoiceInquiryRow>>> GetInvoiceInquiryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoInvoiceInquiryRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var invs = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        invs = ApplyDate(invs, x => x.DocDate, prepared);
        invs = ApplyText(invs, x => x.VendorCode, prepared.SuppCode);
        invs = ApplyText(invs, x => x.SalesRep, prepared.BuyerCode);
        invs = ApplyText(invs, x => x.Status, prepared.Status);
        invs = ApplyText(invs, x => x.Type, prepared.Type);

        var search = Normalize(prepared.SearchText);
        if (search is not null)
        {
            invs = invs.Where(x =>
                x.DocNo.Contains(search)
                || (x.VendorName != null && x.VendorName.Contains(search))
                || (x.ExternalDocNo != null && x.ExternalDocNo.Contains(search))
                || (x.InvNo != null && x.InvNo.Contains(search))
                || (x.Remarks != null && x.Remarks.Contains(search)));
        }

        var total = await invs.CountAsync(cancellationToken);
        var rows = await invs
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .Select(x => new PoInvoiceInquiryRow
            {
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                Type = x.Type,
                Status = x.Status,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                TotAmnt = x.TotAmnt,
                Taxes = x.Taxes,
                Currency = x.Currency,
                ExternalDocNo = x.ExternalDocNo,
                LocationCode = x.LocationCode,
                PostedDate = x.PostedDate
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<PoInquiryPage<PoInvoiceInquiryRow>>.Ok(
            new PoInquiryPage<PoInvoiceInquiryRow> { Rows = rows, TotalCount = total });
    }

    // ============================ CDN Inquiry ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoCdnInquiryRow>>> GetCdnInquiryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoCdnInquiryRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var cdns = db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        cdns = ApplyDate(cdns, x => x.DocDate, prepared);
        cdns = ApplyText(cdns, x => x.VendorCode, prepared.SuppCode);
        cdns = ApplyText(cdns, x => x.BuyerCode, prepared.BuyerCode);
        cdns = ApplyText(cdns, x => x.Status, prepared.Status);
        cdns = ApplyText(cdns, x => x.Type, prepared.Type);

        var search = Normalize(prepared.SearchText);
        if (search is not null)
        {
            cdns = cdns.Where(x =>
                x.DocNo.Contains(search)
                || (x.InvNo != null && x.InvNo.Contains(search))
                || (x.VendorName != null && x.VendorName.Contains(search))
                || (x.SupplierDocNo != null && x.SupplierDocNo.Contains(search))
                || (x.Remarks != null && x.Remarks.Contains(search)));
        }

        var total = await cdns.CountAsync(cancellationToken);
        var rows = await cdns
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .Select(x => new PoCdnInquiryRow
            {
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                Type = x.Type,
                Status = x.Status,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                InvNo = x.InvNo,
                TotAmnt = x.TotAmnt,
                ReturnStock = x.ReturnStock,
                VrBatchNo = x.VrBatchNo != null ? x.VrBatchNo.ToString() : null,
                TaxGrCode = x.TaxGrCode
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<PoInquiryPage<PoCdnInquiryRow>>.Ok(
            new PoInquiryPage<PoCdnInquiryRow> { Rows = rows, TotalCount = total });
    }

    // ============================ Document Relationships ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoDocumentRelationshipRow>>> GetDocumentRelationshipAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoDocumentRelationshipRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var all = new List<PoDocumentRelationshipRow>();

        // PR → PO via PoOrderDetail.PrNo / PrLineNo (Qty = PoPurQty).
        var prPo = await (
            from d in db.PoOrderDetails.AsNoTracking()
            join h in db.PoOrders.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.PoNo, d.PoRelNo }
                equals new { h.CompanyCode, h.BranchCode, h.PoNo, h.PoRelNo }
            where d.CompanyCode == company && d.BranchCode == branch
                  && d.PrNo != null && d.PrNo != ""
            select new PoDocumentRelationshipRow
            {
                Relation = "PR→PO",
                SourceDocNo = d.PrNo,
                TargetDocNo = d.PoNo,
                Qty = d.PoPurQty,
                RelatedPoNo = d.PoNo,
                RelatedPrNo = d.PrNo,
                DocDate = h.PoDate,
                VendCode = h.VendCode,
                VendName = h.VendName
            }).ToListAsync(cancellationToken);
        all.AddRange(prPo);

        // PO → GR via IvTrxBatchDetail.PoNo where TrxType is GR or NG.
        var poGr = await (
            from d in db.IvTrxBatchDetails.AsNoTracking()
            join b in db.IvTrxBatches.AsNoTracking()
                on d.BatchId equals b.Id
            join h in db.PoOrders.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, PoNo = d.PoNo!, Rel = d.PoRelNo ?? (short)0 }
                equals new { h.CompanyCode, h.BranchCode, PoNo = h.PoNo, Rel = h.PoRelNo }
                into poJoin
            from h in poJoin.DefaultIfEmpty()
            where d.CompanyCode == company && d.BranchCode == branch
                  && GoodsReceiptTrxTypes.Contains(d.TrxType)
                  && d.PoNo != null && d.PoNo != ""
            select new PoDocumentRelationshipRow
            {
                Relation = "PO→GR",
                SourceDocNo = d.PoNo,
                TargetDocNo = b.BatchNo.ToString(),
                Qty = d.ToPurQty ?? d.ToStdQty ?? 0m,
                RelatedPoNo = d.PoNo,
                RelatedPrNo = null,
                DocDate = b.TrxDtTime,
                VendCode = h != null ? h.VendCode : b.VendCode,
                VendName = h != null ? h.VendName : b.VendName
            }).ToListAsync(cancellationToken);
        all.AddRange(poGr);

        // PO → INV via PoInvoiceDetail.
        var poInv = await (
            from d in db.PoInvoiceDetails.AsNoTracking()
            join i in db.PoInvoices.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.DocNo }
                equals new { i.CompanyCode, i.BranchCode, i.DocNo }
            where d.CompanyCode == company && d.BranchCode == branch
                  && d.PoNo != null && d.PoNo != ""
            select new PoDocumentRelationshipRow
            {
                Relation = "PO→INV",
                SourceDocNo = d.PoNo,
                TargetDocNo = d.DocNo,
                Qty = d.Qty,
                RelatedPoNo = d.PoNo,
                RelatedPrNo = null,
                DocDate = i.DocDate,
                VendCode = i.VendorCode,
                VendName = i.VendorName
            }).ToListAsync(cancellationToken);
        all.AddRange(poInv);

        // INV → CDN via PoCdn.InvNo.
        var invCdn = await (
            from c in db.PoCdns.AsNoTracking()
            where c.CompanyCode == company && c.BranchCode == branch
                  && c.InvNo != null && c.InvNo != ""
            select new PoDocumentRelationshipRow
            {
                Relation = "INV→CDN",
                SourceDocNo = c.InvNo,
                TargetDocNo = c.DocNo,
                Qty = 0m,
                RelatedPoNo = null,
                RelatedPrNo = null,
                DocDate = c.DocDate,
                VendCode = c.VendorCode,
                VendName = c.VendorName
            }).ToListAsync(cancellationToken);
        all.AddRange(invCdn);

        // INV → QtyCN via PoInvoice Type=CN + InvNo pointing at the source INV.
        var invQtyCn = await (
            from cn in db.PoInvoices.AsNoTracking()
            where cn.CompanyCode == company && cn.BranchCode == branch
                  && cn.Type == PoInvoiceTypes.CreditNote
                  && cn.InvNo != null && cn.InvNo != ""
            select new PoDocumentRelationshipRow
            {
                Relation = "INV→QtyCN",
                SourceDocNo = cn.InvNo,
                TargetDocNo = cn.DocNo,
                Qty = cn.Details.Sum(d => d.Qty),
                RelatedPoNo = null,
                RelatedPrNo = null,
                DocDate = cn.DocDate,
                VendCode = cn.VendorCode,
                VendName = cn.VendorName
            }).ToListAsync(cancellationToken);
        all.AddRange(invQtyCn);

        var from = prepared.DateFrom?.Date;
        var toExclusive = prepared.DateTo?.Date.AddDays(1);
        var supp = Normalize(prepared.SuppCode);

        IEnumerable<PoDocumentRelationshipRow> filtered = all;
        if (from is DateTime f)
        {
            filtered = filtered.Where(x => x.DocDate >= f);
        }

        if (toExclusive is DateTime t)
        {
            filtered = filtered.Where(x => x.DocDate < t);
        }

        if (supp is not null)
        {
            filtered = filtered.Where(x => x.VendCode == supp);
        }

        var ordered = filtered
            .OrderByDescending(x => x.DocDate)
            .ThenBy(x => x.Relation)
            .ThenBy(x => x.SourceDocNo)
            .ToList();

        var page = ordered.Skip(skip).Take(take).ToList();

        return IvMasterOperationResult<PoInquiryPage<PoDocumentRelationshipRow>>.Ok(
            new PoInquiryPage<PoDocumentRelationshipRow> { Rows = page, TotalCount = ordered.Count });
    }

    // ============================ Self-billed e-Invoice ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceStatusRow>>> GetSbEInvoiceStatusAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoSbEInvoiceStatusRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var union = SbEInvoiceStatusUnion(db, company, branch, prepared);
        var preset = Normalize(prepared.WorkbenchPreset)?.ToUpperInvariant();

        // Presets need decoration before filter; without a preset keep SQL paging (Phase 1 behaviour).
        if (preset is PoInquiryWorkbenchPresets.SbNotSubmitted or PoInquiryWorkbenchPresets.SbInvalid)
        {
            var all = await union
                .OrderByDescending(x => x.DocDate)
                .ThenByDescending(x => x.DocNo)
                .ToListAsync(cancellationToken);
            await DecorateEInvoiceRowsAsync(db, company, all, cancellationToken);

            var filtered = preset == PoInquiryWorkbenchPresets.SbNotSubmitted
                ? all.Where(x => x.IsNotSubmitted).ToList()
                : all.Where(IsSbInvalidRow).ToList();
            var page = filtered.Skip(skip).Take(take).ToList();

            return IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceStatusRow>>.Ok(
                new PoInquiryPage<PoSbEInvoiceStatusRow> { Rows = page, TotalCount = filtered.Count });
        }

        var total = await union.CountAsync(cancellationToken);
        var rows = await union
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        await DecorateEInvoiceRowsAsync(db, company, rows, cancellationToken);

        return IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceStatusRow>>.Ok(
            new PoInquiryPage<PoSbEInvoiceStatusRow> { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<PoSbEInvoiceSummary>> GetSbEInvoiceSummaryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoSbEInvoiceSummary>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        // Same decoration source of truth as the grid; load key rows for the filtered date/supplier set.
        var union = SbEInvoiceStatusUnion(db, company, branch, prepared);
        var all = await union.ToListAsync(cancellationToken);
        await DecorateEInvoiceRowsAsync(db, company, all, cancellationToken);

        return IvMasterOperationResult<PoSbEInvoiceSummary>.Ok(new PoSbEInvoiceSummary
        {
            TotalCount = all.Count,
            NotSubmittedCount = all.Count(x => x.IsNotSubmitted),
            InvalidCount = all.Count(IsSbInvalidRow)
        });
    }

    /// <summary>
    /// Invalid/rejected from existing decoration: latest submission or document IRBM status.
    /// </summary>
    private static bool IsSbInvalidRow(PoSbEInvoiceStatusRow row)
    {
        var latest = EInvoiceStatuses.Normalize(row.LatestSubmissionStatus);
        var irbm = EInvoiceStatuses.Normalize(row.IrbmStatus);
        return latest is EInvoiceStatuses.Invalid or EInvoiceStatuses.Rejected
            || irbm is EInvoiceStatuses.Invalid or EInvoiceStatuses.Rejected;
    }

    private static IQueryable<PoSbEInvoiceStatusRow> SbEInvoiceStatusUnion(
        AppDbContext db,
        string company,
        string branch,
        PoInquiryQuery query)
    {
        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);
        var supp = Normalize(query.SuppCode);

        IQueryable<PoSbEInvoiceStatusRow> invs = db.PoSbInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSbEInvoiceStatusRow
            {
                DocType = EInvoiceDocumentTypes.SelfBilledInvoice,
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                IrbmStatus = x.IrbmStatus,
                IrbmUuid = x.IrbmUuid,
                IrbmSubmitId = x.IrbmSubmitId
            });
        invs = ApplyCommonSbEInvoice(invs, from, toExclusive, supp);

        IQueryable<PoSbEInvoiceStatusRow> cdns = db.PoSbCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new PoSbEInvoiceStatusRow
            {
                DocType = x.Type == PoCdnTypes.DebitNote
                    ? EInvoiceDocumentTypes.SelfBilledDebitNote
                    : EInvoiceDocumentTypes.SelfBilledCreditNote,
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                IrbmStatus = x.IrbmStatus,
                IrbmUuid = x.IrbmUuid,
                IrbmSubmitId = x.IrbmSubmitId
            });
        cdns = ApplyCommonSbEInvoice(cdns, from, toExclusive, supp);

        return invs.Concat(cdns);
    }

    private static IQueryable<PoSbEInvoiceStatusRow> ApplyCommonSbEInvoice(
        IQueryable<PoSbEInvoiceStatusRow> rows,
        DateTime? from,
        DateTime? toExclusive,
        string? supp)
    {
        if (from is DateTime f)
        {
            rows = rows.Where(x => x.DocDate >= f);
        }

        if (toExclusive is DateTime t)
        {
            rows = rows.Where(x => x.DocDate < t);
        }

        if (supp is not null)
        {
            rows = rows.Where(x => x.VendorCode == supp);
        }

        return rows;
    }

    private async Task DecorateEInvoiceRowsAsync(
        AppDbContext db,
        string company,
        List<PoSbEInvoiceStatusRow> rows,
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
                && SbEInvoiceDocTypeFilter.Contains(x.DocumentType))
            .OrderByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.DocumentType,
                x.DocumentNo,
                x.Status,
                x.DateTimeIssued,
                x.Uuid
            })
            .ToListAsync(cancellationToken);

        var latestByKey = new Dictionary<(string Type, string No), (string Status, DateTime? Issued, string? Uuid)>();
        foreach (var r in registry)
        {
            var key = (r.DocumentType!.ToUpperInvariant(), r.DocumentNo!.ToUpperInvariant());
            if (!latestByKey.ContainsKey(key))
            {
                latestByKey[key] = (r.Status, r.DateTimeIssued, r.Uuid);
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
                row.IsUnknownStatus = normalized == EInvoiceStatuses.New
                    && !string.IsNullOrWhiteSpace(latest.Status)
                    && !EInvoiceStatuses.All.Contains(latest.Status);
            }

            row.NormalizedStatus = EInvoiceStatuses.Normalize(row.IrbmStatus);
            row.StatusLabel = ResolveEInvoiceLabel(row);
        }
    }

    private static string ResolveEInvoiceLabel(PoSbEInvoiceStatusRow row)
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

    public async Task<IvMasterOperationResult<IReadOnlyList<PoSbEInvoiceSubmissionRow>>> GetSbEInvoiceSubmissionHistoryAsync(
        string menuCode,
        string docType,
        string docNo,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<IReadOnlyList<PoSbEInvoiceSubmissionRow>>(gate);
        }

        var type = (docType ?? string.Empty).Trim().ToUpperInvariant();
        var no = (docNo ?? string.Empty).Trim();
        if (type.Length == 0 || no.Length == 0)
        {
            return IvMasterOperationResult<IReadOnlyList<PoSbEInvoiceSubmissionRow>>.Fail(
                IvMasterErrorCode.Validation, "A document type and number are required.");
        }

        if (!SbEInvoiceDocTypeFilter.Contains(type))
        {
            return IvMasterOperationResult<IReadOnlyList<PoSbEInvoiceSubmissionRow>>.Fail(
                IvMasterErrorCode.Validation, $"Document type '{docType}' is not a self-billed e-Invoice type.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;

        var raw = await db.EInvDocSubmissions.AsNoTracking()
            .Where(x => x.CompanyId == company
                && x.DocumentType == type
                && x.DocumentNo == no)
            .OrderBy(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.DateTimeIssued,
                x.Status,
                x.SubmissionUuid,
                x.Uuid,
                x.LongId
            })
            .ToListAsync(cancellationToken);

        var rows = raw.Select(x => new PoSbEInvoiceSubmissionRow
        {
            Id = x.Id,
            SubmittedOn = x.DateTimeIssued,
            Status = EInvoiceStatuses.Normalize(x.Status),
            SubmissionUuid = x.SubmissionUuid,
            Uuid = x.Uuid,
            LongId = x.LongId
        }).ToList();

        return IvMasterOperationResult<IReadOnlyList<PoSbEInvoiceSubmissionRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceReconciliationRow>>> GetSbEInvoiceReconciliationAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoSbEInvoiceReconciliationRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var union = SbEInvoiceStatusUnion(db, company, branch, prepared);

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
            var finding = ReconciliationFinding(
                erpStatus, x.IrbmUuid, x.LatestSubmissionStatus, x.IsNotSubmitted, x.IsUnknownStatus);

            return new PoSbEInvoiceReconciliationRow
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

        return IvMasterOperationResult<PoInquiryPage<PoSbEInvoiceReconciliationRow>>.Ok(
            new PoInquiryPage<PoSbEInvoiceReconciliationRow> { Rows = rows, TotalCount = total });
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

    private static (int Skip, int Take) Page(PoInquiryQuery query)
    {
        var skip = Math.Max(0, query.Skip);
        var take = query.Take <= 0 ? DefaultPageSize : Math.Min(query.Take, MaxExportRows);
        return (skip, take);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal Money(decimal value) => PoOrderCalc.RoundMoney(value);

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
        PoInquiryQuery query)
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

    private static IQueryable<T> ApplyNullableDate<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTime?>> selector,
        PoInquiryQuery query)
    {
        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);

        if (from is DateTime f)
        {
            var parameter = selector.Parameters[0];
            var body = Expression.GreaterThanOrEqual(
                Expression.Property(selector.Body, nameof(Nullable<DateTime>.Value)),
                Expression.Constant(f));
            var notNull = Expression.Property(selector.Body, nameof(Nullable<DateTime>.HasValue));
            var and = Expression.AndAlso(notNull, body);
            source = source.Where(Expression.Lambda<Func<T, bool>>(and, parameter));
        }

        if (toExclusive is DateTime t)
        {
            var parameter = selector.Parameters[0];
            var body = Expression.LessThan(
                Expression.Property(selector.Body, nameof(Nullable<DateTime>.Value)),
                Expression.Constant(t));
            var notNull = Expression.Property(selector.Body, nameof(Nullable<DateTime>.HasValue));
            var and = Expression.AndAlso(notNull, body);
            source = source.Where(Expression.Lambda<Func<T, bool>>(and, parameter));
        }

        return source;
    }

    private static Expression<Func<T, bool>> BuildEquals<T>(
        Expression<Func<T, string?>> selector,
        string value)
    {
        var parameter = selector.Parameters[0];
        var body = Expression.Equal(selector.Body, Expression.Constant(value, typeof(string)));
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static Expression<Func<T, bool>> BuildCompare<T, TValue>(
        Expression<Func<T, TValue>> selector,
        ExpressionType comparison,
        TValue value)
    {
        var parameter = selector.Parameters[0];
        var body = Expression.MakeBinary(comparison, selector.Body, Expression.Constant(value, typeof(TValue)));
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }
}
