namespace ErpWeb.Model.Entities.Production;

/// <summary>Immutable audit movement for a <see cref="ProductionBalLot"/>.</summary>
public class ProductionBalLotMovement
{
    public long Uid { get; set; }
    public long ProductionBalLotId { get; set; }
    public ProductionBalLot? ProductionBalLot { get; set; }

    public string MovementType { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal BaseQty { get; set; }
    public string BaseUom { get; set; } = string.Empty;
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }

    public long WorkOrderId { get; set; }
    public long? WorkOrderMaterialId { get; set; }
    public long? WorkOrderOperationId { get; set; }
    public long? RouteStepId { get; set; }
    public long? ProductionOutputId { get; set; }
    public ProductionOutput? ProductionOutput { get; set; }
    public long PostingLinkId { get; set; }
    public ProductionPostingLink? PostingLink { get; set; }
    public long? OriginalMovementId { get; set; }
    public ProductionBalLotMovement? OriginalMovement { get; set; }

    // Nullable V2 journal and frozen-snapshot fields preserve V1 compatibility.
    public byte? LedgerVersion { get; set; }
    public long? LedgerEpochId { get; set; }
    public long? StockPostingId { get; set; }
    public int? PostingLineNo { get; set; }
    public string? CompanyCode { get; set; }
    public string? BranchCode { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public string? BalanceStage { get; set; }
    public long? ProductionLocationId { get; set; }
    public string? ProductionLocationCode { get; set; }
    public string? WorkCentreCode { get; set; }
    public string? ProcessCode { get; set; }
    public string? LotIdentity { get; set; }
    public string? PhysicalLotNo { get; set; }
    public string? StockStatusCode { get; set; }
    public string? WorkOrderNo { get; set; }
    public decimal? ConversionFactorToBase { get; set; }
    public Guid? MovementGroupId { get; set; }
    public string? SourceLineId { get; set; }
    public int? SplitOrdinal { get; set; }
    public int? InventoryHistoryId { get; set; }
    public string? ValuationStatus { get; set; }
    public int? CostBasisVersion { get; set; }

    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public DateTime MovementDate { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
