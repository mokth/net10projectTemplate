namespace ErpWeb.Model.Entities.Production;

/// <summary>Full-balance ownership transfer of one stockable WIP lot between Work Orders.</summary>
public sealed class ProductionWorkOrderTransfer
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string TransferNo { get; set; } = string.Empty;
    public string Status { get; set; } = ProductionWorkOrderTransferStatuses.New;
    public DateTime TransferDateTime { get; set; }
    public string? Remark { get; set; }

    public long SourceProductionBalLotId { get; set; }
    public long SourceWorkOrderId { get; set; }
    public string SourceWorkOrderNo { get; set; } = string.Empty;
    public long SourceProducingRouteStepId { get; set; }
    public long SourceProducerOperationId { get; set; }
    public string SourceWorkCentreCode { get; set; } = string.Empty;
    public string SourceProcessCode { get; set; } = string.Empty;
    public long SourceProductionLocationId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public string? PhysicalLotNo { get; set; }
    public string? PoolCode { get; set; }
    public string? LotIdentityKind { get; set; }
    public decimal Qty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal BaseQty { get; set; }
    public string BaseUom { get; set; } = string.Empty;
    public decimal ConversionFactorToBase { get; set; }
    public decimal TransferTotalCost { get; set; }
    public DateTime? SourceLastMovementDate { get; set; }
    public DateTime? SourceLastStockEventEffectiveAt { get; set; }
    public byte[] SourceBalanceRowVersion { get; set; } = [];

    // Live destination identity follows the target Work Order's producing route.
    public long TargetWorkOrderId { get; set; }
    public string TargetWorkOrderNo { get; set; } = string.Empty;
    public long TargetProducingRouteStepId { get; set; }
    public long TargetProducerOperationId { get; set; }
    public string TargetProducerWorkCentreCode { get; set; } = string.Empty;
    public string TargetProducerProcessCode { get; set; } = string.Empty;
    public long TargetProducerProductionLocationId { get; set; }

    // Business destination records why this WIP was assigned to the target Work Order.
    public long TargetWorkOrderMaterialId { get; set; }
    public long TargetConsumingRouteStepId { get; set; }
    public long TargetConsumingOperationId { get; set; }
    public string TargetConsumingWorkCentreCode { get; set; } = string.Empty;
    public string TargetConsumingProcessCode { get; set; } = string.Empty;
    public long? TargetConsumerProductionLocationId { get; set; }

    public int TargetSnapshotRevision { get; set; }
    public string TargetSnapshotHash { get; set; } = string.Empty;

    public string PostingRequestId { get; set; } = string.Empty;
    public long ProductionPostingLinkId { get; set; }
    public long? StockPostingId { get; set; }
    public long? DestinationProductionBalLotId { get; set; }
    public long? TransferOutMovementId { get; set; }
    public long? TransferInMovementId { get; set; }
    public Guid? MovementGroupId { get; set; }

    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
