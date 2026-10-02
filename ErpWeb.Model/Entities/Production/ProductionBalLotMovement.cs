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

    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public DateTime MovementDate { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
