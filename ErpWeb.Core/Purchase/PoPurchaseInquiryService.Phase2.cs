using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

/// <summary>Purchase Inquiry Phase 2 — Price History, Matching/RNI, Delivery Performance.</summary>
public sealed partial class PoPurchaseInquiryService
{
    private const int PriceDecimals = 6;

    // ============================ Purchase Price History ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoPurchasePriceHistoryRow>>> GetPurchasePriceHistoryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoPurchasePriceHistoryRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var slice =
            from h in db.PoInvoices.AsNoTracking()
            from l in h.Details
            where h.CompanyCode == company
                  && h.BranchCode == branch
                  && h.Status == PoInvoiceStatuses.Posted
                  && h.Type == PoInvoiceTypes.Invoice
            select new { h, l };

        slice = ApplyDate(slice, x => x.h.DocDate, prepared);
        slice = ApplyText(slice, x => x.h.VendorCode, prepared.SuppCode);
        slice = ApplyText(slice, x => x.l.ICode, prepared.ItemCode);
        slice = ApplyText(slice, x => x.h.Currency, prepared.Currency);

        var total = await slice.CountAsync(cancellationToken);
        var materialised = await slice
            .OrderByDescending(x => x.h.DocDate)
            .ThenByDescending(x => x.h.DocNo)
            .ThenByDescending(x => x.l.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new
            {
                x.h.DocDate,
                x.h.DocNo,
                x.l.Line,
                x.h.VendorCode,
                x.h.VendorName,
                x.l.ICode,
                x.l.IDesc,
                x.l.PoNo,
                x.l.PoRelNo,
                x.l.Qty,
                x.l.StdUom,
                x.l.SellingUom,
                x.l.UnitPrice,
                x.l.Amount,
                x.l.NetAmount,
                x.l.TaxAmt,
                x.l.IsInclusive,
                x.h.Currency,
                x.h.CurrRate
            })
            .ToListAsync(cancellationToken);

        var rows = materialised.Select(x => MapPriceHistoryRow(
            x.DocDate, x.DocNo, x.Line, x.VendorCode, x.VendorName, x.ICode, x.IDesc,
            x.PoNo, x.PoRelNo, x.Qty, x.StdUom, x.SellingUom, x.UnitPrice, x.Amount,
            x.NetAmount, x.TaxAmt, x.IsInclusive, x.Currency, x.CurrRate)).ToList();

