using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Authoritative inventory valuation read model. It deliberately has a separate contract from the
/// operational stock summary because historical value must come from the sealed valuation ledger and
/// applicable snapshots, never from mutable balance or item-master prices.
/// </summary>
public interface IStockValuationReportService
{
    Task<StockValuationReportPage> SearchAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);

    Task<StockValuationReportSummary> SummariseAsync(
        string companyCode,
        string branchCode,
        IvStockSummaryQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class StockValuationReportPage
{
    public IReadOnlyList<StockValuationReportRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class StockValuationReportSummary
{
    public int GroupCount { get; init; }
    public int ItemCount { get; init; }
    public int PileCount { get; init; }
    public int ZeroQtyPileCount { get; init; }
    public decimal TotalQty { get; init; }
    public decimal TotalValue { get; init; }
    public int UnresolvedCount { get; init; }
}

public sealed class StockValuationReportRow
{
    public string? ICode { get; init; }
    public string? IDesc { get; init; }
    public string? IClassCode { get; init; }
    public string? IClassDesc { get; init; }
    public string? StdUom { get; init; }
    public string? WhCode { get; init; }
    public string? WhDesc { get; init; }
    public decimal TotalQty { get; init; }
    public int ItemCount { get; init; }
    public int PileCount { get; init; }
    public int ZeroQtyPileCount { get; init; }
    public decimal InventoryValue { get; init; }
    public decimal UnitCost { get; init; }
    public string CostMethod { get; init; } = string.Empty;
    public string ValuationStatus { get; init; } = string.Empty;
    public bool IsAllocatedWarehouseValue { get; init; }
}
