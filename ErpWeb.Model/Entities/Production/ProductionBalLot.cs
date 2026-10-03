namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Live production balance lot (MATERIAL_IN per Issue contribution, or WIP per route/lot).
/// Qty/BaseQty are authoritative; movements are audit.
/// </summary>
public class ProductionBalLot
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    public string ItemCode { get; set; } = string.Empty;
    public string? Description { get; set; }

    public decimal Qty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal BaseQty { get; set; }
    public string BaseUom { get; set; } = string.Empty;
    public decimal ConversionFactorToBase { get; set; } = 1m;

    public decimal TotalCost { get; set; }
    public decimal AverageUnitCost { get; set; }

    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public string WorkOrderNo { get; set; } = string.Empty;

    public long? WorkOrderMaterialId { get; set; }
    public ProductionWorkOrderMaterial? WorkOrderMaterial { get; set; }
    public long? OriginalIssueMovementId { get; set; }
    public ProductionMaterialMovement? OriginalIssueMovement { get; set; }
    public int? SourceIvBalLocId { get; set; }

    public string WarehouseCode { get; set; } = string.Empty;
    public string LocationCode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;

    // V2 dimensions remain nullable while legacy rows are in service.
    public string? BalanceStage { get; set; }
    public long? ProductionLocationId { get; set; }
    public ProductionLocation? ProductionLocation { get; set; }
    public string? StockStatusCode { get; set; }
    public string? PoolCode { get; set; }
    public string? PhysicalLotNo { get; set; }
    public string? LotIdentityKind { get; set; }
    public DateTime? FirstReceiptEffectiveAt { get; set; }
    public DateTime? LastStockEventEffectiveAt { get; set; }
    public Guid? ContributionKey { get; set; }
    public string? OriginType { get; set; }

    public long? ProducingRouteStepId { get; set; }
    public ProductionWorkOrderRouteStep? ProducingRouteStep { get; set; }
    public long? WorkOrderOperationId { get; set; }
    public ProductionWorkOrderOperation? WorkOrderOperation { get; set; }
    public string? OutputType { get; set; }
    public string? WorkCentreCode { get; set; }
    public string? ProcessCode { get; set; }

    public DateTime? LastMovementDate { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ProductionBalLotMovement> Movements { get; set; } = new List<ProductionBalLotMovement>();
}