        return IvMasterOperationResult<PoInquiryPage<PoPurchasePriceHistoryRow>>.Ok(
            new PoInquiryPage<PoPurchasePriceHistoryRow> { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<PoPurchasePriceHistorySummary>> GetPurchasePriceHistorySummaryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoPurchasePriceHistorySummary>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var slice =
            from h in db.PoInvoices.AsNoTracking()
            from l in h.Details
            where h.CompanyCode == company
                  && h.BranchCode == branch
                  && h.Status == PoInvoiceStatuses.Posted
                  && h.Type == PoInvoiceTypes.Invoice
                  && l.Qty != 0m
            select new { h, l };

        slice = ApplyDate(slice, x => x.h.DocDate, prepared);
        slice = ApplyText(slice, x => x.h.VendorCode, prepared.SuppCode);
        slice = ApplyText(slice, x => x.l.ICode, prepared.ItemCode);
        slice = ApplyText(slice, x => x.h.Currency, prepared.Currency);

        var ordered = await slice
            .OrderByDescending(x => x.h.DocDate)
            .ThenByDescending(x => x.h.DocNo)
            .ThenByDescending(x => x.l.Line)
            .Select(x => new { x.l.Qty, x.l.NetAmount })
            .ToListAsync(cancellationToken);

        var prices = ordered
            .Select(x => x.NetAmount / x.Qty)
            .ToList();

        var summary = new PoPurchasePriceHistorySummary
        {
            SampleCount = prices.Count,
            LatestNetUnitPrice = prices.Count > 0 ? prices[0] : null,
            PreviousNetUnitPrice = prices.Count > 1 ? prices[1] : null,
            MinNetUnitPrice = prices.Count > 0 ? prices.Min() : null,
            MaxNetUnitPrice = prices.Count > 0 ? prices.Max() : null,
            AvgNetUnitPrice = prices.Count > 0 ? prices.Average() : null
        };

        return IvMasterOperationResult<PoPurchasePriceHistorySummary>.Ok(summary);
    }

    /// <summary>
    /// ItemDiscAmount = Amount − PostDiscountBase where
    /// PostDiscountBase = IsInclusive ? NetAmount+TaxAmt : NetAmount.
    /// </summary>
    internal static decimal ComputeItemDiscAmount(
        decimal amount,
        decimal netAmount,
        decimal taxAmt,
        bool isInclusive) =>
        PoOrderCalc.RoundMoney(
            amount - (isInclusive
                ? PoOrderCalc.RoundMoney(netAmount + taxAmt)
                : PoOrderCalc.RoundMoney(netAmount)));

    internal static decimal? ComputeLocalAmount(decimal netAmount, decimal currRate) =>
        currRate > 0m ? PoOrderCalc.RoundMoney(netAmount * currRate) : null;

    private static PoPurchasePriceHistoryRow MapPriceHistoryRow(
        DateTime docDate,
        string docNo,
        short line,
        string? vendorCode,
        string? vendorName,
        string? iCode,
        string? iDesc,
        string? poNo,
        short? poRelNo,
        decimal qty,
        string? stdUom,
        string? sellingUom,
        decimal unitPrice,
        decimal amount,
        decimal netAmount,
        decimal taxAmt,
        bool isInclusive,
        string? currency,
        decimal currRate) =>
        new()
        {
            DocDate = docDate,
            DocNo = docNo,
            Line = line,
            VendorCode = vendorCode,
            VendorName = vendorName,
            ICode = iCode,
            IDesc = iDesc,
            PoNo = poNo,
            PoRelNo = poRelNo,
            Qty = qty,
            Uom = !string.IsNullOrWhiteSpace(stdUom) ? stdUom : sellingUom,
            UnitPrice = unitPrice,
            ItemDiscAmount = ComputeItemDiscAmount(amount, netAmount, taxAmt, isInclusive),
            NetAmount = netAmount,
            NetUnitPrice = qty != 0m ? netAmount / qty : null,
            Currency = currency,
            CurrRate = currRate,
            LocalAmount = ComputeLocalAmount(netAmount, currRate)
        };

    // ============================ PO Matching ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoMatchingRow>>> GetPoMatchingAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoMatchingRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var spine = await BuildMatchingSpineAsync(db, company, branch, prepared, cancellationToken);
        var preset = Normalize(prepared.WorkbenchPreset)
                     ?? PoInquiryWorkbenchPresets.ReceivedNotInvoiced;

        // Qty-only presets: filter spine first, then aggregate invoice/GR for the page.
        // Status presets that need invoice data: map all, filter by MatchingStatus, then page.
        var needsFullMap = string.Equals(preset, PoInquiryWorkbenchPresets.FullyMatched, StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, PoInquiryWorkbenchPresets.PriceMismatch, StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset, PoInquiryWorkbenchPresets.All, StringComparison.OrdinalIgnoreCase);

        List<PoMatchingRow> rows;
        int total;

        if (needsFullMap)
        {
            var keys = spine.Select(x => (x.PoNo, x.PoRelNo, x.Line)).ToList();
            var invAggs = await LoadInvoiceAggregatesAsync(db, company, branch, keys, cancellationToken);
            var grAggs = await LoadGrAggregatesAsync(db, company, branch, keys, cancellationToken);
            var vendorTols = await LoadVendorPriceTolerancesAsync(db, company, branch, spine, cancellationToken);
            var mapped = spine.Select(s =>
            {
                invAggs.TryGetValue((s.PoNo, s.PoRelNo, s.Line), out var inv);
                grAggs.TryGetValue((s.PoNo, s.PoRelNo, s.Line), out var gr);
                vendorTols.TryGetValue((NormalizeKey(s.VendCode), NormalizeKey(s.ICode)), out var vendorTol);
                return MapMatchingRow(s, inv, gr, vendorTol);
            }).ToList();

            if (string.Equals(preset, PoInquiryWorkbenchPresets.FullyMatched, StringComparison.OrdinalIgnoreCase))
            {
                mapped = mapped.Where(x => x.MatchingStatus == PoMatchingStatuses.FullyMatched).ToList();
            }
            else if (string.Equals(preset, PoInquiryWorkbenchPresets.PriceMismatch, StringComparison.OrdinalIgnoreCase))
            {
                mapped = mapped.Where(x => x.MatchingStatus == PoMatchingStatuses.PriceMismatch).ToList();
            }

            total = mapped.Count;
            rows = mapped
                .OrderByDescending(x => x.PoDate)
                .ThenByDescending(x => x.PoNo)
                .ThenBy(x => x.Line)
                .Skip(skip)
                .Take(take)
                .ToList();
        }
        else
        {
            var filtered = ApplyMatchingQtyPreset(spine, preset);
            total = filtered.Count;
            var page = filtered
                .OrderByDescending(x => x.PoDate)
                .ThenByDescending(x => x.PoNo)
                .ThenBy(x => x.Line)
                .Skip(skip)
                .Take(take)
                .ToList();

            if (page.Count == 0)
            {
                rows = [];
            }
            else
            {
                var keys = page.Select(x => (x.PoNo, x.PoRelNo, x.Line)).ToList();
                var invAggs = await LoadInvoiceAggregatesAsync(db, company, branch, keys, cancellationToken);
                var grAggs = await LoadGrAggregatesAsync(db, company, branch, keys, cancellationToken);
                var vendorTols = await LoadVendorPriceTolerancesAsync(db, company, branch, page, cancellationToken);
                rows = page.Select(spineRow =>
                {
                    invAggs.TryGetValue((spineRow.PoNo, spineRow.PoRelNo, spineRow.Line), out var inv);
                    grAggs.TryGetValue((spineRow.PoNo, spineRow.PoRelNo, spineRow.Line), out var gr);
                    vendorTols.TryGetValue(
                        (NormalizeKey(spineRow.VendCode), NormalizeKey(spineRow.ICode)),
                        out var vendorTol);
                    return MapMatchingRow(spineRow, inv, gr, vendorTol);
                }).ToList();
            }
        }

        return IvMasterOperationResult<PoInquiryPage<PoMatchingRow>>.Ok(
            new PoInquiryPage<PoMatchingRow> { Rows = rows, TotalCount = total });
    }

