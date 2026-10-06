using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Stage 5 procurement costing read model.
///
/// <para>
/// Settlement and adjustment tables are append-only. A reversal row therefore cannot simply be
/// included in a SUM: it is a copy of the original slice with a parent link. This service collapses
/// each root/child chain using alternating signs before it builds the inquiry rows.
/// </para>
/// </summary>
public sealed class PurchaseCostInquiryService : IPurchaseCostInquiryService
{
    private const decimal Epsilon = 0.0000005m;
    private const int MaxPageSize = 5000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public PurchaseCostInquiryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<PurchaseCostInquiryPage> SearchAsync(
        PurchaseCostInquiryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope()
                    ?? throw new InvalidOperationException("A trusted company/branch scope is required.");
        query ??= new PurchaseCostInquiryQuery();

        var from = query.DateFrom?.Date;
        var toExclusive = query.DateTo?.Date.AddDays(1);
        var vendor = Normalize(query.VendorCode);
        var poNo = Normalize(query.PoNo);
        var itemCode = Normalize(query.ItemCode);
        var piDocNo = Normalize(query.PiDocNo);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var invoiceQuery = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.Type == PoInvoiceTypes.Invoice
                        && x.Status == PoInvoiceStatuses.Posted);
        if (from is DateTime fromDate)
            invoiceQuery = invoiceQuery.Where(x => x.DocDate >= fromDate);
        if (toExclusive is DateTime toDate)
            invoiceQuery = invoiceQuery.Where(x => x.DocDate < toDate);
        if (vendor is not null)
            invoiceQuery = invoiceQuery.Where(x => x.VendorCode == vendor);
        if (piDocNo is not null)
            invoiceQuery = invoiceQuery.Where(x => x.DocNo == piDocNo);

        var invoices = await invoiceQuery
            .Select(x => new InvoiceProjection(
                x.DocNo,
                x.DocDate,
                x.VendorCode,
                x.VendorName,
                x.Currency,
                x.CurrRate))
            .ToListAsync(cancellationToken);
        if (invoices.Count == 0)
            return EmptyPage();

