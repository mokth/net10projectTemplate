using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockAsOfValuationRow(
    string ItemCode,
    string BaseUom,
    decimal BaseQty,
    decimal InventoryValue,
    decimal AverageUnitCost);

public sealed record SalesInvoiceCogsLine(
    int InvoiceLine,
    string ItemCode,
    bool LinkDo,
    string ValuationOwnerType,
    string ValuationOwnerNo,
    decimal Cogs,
    bool IsResolved);

public sealed record SalesInvoiceCogsResult(
    string InvoiceNo,
    decimal TotalCogs,
    bool IsFullyResolved,
    IReadOnlyList<SalesInvoiceCogsLine> Lines);

public interface IStockValuationQueryService
{
    Task<IReadOnlyList<StockAsOfValuationRow>> GetAsOfAsync(
        DateTime asOf,
        CancellationToken cancellationToken = default);

    Task<SalesInvoiceCogsResult> GetInvoiceCogsAsync(
        string invoiceNo,
        CancellationToken cancellationToken = default);
}

public sealed class StockValuationQueryService : IStockValuationQueryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;

    public StockValuationQueryService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
    }

    public async Task<IReadOnlyList<StockAsOfValuationRow>> GetAsOfAsync(
        DateTime asOf,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope()
                    ?? throw new InvalidOperationException("A trusted company/branch scope is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exclusiveEnd = asOf.Date.AddDays(1);
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.EffectiveAt < exclusiveEnd
                        && x.StockPosting!.SealedAtUtc != null)
            .Select(x => new { x.ItemCode, x.BaseUom, x.Direction, x.BaseQty, x.CostAmount })
            .ToListAsync(cancellationToken);

        return facts.GroupBy(x => new { x.ItemCode, x.BaseUom })
            .Select(x =>
            {
                var qty = x.Sum(y => y.BaseQty * y.Direction);
                var value = x.Sum(y => y.CostAmount * y.Direction);
                return new StockAsOfValuationRow(
                    x.Key.ItemCode,
                    x.Key.BaseUom,
                    qty,
                    value,
                    qty == 0m ? 0m : decimal.Round(value / qty, 6, MidpointRounding.AwayFromZero));
            })
            .Where(x => x.BaseQty != 0m || x.InventoryValue != 0m)
            .OrderBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<SalesInvoiceCogsResult> GetInvoiceCogsAsync(
        string invoiceNo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceNo))
            throw new ArgumentException("Invoice number is required.", nameof(invoiceNo));
        var scope = _tenant.TryBranchScope()
                    ?? throw new InvalidOperationException("A trusted company/branch scope is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var no = invoiceNo.Trim();
        var invoiceExists = await db.SaInvoices.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.InvNo == no,
            cancellationToken);
        if (!invoiceExists)
            throw new InvalidOperationException($"Invoice '{no}' was not found in the current branch.");

        var lines = await db.SaInvoiceDetails.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.InvNo == no
                        && x.StockControl
                        && x.StdQty > 0m)
            .OrderBy(x => x.Line)
            .Select(x => new
            {
                x.Line, x.ICode, x.LinkDo, x.DoNo, x.DoLine
            })
            .ToListAsync(cancellationToken);

        var ownerNos = lines.Select(x => x.LinkDo ? x.DoNo : no)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var facts = ownerNos.Length == 0
            ? []
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                            && x.BranchCode == scope.BranchCode
                            && x.Direction == -1
                            && x.ValuationStatus == StockValuationStatuses.Valued
                            && ownerNos.Contains(x.SourceDocumentNo)
                            && !db.StockValuationFacts.Any(r => r.ReversesValuationFactId == x.Id)
                            && x.StockPosting!.SealedAtUtc != null)
                .Select(x => new
                {
                    x.SourceDocumentType, x.SourceDocumentNo, x.SourceDocumentLine,
                    x.ItemCode, x.CostAmount
                })
                .ToListAsync(cancellationToken);

        var result = new List<SalesInvoiceCogsLine>();
        foreach (var line in lines)
        {
            var ownerType = line.LinkDo ? "SA_DO" : "SA_INVOICE";
            var ownerNo = line.LinkDo ? line.DoNo.Trim() : no;
            var ownerLine = line.LinkDo ? line.DoLine?.ToString() : line.Line.ToString();
            // A sales line is resolved only by its exact valuation owner and source line.
            // Same-document/item facts are deliberately not a fallback: under split costing they
            // may belong to another line, and aggregating them would manufacture COGS.
            var matches = ownerLine is null
                ? []
                : facts.Where(x =>
                        string.Equals(x.SourceDocumentType, ownerType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.SourceDocumentNo, ownerNo, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.SourceDocumentLine, ownerLine, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.ItemCode, line.ICode, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            result.Add(new SalesInvoiceCogsLine(
                line.Line,
                line.ICode ?? string.Empty,
                line.LinkDo,
                ownerType,
                ownerNo,
                matches.Sum(x => x.CostAmount),
                matches.Length > 0));
        }

        return new SalesInvoiceCogsResult(
            no,
            result.Sum(x => x.Cogs),
            result.All(x => x.IsResolved),
            result);
    }
}