    public async Task<IvMasterOperationResult<PoMatchingSummary>> GetPoMatchingSummaryAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoMatchingSummary>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        prepared.WorkbenchPreset = null;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var spine = await BuildMatchingSpineAsync(db, company, branch, prepared, cancellationToken);
        // Summary status counts need invoice aggregates for PRICE_MISMATCH — load for all spine rows.
        // Cap via paging is not applied to summary chips (same as Outstanding).
        var keys = spine.Select(x => (x.PoNo, x.PoRelNo, x.Line)).ToList();
        var invAggs = keys.Count == 0
            ? new Dictionary<(string, short, short), InvoiceAgg>()
            : await LoadInvoiceAggregatesAsync(db, company, branch, keys, cancellationToken);
        var vendorTols = await LoadVendorPriceTolerancesAsync(db, company, branch, spine, cancellationToken);

        var summary = new PoMatchingSummary();
        foreach (var s in spine)
        {
            invAggs.TryGetValue((s.PoNo, s.PoRelNo, s.Line), out var inv);
            vendorTols.TryGetValue((NormalizeKey(s.VendCode), NormalizeKey(s.ICode)), out var vendorTol);
            var row = MapMatchingRow(s, inv, null, vendorTol);
            switch (row.MatchingStatus)
            {
                case PoMatchingStatuses.ReceivedNotInvoiced:
                    summary.ReceivedNotInvoicedCount++;
                    break;
                case PoMatchingStatuses.PoOutstanding:
                    summary.PoOutstandingCount++;
                    break;
                case PoMatchingStatuses.PartiallyMatched:
                    summary.PartiallyMatchedCount++;
                    break;
                case PoMatchingStatuses.FullyMatched:
                    summary.FullyMatchedCount++;
                    break;
                case PoMatchingStatuses.PriceMismatch:
                    summary.PriceMismatchCount++;
                    break;
                case PoMatchingStatuses.InvoicedNotReceived:
                    summary.InvoicedNotReceivedCount++;
                    break;
            }
        }