        var invoiceNos = invoices.Select(x => x.DocNo).ToArray();
        var invoiceByNo = invoices.ToDictionary(x => x.DocNo, StringComparer.OrdinalIgnoreCase);
        var detailRows = await db.PoInvoiceDetails.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && invoiceNos.Contains(x.DocNo)
                        && x.StdQty > 0m
                        && x.ICode != null
                        && x.ICode != string.Empty)
            .Select(x => new InvoiceLineProjection(
                x.DocNo,
                x.Line,
                x.ICode!,
                x.StdQty,
                x.PoNo,
                x.PoRelNo,
                x.PoLineNo))
            .ToListAsync(cancellationToken);
        if (itemCode is not null)
            detailRows = detailRows
                .Where(x => string.Equals(x.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (poNo is not null)
            detailRows = detailRows
                .Where(x => string.Equals(x.PoNo, poNo, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var settlements = await db.PurchaseReceiptCostSettlements.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && invoiceNos.Contains(x.PiDocNo))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var activeSettlements = CollapseSettlementChains(settlements);

        var adjustmentRows = await db.PurchaseCostAdjustments.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.SourceDocumentType == "PO_INVOICE"
                        && invoiceNos.Contains(x.SourceDocumentNo))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var activeAdjustments = CollapseAdjustmentChains(adjustmentRows);

        var poKeys = detailRows
            .Where(x => x.PoNo is not null && x.PoRelNo is not null && x.PoLineNo is not null)
            .Select(x => PoKey(x.PoNo!, x.PoRelNo!.Value, x.PoLineNo!.Value))
            .Concat(activeSettlements.Select(x => PoKey(x.PoNo, x.PoRelNo, x.PoLineNo)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var poNos = poKeys
            .Select(x => x.Split('|')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var poDetails = poNos.Length == 0
            ? []
            : await db.PoOrderDetails.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                            && x.BranchCode == scope.BranchCode
                            && poNos.Contains(x.PoNo))
                .Select(x => new PoLineProjection(
                    x.PoNo,
                    x.PoRelNo,
                    x.Line,
                    x.ICode,
                    x.PoUnitPrice,
                    x.PoPurQty,
                    x.CurCode))
                .ToListAsync(cancellationToken);
        var poByKey = poDetails
            .Where(x => !string.IsNullOrWhiteSpace(x.ICode))
            .GroupBy(x => PoKey(x.PoNo, x.PoRelNo, x.Line), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var factIds = activeSettlements
            .SelectMany(x => x.FactIds)
            .Distinct()
            .ToArray();
        var facts = factIds.Length == 0
            ? []
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => factIds.Contains(x.Id))
                .Select(x => new FactProjection(x.Id, x.CostMethod))
                .ToListAsync(cancellationToken);
        var methodByFact = facts.ToDictionary(x => x.Id, x => x.CostMethod);

        var rows = new List<PurchaseCostInquiryRow>();
        foreach (var line in detailRows.OrderBy(x => x.DocNo).ThenBy(x => x.Line))
        {
            if (!invoiceByNo.TryGetValue(line.DocNo, out var invoice))
                continue;

            var lineSettlements = activeSettlements
                .Where(x => string.Equals(x.PiDocNo, line.DocNo, StringComparison.OrdinalIgnoreCase)
                            && x.PiLineNo == line.Line
                            && string.Equals(x.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => PoKey(x.PoNo, x.PoRelNo, x.PoLineNo), StringComparer.OrdinalIgnoreCase)
                .Select(x => AggregateSettlements(x))
                .ToArray();

            if (lineSettlements.Length == 0)
            {
                rows.Add(BuildRow(
                    invoice,
                    line,
                    settlement: null,
                    lineAdjustments: [],
                    poByKey,
                    methodByFact));
                continue;
            }

            foreach (var settlement in lineSettlements)
            {
                var lineAdjustments = activeAdjustments.Where(x =>
                        string.Equals(x.SourceDocumentNo, line.DocNo, StringComparison.OrdinalIgnoreCase)
                        && x.SourceDocumentLine == line.Line
                        && string.Equals(x.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.PoNo, settlement.PoNo, StringComparison.OrdinalIgnoreCase)
                        && x.PoRelNo == settlement.PoRelNo
                        && x.PoLineNo == settlement.PoLineNo)
                    .ToArray();
                rows.Add(BuildRow(
                    invoice,
                    line,
                    settlement,
                    lineAdjustments,
                    poByKey,
                    methodByFact));
            }
        }

        rows = rows
            .Where(x => query.IncludeUnresolvedRows || !string.Equals(x.ValuationStatus, "UNRESOLVED", StringComparison.Ordinal))
            .Where(x => itemCode is null || string.Equals(x.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
            .Where(x => poNo is null || string.Equals(x.PoNo, poNo, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.VendorCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PoNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PoRelNo)
            .ThenBy(x => x.PoLineNo)
            .ThenBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PiDocNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PiLineNo)
            .ToList();

        var totals = BuildTotals(rows);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, MaxPageSize);
        return new PurchaseCostInquiryPage
        {
            Rows = rows.Skip(skip).Take(take).ToArray(),
            TotalCount = rows.Count,
            Totals = totals
        };
    }

    private static PurchaseCostInquiryRow BuildRow(
        InvoiceProjection invoice,
        InvoiceLineProjection line,
        SettlementAggregate? settlement,
        IReadOnlyCollection<PurchaseCostAdjustmentAggregate> lineAdjustments,
        IReadOnlyDictionary<string, PoLineProjection> poByKey,
        IReadOnlyDictionary<long, string> methodByFact)
    {
        var po = settlement is null
            ? line.PoNo is not null && line.PoRelNo is short rel && line.PoLineNo is short poLine
                ? poByKey.GetValueOrDefault(PoKey(line.PoNo, rel, poLine))
                : null
            : poByKey.GetValueOrDefault(PoKey(settlement.PoNo, settlement.PoRelNo, settlement.PoLineNo));
        var poNo = settlement?.PoNo ?? line.PoNo ?? string.Empty;
        var poRelNo = settlement?.PoRelNo ?? line.PoRelNo;
        var poLineNo = settlement?.PoLineNo ?? line.PoLineNo;
        var qty = settlement?.SettledBaseQty ?? 0m;
        var provisionalQty = settlement?.SettledBaseQty ?? line.StdQty;
        var grCost = settlement?.ReceiptValuationBaseAmount ?? 0m;
        var grCommercial = settlement?.ReceiptCommercialBaseAmount ?? 0m;
        var actual = settlement?.AllocatedActualBaseAmount ?? 0m;
        var variance = settlement is null ? 0m : actual - grCost;
        var inventoryVariance = lineAdjustments.Sum(x => x.InventoryAdjustmentAmount);
        var consumedVariance = lineAdjustments.Sum(x => x.ConsumedVarianceAmount);
        var landedCost = lineAdjustments
            .Where(x => x.AdjustmentType.Contains("LANDED", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.TotalAdjustmentAmount);

        decimal? provisional = null;
        if (po is not null && provisionalQty > Epsilon)
            provisional = Money(po.PoUnitPrice * provisionalQty);

        var methods = settlement is null
            ? []
            : settlement.FactIds
                .Select(x => methodByFact.GetValueOrDefault(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var method = methods.Length switch
        {
            0 => string.Empty,
            1 => methods[0] ?? string.Empty,
            _ => "MIXED"
        };
        var status = settlement is null
            ? "UNRESOLVED"
            : settlement.SettledBaseQty + Epsilon >= line.StdQty
                ? "VALUED"
                : settlement.SettledBaseQty > Epsilon
                    ? "PARTIAL"
                    : "UNRESOLVED";

        return new PurchaseCostInquiryRow
        {
            VendorCode = invoice.VendorCode,
            VendorName = invoice.VendorName,
            PoNo = poNo,
            PoRelNo = poRelNo,
            PoLineNo = poLineNo,
            ItemCode = line.ItemCode,
            PiDocNo = line.DocNo,
            PiLineNo = line.Line,
            PiDate = invoice.DocDate,
            CurrencyCode = po?.CurrencyCode ?? invoice.Currency,
            SettledBaseQty = Qty(qty),
            PoProvisionalCost = provisional,
            GrReceiptCost = Money(grCost),
            GrCommercialCost = Money(grCommercial),
            PiActualCost = Money(actual),
            PiVariance = Money(variance),
            InventoryCapitalizedVariance = Money(inventoryVariance),
            ConsumedVariance = Money(consumedVariance),
            LandedCost = Money(landedCost),
            CostMethod = method,
            ValuationStatus = status
        };
    }

    private static PurchaseCostInquiryTotals BuildTotals(IReadOnlyCollection<PurchaseCostInquiryRow> rows) =>
        new()
        {
            ResolvedLineCount = rows.Count(x => x.ValuationStatus == "VALUED"),
            UnresolvedLineCount = rows.Count(x => x.ValuationStatus != "VALUED"),
            SettledBaseQty = Qty(rows.Sum(x => x.SettledBaseQty)),
            PoProvisionalCost = Money(rows.Sum(x => x.PoProvisionalCost ?? 0m)),
            GrReceiptCost = Money(rows.Sum(x => x.GrReceiptCost)),
            PiActualCost = Money(rows.Sum(x => x.PiActualCost)),
            PiVariance = Money(rows.Sum(x => x.PiVariance)),
            InventoryCapitalizedVariance = Money(rows.Sum(x => x.InventoryCapitalizedVariance)),
            ConsumedVariance = Money(rows.Sum(x => x.ConsumedVariance)),
            LandedCost = Money(rows.Sum(x => x.LandedCost))
        };

    private static IReadOnlyList<SettlementAggregate> CollapseSettlementChains(
        IReadOnlyCollection<PurchaseReceiptCostSettlement> rows)
    {
        if (rows.Count == 0)
            return [];

        var children = rows.Where(x => x.ReversesSettlementId is not null)
            .GroupBy(x => x.ReversesSettlementId!.Value)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Id).First());
        var result = new List<SettlementAggregate>();
        foreach (var root in rows.Where(x => x.ReversesSettlementId is null).OrderBy(x => x.Id))
        {
            var chain = BuildSettlementChain(root, children);
            var sign = 1m;
            var qty = 0m;
            var commercial = 0m;
            var valuation = 0m;
            var actual = 0m;
            var factIds = new HashSet<long>();
            foreach (var row in chain)
            {
                qty += sign * row.SettledBaseQty;
                commercial += sign * row.ReceiptCommercialBaseAmount;
                valuation += sign * row.ReceiptValuationBaseAmount;
                actual += sign * row.AllocatedActualBaseAmount;
                factIds.Add(row.ReceiptValuationFactId);
                sign = -sign;
            }

            if (qty <= Epsilon)
                continue;
            result.Add(new SettlementAggregate(
                root.PiDocNo,
                root.PiLineNo,
                root.PoNo,
                root.PoRelNo,
                root.PoLineNo,
                root.ItemCode,
                Qty(qty),
                Money(commercial),
                Money(valuation),
                Money(actual),
                factIds));
        }

        return result;
    }

    private static IReadOnlyList<PurchaseCostAdjustmentAggregate> CollapseAdjustmentChains(
        IReadOnlyCollection<PurchaseCostAdjustment> rows)
    {
        if (rows.Count == 0)
            return [];

        var children = rows.Where(x => x.ReversesAdjustmentId is not null)
            .GroupBy(x => x.ReversesAdjustmentId!.Value)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Id).First());
        var result = new List<PurchaseCostAdjustmentAggregate>();
        foreach (var root in rows.Where(x => x.ReversesAdjustmentId is null).OrderBy(x => x.Id))
        {
            var chain = BuildAdjustmentChain(root, children);
            var sign = 1m;
            var actual = 0m;
            var reference = 0m;
            var total = 0m;
            var inventory = 0m;
            var consumed = 0m;
            foreach (var row in chain)
            {
                actual += sign * row.ActualBaseAmount;
                reference += sign * row.ReferenceBaseAmount;
                total += sign * row.TotalAdjustmentAmount;
                inventory += sign * row.InventoryAdjustmentAmount;
                consumed += sign * row.ConsumedVarianceAmount;
                sign = -sign;
            }

            if (Math.Abs(total) <= Epsilon
                && Math.Abs(inventory) <= Epsilon
                && Math.Abs(consumed) <= Epsilon)
                continue;
            result.Add(new PurchaseCostAdjustmentAggregate(
                root.SourceDocumentNo,
                root.SourceDocumentLine,
                root.PoNo,
                root.PoRelNo,
                root.PoLineNo,
                root.ItemCode,
                root.AdjustmentType,
                Money(actual),
                Money(reference),
                Money(total),
                Money(inventory),
                Money(consumed)));
        }

        return result;
    }

    private static IReadOnlyList<PurchaseReceiptCostSettlement> BuildSettlementChain(
        PurchaseReceiptCostSettlement root,
        IReadOnlyDictionary<long, PurchaseReceiptCostSettlement> children)
    {
        var chain = new List<PurchaseReceiptCostSettlement> { root };
        var seen = new HashSet<long> { root.Id };
        var current = root;
        while (children.TryGetValue(current.Id, out var child) && seen.Add(child.Id))
        {
            chain.Add(child);
            current = child;
        }

        return chain;
    }

    private static IReadOnlyList<PurchaseCostAdjustment> BuildAdjustmentChain(
        PurchaseCostAdjustment root,
        IReadOnlyDictionary<long, PurchaseCostAdjustment> children)
    {
        var chain = new List<PurchaseCostAdjustment> { root };
        var seen = new HashSet<long> { root.Id };
        var current = root;
        while (children.TryGetValue(current.Id, out var child) && seen.Add(child.Id))
        {
            chain.Add(child);
            current = child;
        }

        return chain;
    }

    private static SettlementAggregate AggregateSettlements(
        IEnumerable<SettlementAggregate> rows)
    {
        var materialized = rows.ToArray();
        var first = materialized[0];
        return new SettlementAggregate(
            first.PiDocNo,
            first.PiLineNo,
            first.PoNo,
            first.PoRelNo,
            first.PoLineNo,
            first.ItemCode,
            Qty(materialized.Sum(x => x.SettledBaseQty)),
            Money(materialized.Sum(x => x.ReceiptCommercialBaseAmount)),
            Money(materialized.Sum(x => x.ReceiptValuationBaseAmount)),
            Money(materialized.Sum(x => x.AllocatedActualBaseAmount)),
            materialized.SelectMany(x => x.FactIds).ToHashSet());
    }

    private static PurchaseCostInquiryPage EmptyPage() =>
        new() { Rows = [], TotalCount = 0, Totals = new PurchaseCostInquiryTotals() };

    private static string PoKey(string poNo, short relNo, short lineNo) =>
        $"{poNo.Trim().ToUpperInvariant()}|{relNo}|{lineNo}";

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal Qty(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static decimal Money(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private sealed record InvoiceProjection(
        string DocNo,
        DateTime DocDate,
        string VendorCode,
        string? VendorName,
        string? Currency,
        decimal CurrRate);

    private sealed record InvoiceLineProjection(
        string DocNo,
        short Line,
        string ItemCode,
        decimal StdQty,
        string? PoNo,
        short? PoRelNo,
        short? PoLineNo);

    private sealed record PoLineProjection(
        string PoNo,
        short PoRelNo,
        short Line,
        string? ICode,
        decimal PoUnitPrice,
        decimal PoPurQty,
        string? CurrencyCode);

    private sealed record FactProjection(long Id, string CostMethod);

    private sealed record SettlementAggregate(
        string PiDocNo,
        short PiLineNo,
        string PoNo,
        short PoRelNo,
        short PoLineNo,
        string ItemCode,
        decimal SettledBaseQty,
        decimal ReceiptCommercialBaseAmount,
        decimal ReceiptValuationBaseAmount,
        decimal AllocatedActualBaseAmount,
        IReadOnlySet<long> FactIds);

    private sealed record PurchaseCostAdjustmentAggregate(
        string SourceDocumentNo,
        int SourceDocumentLine,
        string? PoNo,
        short? PoRelNo,
        short? PoLineNo,
        string ItemCode,
        string AdjustmentType,
        decimal ActualBaseAmount,
        decimal ReferenceBaseAmount,
        decimal TotalAdjustmentAmount,
        decimal InventoryAdjustmentAmount,
        decimal ConsumedVarianceAmount);
}
