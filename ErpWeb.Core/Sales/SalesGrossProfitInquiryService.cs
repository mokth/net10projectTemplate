using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Combines posted invoice net sales with exact outbound valuation facts and active customer-return
/// allocations. The return allocation chain is collapsed before it reduces COGS, so a rollback does
/// not double-count the original return.
/// </summary>
public sealed class SalesGrossProfitInquiryService : ISalesGrossProfitInquiryService
{
    private const decimal Epsilon = 0.0000005m;
    private const int MaxPageSize = 5000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public SalesGrossProfitInquiryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<SalesGrossProfitInquiryPage> SearchAsync(
        SalesGrossProfitInquiryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope()
                    ?? throw new InvalidOperationException("A trusted company/branch scope is required.");
        query ??= new SalesGrossProfitInquiryQuery();

        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);
        var invoiceNo = Normalize(query.InvoiceNo);
        var customerCode = Normalize(query.CustomerCode);
        var itemCode = Normalize(query.ItemCode);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoiceQuery = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.Status == SaInvoiceStatuses.Posted);
        if (from is DateTime fromDate)
            invoiceQuery = invoiceQuery.Where(x => x.InvDate >= fromDate);
        if (toExclusive is DateTime toDate)
            invoiceQuery = invoiceQuery.Where(x => x.InvDate < toDate);
        if (invoiceNo is not null)
            invoiceQuery = invoiceQuery.Where(x => x.InvNo == invoiceNo);
        if (customerCode is not null)
            invoiceQuery = invoiceQuery.Where(x => x.CustCode == customerCode);

        var invoices = await invoiceQuery
            .Select(x => new InvoiceProjection(
                x.InvNo,
                x.InvDate,
                x.CustCode,
                x.CustName,
                x.CurrRate))
            .ToListAsync(cancellationToken);
        if (invoices.Count == 0)
            return EmptyPage();

        var invoiceNos = invoices.Select(x => x.InvoiceNo).ToArray();
        var invoiceByNo = invoices.ToDictionary(x => x.InvoiceNo, StringComparer.OrdinalIgnoreCase);
        var lines = await db.SaInvoiceDetails.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && invoiceNos.Contains(x.InvNo)
                        && x.StdQty > 0m
                        && x.ICode != null
                        && x.ICode != string.Empty)
            .Select(x => new InvoiceLineProjection(
                x.InvNo,
                x.Line,
                x.ICode!,
                x.StdQty,
                x.LocalAmount,
                x.NetAmount,
                x.LinkDo,
                x.DoNo,
                x.DoLine,
                x.StockControl))
            .ToListAsync(cancellationToken);
        if (itemCode is not null)
            lines = lines
                .Where(x => string.Equals(x.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var ownerNos = lines
            .Select(x => x.LinkDo ? x.DoNo : x.InvoiceNo)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var itemCodes = lines.Select(x => x.ItemCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var facts = ownerNos.Length == 0 || itemCodes.Length == 0
            ? []
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                            && x.BranchCode == scope.BranchCode
                            && x.Direction == -1
                            && x.ValuationStatus == StockValuationStatuses.Valued
                            && ownerNos.Contains(x.SourceDocumentNo)
                            && itemCodes.Contains(x.ItemCode)
                            && x.StockPosting!.SealedAtUtc != null
                            && !db.StockValuationFacts.Any(r => r.ReversesValuationFactId == x.Id))
                .Select(x => new FactProjection(
                    x.Id,
                    x.SourceDocumentType,
                    x.SourceDocumentNo,
                    x.SourceDocumentLine,
                    x.ItemCode,
                    x.CostAmount))
                .ToListAsync(cancellationToken);

        var allocations = ownerNos.Length == 0
            ? []
            : await db.SalesReturnCostAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                            && x.BranchCode == scope.BranchCode
                            && ownerNos.Contains(x.OriginalOwnerDocumentNo))
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);
        var returnedCostByFact = CollapseReturnAllocationChains(allocations)
            .GroupBy(x => x.OriginalValuationFactId)
            .ToDictionary(x => x.Key, x => Money(x.Sum(y => y.ReturnedCostAmount)));

        var rows = new List<SalesGrossProfitInquiryRow>(lines.Count);
        foreach (var line in lines.OrderBy(x => x.InvoiceNo).ThenBy(x => x.Line))
        {
            if (!invoiceByNo.TryGetValue(line.InvoiceNo, out var invoice))
                continue;

            var ownerType = line.StockControl && line.LinkDo ? "SA_DO" : "SA_INVOICE";
            var ownerNo = line.LinkDo ? Normalize(line.DoNo) : invoice.InvoiceNo;
            var ownerLine = line.LinkDo
                ? line.DoLine?.ToString(CultureInfo.InvariantCulture)
                : line.Line.ToString(CultureInfo.InvariantCulture);
            var matches = line.StockControl && ownerNo is not null && ownerLine is not null
                ? facts.Where(x => string.Equals(x.SourceDocumentType, ownerType, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(x.SourceDocumentNo, ownerNo, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(x.SourceDocumentLine, ownerLine, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(x.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : [];

            var resolved = !line.StockControl || matches.Length > 0;
            var netSales = ResolveNetSales(line, invoice.CurrRate);
            decimal? cogs = null;
            decimal? grossProfit = null;
            decimal? margin = null;
            if (resolved)
            {
                cogs = Money(matches.Sum(x => x.CostAmount)
                              - matches.Sum(x => returnedCostByFact.GetValueOrDefault(x.Id)));
                grossProfit = Money(netSales - cogs.Value);
                margin = Math.Abs(netSales) <= Epsilon
                    ? null
                    : Money(grossProfit.Value / netSales * 100m);
            }

            rows.Add(new SalesGrossProfitInquiryRow
            {
                Invoice = invoice.InvoiceNo,
                Date = invoice.InvoiceDate,
                Customer = invoice.CustomerCode,
                CustomerName = invoice.CustomerName,
                InvoiceLine = line.Line,
                Item = line.ItemCode,
                Qty = Qty(line.StdQty),
                NetSales = netSales,
                Cogs = cogs,
                GrossProfit = grossProfit,
                GrossMarginPercent = margin,
                CogsOwnerType = line.StockControl ? ownerType : "NON_STOCK",
                CogsOwnerNo = ownerNo ?? string.Empty,
                CogsOwnerLine = ownerLine,
                CogsResolved = resolved
            });
        }

        rows = rows
            .Where(x => query.IncludeUnresolvedRows || x.CogsResolved)
            .OrderByDescending(x => x.Date)
            .ThenBy(x => x.Invoice, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.InvoiceLine)
            .ToList();

        var totals = BuildTotals(rows);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, MaxPageSize);
        return new SalesGrossProfitInquiryPage
        {
            Rows = rows.Skip(skip).Take(take).ToArray(),
            TotalCount = rows.Count,
            Totals = totals
        };
    }

    private static SalesGrossProfitInquiryTotals BuildTotals(
        IReadOnlyCollection<SalesGrossProfitInquiryRow> rows)
    {
        var resolved = rows.Where(x => x.CogsResolved).ToArray();
        var netSales = Money(resolved.Sum(x => x.NetSales));
        var cogs = Money(resolved.Sum(x => x.Cogs ?? 0m));
        var grossProfit = Money(resolved.Sum(x => x.GrossProfit ?? 0m));
        return new SalesGrossProfitInquiryTotals
        {
            ResolvedLineCount = resolved.Length,
            UnresolvedLineCount = rows.Count - resolved.Length,
            Qty = Qty(resolved.Sum(x => x.Qty)),
            NetSales = netSales,
            Cogs = cogs,
            GrossProfit = grossProfit,
            GrossMarginPercent = Math.Abs(netSales) <= Epsilon
                ? null
                : Money(grossProfit / netSales * 100m)
        };
    }

    private static decimal ResolveNetSales(InvoiceLineProjection line, decimal currRate)
    {
        // LocalAmount is the persisted company-base amount. The fallback keeps legacy rows with a
        // missing local amount readable while preserving the posted line value for modern rows.
        var amount = Math.Abs(line.LocalAmount) > Epsilon || Math.Abs(line.NetAmount) <= Epsilon
            ? line.LocalAmount
            : line.NetAmount * (currRate > 0m ? currRate : 1m);
        return Money(amount);
    }

    private static IReadOnlyList<ReturnAllocationAggregate> CollapseReturnAllocationChains(
        IReadOnlyCollection<SalesReturnCostAllocation> rows)
    {
        if (rows.Count == 0)
            return [];

        var children = rows.Where(x => x.ReversesAllocationId is not null)
            .GroupBy(x => x.ReversesAllocationId!.Value)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Id).First());
        var result = new List<ReturnAllocationAggregate>();
        foreach (var root in rows.Where(x => x.ReversesAllocationId is null).OrderBy(x => x.Id))
        {
            var chain = BuildReturnAllocationChain(root, children);
            var sign = 1m;
            var qty = 0m;
            var amount = 0m;
            foreach (var row in chain)
            {
                qty += sign * row.ReturnedBaseQty;
                amount += sign * row.ReturnedCostAmount;
                sign = -sign;
            }

            if (qty <= Epsilon || amount <= Epsilon)
                continue;
            result.Add(new ReturnAllocationAggregate(
                root.OriginalValuationFactId,
                Qty(qty),
                Money(amount)));
        }

        return result;
    }

    private static IReadOnlyList<SalesReturnCostAllocation> BuildReturnAllocationChain(
        SalesReturnCostAllocation root,
        IReadOnlyDictionary<long, SalesReturnCostAllocation> children)
    {
        var chain = new List<SalesReturnCostAllocation> { root };
        var seen = new HashSet<long> { root.Id };
        var current = root;
        while (children.TryGetValue(current.Id, out var child) && seen.Add(child.Id))
        {
            chain.Add(child);
            current = child;
        }

        return chain;
    }

    private static SalesGrossProfitInquiryPage EmptyPage() =>
        new() { Rows = [], TotalCount = 0, Totals = new SalesGrossProfitInquiryTotals() };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal Qty(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static decimal Money(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private sealed record InvoiceProjection(
        string InvoiceNo,
        DateTime InvoiceDate,
        string CustomerCode,
        string? CustomerName,
        decimal CurrRate);

    private sealed record InvoiceLineProjection(
        string InvoiceNo,
        int Line,
        string ItemCode,
        decimal StdQty,
        decimal LocalAmount,
        decimal NetAmount,
        bool LinkDo,
        string DoNo,
        short? DoLine,
        bool StockControl);

    private sealed record FactProjection(
        long Id,
        string SourceDocumentType,
        string SourceDocumentNo,
        string? SourceDocumentLine,
        string ItemCode,
        decimal CostAmount);

    private sealed record ReturnAllocationAggregate(
        long OriginalValuationFactId,
        decimal ReturnedBaseQty,
        decimal ReturnedCostAmount);
}