        return IvMasterOperationResult<PoMatchingSummary>.Ok(summary);
    }

    private sealed class MatchingSpineRow
    {
        public string PoNo { get; init; } = string.Empty;
        public short PoRelNo { get; init; }
        public DateTime? PoDate { get; init; }
        public string? VendCode { get; init; }
        public string? VendName { get; init; }
        public string? Buyer { get; init; }
        public string? CurCode { get; init; }
        public short Line { get; init; }
        public string? ICode { get; init; }
        public string? IDesc { get; init; }
        public string? PurchaseUom { get; init; }
        public decimal PoUnitPrice { get; init; }
        public decimal PoPurQty { get; init; }
        public decimal RecvQty { get; init; }
        public decimal ReturnQty { get; init; }
        public decimal InvoicedQty { get; init; }
        public decimal BalanceQty { get; init; }
        public decimal NetAmount { get; init; }
        public DateTime? EtaDate { get; init; }
        public string? ToWarehouse { get; init; }
        public decimal? InvoicePriceTolerance { get; set; }
    }

    private sealed class InvoiceAgg
    {
        public decimal InvoiceAmount { get; init; }
        public decimal InvoiceQtyPosted { get; init; }
        public int DistinctDocCount { get; init; }
        public bool HasMultipleInvoicePrices { get; init; }
        public string? LatestInvoiceDocNo { get; init; }
        public string? LatestInvoiceCurrency { get; init; }
        public decimal? LatestInvoicePriceTolerance { get; init; }
    }

    private sealed class GrAgg
    {
        public int DistinctBatchCount { get; init; }
        public int? SingleBatchNo { get; init; }
    }

    private async Task<List<MatchingSpineRow>> BuildMatchingSpineAsync(
        AppDbContext db,
        string company,
        string branch,
        PoInquiryQuery prepared,
        CancellationToken cancellationToken)
    {
        var maxRel =
            from o in db.PoOrders.AsNoTracking()
            where o.CompanyCode == company && o.BranchCode == branch
            group o by o.PoNo into g
            select new { PoNo = g.Key, MaxRel = g.Max(x => x.PoRelNo) };

        var query =
            from h in db.PoOrders.AsNoTracking()
            join m in maxRel
                on new { h.PoNo, h.PoRelNo } equals new { m.PoNo, PoRelNo = m.MaxRel }
            from line in h.Details
            where h.CompanyCode == company
                  && h.BranchCode == branch
                  && h.Status != PoOrderStatuses.Cancelled
            select new { h, line };

        query = ApplyNullableDate(query, x => x.h.PoDate, prepared);
        query = ApplyText(query, x => x.h.VendCode, prepared.SuppCode);
        query = ApplyText(query, x => x.h.Buyer, prepared.BuyerCode);
        query = ApplyText(query, x => x.line.ICode, prepared.ItemCode);
        query = ApplyText(query, x => x.line.ToWarehouse, prepared.Warehouse);

        var rows = await query
            .Select(x => new MatchingSpineRow
            {
                PoNo = x.h.PoNo,
                PoRelNo = x.h.PoRelNo,
                PoDate = x.h.PoDate,
                VendCode = x.h.VendCode,
                VendName = x.h.VendName,
                Buyer = x.h.Buyer,
                CurCode = x.h.CurCode,
                Line = x.line.Line,
                ICode = x.line.ICode,
                IDesc = x.line.IDesc,
                PurchaseUom = x.line.PurchaseUom,
                PoUnitPrice = x.line.PoUnitPrice,
                PoPurQty = x.line.PoPurQty,
                RecvQty = x.line.RecvQty,
                ReturnQty = x.line.ReturnQty,
                InvoicedQty = x.line.InvoicedQty,
                BalanceQty = x.line.BalanceQty,
                NetAmount = x.line.NetAmount,
                EtaDate = x.line.EtaDate,
                ToWarehouse = x.line.ToWarehouse
            })
            .ToListAsync(cancellationToken);

        return rows;
    }

    private static List<MatchingSpineRow> ApplyMatchingQtyPreset(
        List<MatchingSpineRow> spine,
        string preset)
    {
        if (string.Equals(preset, PoInquiryWorkbenchPresets.ReceivedNotInvoiced, StringComparison.OrdinalIgnoreCase))
        {
            return spine.Where(x =>
                PoOrderCalc.ComputeInvoiceable(x.RecvQty, x.ReturnQty, x.InvoicedQty) > 0m).ToList();
        }

        if (string.Equals(preset, PoInquiryWorkbenchPresets.PoOutstanding, StringComparison.OrdinalIgnoreCase))
        {
            return spine.Where(x => x.BalanceQty > 0m && x.RecvQty == 0m).ToList();
        }

        if (string.Equals(preset, PoInquiryWorkbenchPresets.PartiallyMatched, StringComparison.OrdinalIgnoreCase))
        {
            return spine.Where(x => x.BalanceQty > 0m && x.RecvQty > 0m).ToList();
        }

        if (string.Equals(preset, PoInquiryWorkbenchPresets.InvoicedNotReceived, StringComparison.OrdinalIgnoreCase))
        {
            return spine.Where(x =>
                PoOrderCalc.ComputeOverInvoiced(x.RecvQty, x.ReturnQty, x.InvoicedQty) > 0m).ToList();
        }

        return spine;
    }

    private async Task<Dictionary<(string PoNo, short Rel, short Line), InvoiceAgg>> LoadInvoiceAggregatesAsync(
        AppDbContext db,
        string company,
        string branch,
        List<(string PoNo, short Rel, short Line)> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<(string, short, short), InvoiceAgg>();
        }

        var poNos = keys.Select(x => x.PoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var lines = await (
            from d in db.PoInvoiceDetails.AsNoTracking()
            join h in db.PoInvoices.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.DocNo }
                equals new { h.CompanyCode, h.BranchCode, h.DocNo }
            where d.CompanyCode == company
                  && d.BranchCode == branch
                  && h.Status == PoInvoiceStatuses.Posted
                  && h.Type == PoInvoiceTypes.Invoice
                  && d.PoNo != null
                  && poNos.Contains(d.PoNo)
            select new
            {
                PoNo = d.PoNo!,
                Rel = d.PoRelNo ?? (short)0,
                Line = d.PoLineNo ?? (short)0,
                d.Qty,
                d.NetAmount,
                d.UnitPrice,
                h.DocNo,
                h.DocDate,
                h.Currency,
                h.PriceTolerance
            }).ToListAsync(cancellationToken);

        var keySet = keys.ToHashSet();
        return lines
            .Where(x => keySet.Contains((x.PoNo, x.Rel, x.Line)))
            .GroupBy(x => (x.PoNo, x.Rel, x.Line))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var list = g.ToList();
                    var roundedPrices = list
                        .Select(x => PoOrderCalc.RoundPrice(x.UnitPrice, PriceDecimals))
                        .Distinct()
                        .ToList();
                    var latest = list
                        .OrderByDescending(x => x.DocDate)
                        .ThenByDescending(x => x.DocNo)
                        .First();
                    return new InvoiceAgg
                    {
                        InvoiceAmount = list.Sum(x => x.NetAmount),
                        InvoiceQtyPosted = list.Sum(x => x.Qty),
                        DistinctDocCount = list.Select(x => x.DocNo).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        HasMultipleInvoicePrices = roundedPrices.Count >= 2,
                        LatestInvoiceDocNo = latest.DocNo,
                        LatestInvoiceCurrency = latest.Currency,
                        LatestInvoicePriceTolerance = latest.PriceTolerance
                    };
                });
    }

    private async Task<Dictionary<(string PoNo, short Rel, short Line), GrAgg>> LoadGrAggregatesAsync(
        AppDbContext db,
        string company,
        string branch,
        List<(string PoNo, short Rel, short Line)> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<(string, short, short), GrAgg>();
        }

        var poNos = keys.Select(x => x.PoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var lines = await (
            from d in db.IvTrxBatchDetails.AsNoTracking()
            join b in db.IvTrxBatches.AsNoTracking() on d.BatchId equals b.Id
            where d.CompanyCode == company
                  && d.BranchCode == branch
                  && GoodsReceiptTrxTypes.Contains(d.TrxType)
                  && d.PoNo != null
                  && poNos.Contains(d.PoNo)
            select new
            {
                PoNo = d.PoNo!,
                Rel = d.PoRelNo ?? (short)0,
                Line = d.PoLineNo ?? (short)0,
                b.BatchNo
            }).ToListAsync(cancellationToken);

        var keySet = keys.ToHashSet();
        return lines
            .Where(x => keySet.Contains((x.PoNo, x.Rel, x.Line)))
            .GroupBy(x => (x.PoNo, x.Rel, x.Line))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var batches = g.Select(x => x.BatchNo).Distinct().ToList();
                    return new GrAgg
                    {
                        DistinctBatchCount = batches.Count,
                        SingleBatchNo = batches.Count == 1 ? batches[0] : null
                    };
                });
    }

    private static async Task<Dictionary<(string Vendor, string ICode), decimal>> LoadVendorPriceTolerancesAsync(
        AppDbContext db,
        string company,
        string branch,
        List<MatchingSpineRow> page,
        CancellationToken cancellationToken)
    {
        var pairs = page
            .Select(x => (Vendor: NormalizeKey(x.VendCode), ICode: NormalizeKey(x.ICode)))
            .Where(x => x.Vendor.Length > 0 && x.ICode.Length > 0)
            .Distinct()
            .ToList();

        if (pairs.Count == 0)
        {
            return new Dictionary<(string, string), decimal>();
        }

        var vendors = pairs.Select(x => x.Vendor).Distinct().ToList();
        var items = pairs.Select(x => x.ICode).Distinct().ToList();

        var rows = await db.PoVendorByItems.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.Vendor != null
                        && x.ICode != null
                        && vendors.Contains(x.Vendor)
                        && items.Contains(x.ICode)
                        && (x.BranchCode == branch || x.BranchCode == null || x.BranchCode == string.Empty))
            .Select(x => new { x.Vendor, x.ICode, x.BranchCode, x.PriceTolerance, x.PurUom })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<(string, string), decimal>();
        foreach (var pair in pairs)
        {
            var spine = page.First(x =>
                NormalizeKey(x.VendCode) == pair.Vendor && NormalizeKey(x.ICode) == pair.ICode);
            var purUom = (spine.PurchaseUom ?? string.Empty).Trim();
            var match = rows
                .Where(x => string.Equals(x.Vendor, pair.Vendor, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.ICode, pair.ICode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => string.Equals(x.BranchCode, branch, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => purUom.Length > 0
                    && string.Equals(x.PurUom, purUom, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            result[pair] = match?.PriceTolerance is decimal t
                ? (t < 0m ? 0m : (t > 100m ? 100m : t))
                : 0m;
        }

        return result;
    }

    private static PoMatchingRow MapMatchingRow(
        MatchingSpineRow spine,
        InvoiceAgg? inv,
        GrAgg? gr,
        decimal vendorTol)
    {
        var netReceived = PoOrderCalc.ComputeNetReceived(spine.RecvQty, spine.ReturnQty);
        var invoiceable = PoOrderCalc.ComputeInvoiceable(spine.RecvQty, spine.ReturnQty, spine.InvoicedQty);
        var overInvoiced = PoOrderCalc.ComputeOverInvoiced(spine.RecvQty, spine.ReturnQty, spine.InvoicedQty);
        var receivedAmount = spine.PoPurQty > 0m
            ? PoOrderCalc.RoundMoney(spine.NetAmount * netReceived / spine.PoPurQty)
            : 0m;

        decimal? effectivePrice = null;
        if (inv is { InvoiceQtyPosted: not 0m })
        {
            effectivePrice = inv.InvoiceAmount / inv.InvoiceQtyPosted;
        }

        var currencyOk = CurrenciesEqual(spine.CurCode, inv?.LatestInvoiceCurrency);
        var canCompare = currencyOk && effectivePrice is not null;
        var effectiveTol = PoToleranceLookup.EffectivePriceTolerance(
            inv?.LatestInvoicePriceTolerance, vendorTol);

        var priceMismatch = false;
        decimal? priceVariance = null;
        decimal? priceVariancePct = null;
        if (canCompare)
        {
            priceMismatch = !PoOrderCalc.ValidatePriceTolerance(
                spine.PoUnitPrice,
                effectivePrice!.Value,
                effectiveTol,
                PriceDecimals,
                out _);
            priceVariance = PoOrderCalc.RoundPrice(effectivePrice.Value - spine.PoUnitPrice, PriceDecimals);
            if (spine.PoUnitPrice != 0m)
            {
                priceVariancePct = Math.Abs(effectivePrice.Value - spine.PoUnitPrice) / spine.PoUnitPrice * 100m;
            }
        }

        var status = ResolveMatchingStatus(
            overInvoiced, priceMismatch && canCompare, invoiceable, spine.BalanceQty, spine.RecvQty, spine.PoPurQty);

        return new PoMatchingRow
        {
            PoNo = spine.PoNo,
            PoRelNo = spine.PoRelNo,
            PoDate = spine.PoDate,
            VendCode = spine.VendCode,
            VendName = spine.VendName,
            Buyer = spine.Buyer,
            CurCode = spine.CurCode,
            Line = spine.Line,
            ICode = spine.ICode,
            IDesc = spine.IDesc,
            PurchaseUom = spine.PurchaseUom,
            PoUnitPrice = spine.PoUnitPrice,
            PoPurQty = spine.PoPurQty,
            RecvQty = spine.RecvQty,
            ReturnQty = spine.ReturnQty,
            NetReceivedQty = netReceived,
            InvoicedQty = spine.InvoicedQty,
            InvoiceableQty = invoiceable,
            OverInvoicedQty = overInvoiced,
            BalanceQty = spine.BalanceQty,
            PoAmount = spine.NetAmount,
            ReceivedAmount = receivedAmount,
            InvoiceAmount = inv?.InvoiceAmount ?? 0m,
            InvoiceQtyPosted = inv?.InvoiceQtyPosted ?? 0m,
            EffectiveInvoiceUnitPrice = effectivePrice,
            QtyVariance = netReceived - spine.InvoicedQty,
            PriceVariance = priceVariance,
            PriceVariancePct = priceVariancePct,
            MatchingStatus = status,
            PriceMismatch = priceMismatch,
            OverInvoiced = overInvoiced > 0m,
            HasMultipleInvoices = (inv?.DistinctDocCount ?? 0) > 1,
            HasMultipleInvoicePrices = inv?.HasMultipleInvoicePrices ?? false,
            HasMultipleGRs = (gr?.DistinctBatchCount ?? 0) > 1,
            LatestInvoiceDocNo = inv?.LatestInvoiceDocNo,
            SingleGrBatchNo = gr?.SingleBatchNo,
            EtaDate = spine.EtaDate,
            ToWarehouse = spine.ToWarehouse
        };
    }

    internal static string ResolveMatchingStatus(
        decimal overInvoicedQty,
        bool priceMismatch,
        decimal invoiceableQty,
        decimal balanceQty,
        decimal recvQty,
        decimal poPurQty)
    {
        if (overInvoicedQty > 0m)
        {
            return PoMatchingStatuses.InvoicedNotReceived;
        }

        if (priceMismatch)
        {
            return PoMatchingStatuses.PriceMismatch;
        }

        if (invoiceableQty > 0m)
        {
            return PoMatchingStatuses.ReceivedNotInvoiced;
        }

        if (balanceQty > 0m && recvQty == 0m)
        {
            return PoMatchingStatuses.PoOutstanding;
        }

        if (balanceQty > 0m && recvQty > 0m)
        {
            return PoMatchingStatuses.PartiallyMatched;
        }

        if (balanceQty == 0m && invoiceableQty == 0m && overInvoicedQty == 0m)
        {
            return PoMatchingStatuses.FullyMatched;
        }

        if (poPurQty <= 0m)
        {
            return PoMatchingStatuses.Exception;
        }

        return PoMatchingStatuses.Exception;
    }

    private static bool CurrenciesEqual(string? a, string? b)
    {
        var left = (a ?? string.Empty).Trim();
        var right = (b ?? string.Empty).Trim();
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string? value) => (value ?? string.Empty).Trim();

    // ============================ Delivery Performance ============================

    public async Task<IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceRow>>> GetSupplierDeliveryPerformanceAsync(
        string menuCode,
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<PoInquiryPage<PoDeliveryPerformanceRow>>(gate);
        }

        var prepared = query ?? new PoInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var maxRel =
            from o in db.PoOrders.AsNoTracking()
            where o.CompanyCode == company && o.BranchCode == branch
            group o by o.PoNo into g
            select new { PoNo = g.Key, MaxRel = g.Max(x => x.PoRelNo) };

        var lines =
            from h in db.PoOrders.AsNoTracking()
            join m in maxRel
                on new { h.PoNo, h.PoRelNo } equals new { m.PoNo, PoRelNo = m.MaxRel }
            from line in h.Details
            where h.CompanyCode == company
                  && h.BranchCode == branch
                  && h.Status != PoOrderStatuses.Cancelled
                  && line.EtaDate != null
                  && line.RecvDate != null
                  && line.RecvQty > 0m
            select new { h, line };

        lines = ApplyNullableDate(lines, x => x.h.PoDate, prepared);
        lines = ApplyText(lines, x => x.h.VendCode, prepared.SuppCode);
        lines = ApplyText(lines, x => x.h.Buyer, prepared.BuyerCode);
        lines = ApplyText(lines, x => x.line.ICode, prepared.ItemCode);
        lines = ApplyText(lines, x => x.line.ToWarehouse, prepared.Warehouse);

        var evaluated = await lines
            .Select(x => new
            {
                x.h.VendCode,
                x.h.VendName,
                x.h.PoNo,
                x.line.PoPurQty,
                x.line.RecvQty,
                Eta = x.line.EtaDate!.Value,
                Recv = x.line.RecvDate!.Value
            })
            .ToListAsync(cancellationToken);

        var grouped = evaluated
            .GroupBy(x => NormalizeKey(x.VendCode), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var onTime = g.Count(x => x.Recv.Date <= x.Eta.Date);
                var lateLines = g.Where(x => x.Recv.Date > x.Eta.Date).ToList();
                var late = lateLines.Count;
                var daysLate = lateLines
                    .Select(x => Math.Max(0, (x.Recv.Date - x.Eta.Date).Days))
                    .ToList();
                var name = g.Select(x => x.VendName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                           ?? g.First().VendName;
                return new PoDeliveryPerformanceRow
                {
                    SuppCode = g.Key.Length == 0 ? null : g.Key,
                    SuppName = name,
                    PurchaseOrderCount = g.Select(x => x.PoNo).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    PoLinesEvaluated = g.Count(),
                    OrderedQty = g.Sum(x => x.PoPurQty),
                    ReceivedQty = g.Sum(x => x.RecvQty),
                    OnTimeLines = onTime,
                    LateLines = late,
                    OnTimePct = (onTime + late) > 0
                        ? (decimal)onTime / (onTime + late) * 100m
                        : null,
                    AvgDaysLate = daysLate.Count > 0 ? (decimal)daysLate.Average() : null,
                    MaxDaysLate = daysLate.Count > 0 ? daysLate.Max() : null
                };
            })
            .OrderBy(x => x.SuppCode)
            .ToList();

        var total = grouped.Count;
        var pageRows = grouped.Skip(skip).Take(take).ToList();

        return IvMasterOperationResult<PoInquiryPage<PoDeliveryPerformanceRow>>.Ok(
            new PoInquiryPage<PoDeliveryPerformanceRow> { Rows = pageRows, TotalCount = total });
    }
}
