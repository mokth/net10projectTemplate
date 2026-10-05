using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public interface IProductionBalanceInquiryService
{
    Task<IvMasterOperationResult<ProductionBalanceLotPage>> SearchAsync(
        ProductionBalanceLotQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<ProductionBalanceLotMovementRow>>> GetMovementsAsync(
        long productionBalLotId, CancellationToken cancellationToken = default);
}

public sealed class ProductionBalanceLotQuery
{
    public string? SearchText { get; set; }
    public string? Kind { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? ItemCode { get; set; }
    public bool IncludeZeroQty { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class ProductionBalanceLotPage
{
    public IReadOnlyList<ProductionBalanceLotRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class ProductionBalanceLotRow
{
    public long Uid { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string ItemCode { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string LotNo { get; init; } = string.Empty;
    public string WorkOrderNo { get; init; } = string.Empty;
    public string? WorkCentreCode { get; init; }
    public string? ProcessCode { get; init; }
    public decimal Qty { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal NetReceivedBaseQty { get; init; }
    public decimal PendingReceiptBaseQty { get; init; }
    public decimal BaseQty { get; init; }
    public string BaseUom { get; init; } = string.Empty;
    public string WarehouseCode { get; init; } = string.Empty;
    public string LocationCode { get; init; } = string.Empty;
    public DateTime? LastMovementDate { get; init; }
}

public sealed class ProductionBalanceLotMovementRow
{
    public int? FinishedGoodReceiptId { get; init; }
    public long Uid { get; init; }
    public string MovementType { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal BaseQty { get; init; }
    public string BaseUom { get; init; } = string.Empty;
    public decimal SignedBaseQty { get; init; }
    public string DocumentType { get; init; } = string.Empty;
    public string DocumentNo { get; init; } = string.Empty;
    public DateTime MovementDate { get; init; }
    public DateTime CreatedDate { get; init; }
}
