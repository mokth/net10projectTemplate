namespace ErpWeb.Model.Entities.Production;

/// <summary>Daily Production / production output document (NEW → POSTED → REVERSED).</summary>
public class ProductionOutput
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public string Status { get; set; } = ProductionOutputStatuses.New;

    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public long RouteStepId { get; set; }
    public ProductionWorkOrderRouteStep? RouteStep { get; set; }
    public long WorkOrderOperationId { get; set; }
    public ProductionWorkOrderOperation? WorkOrderOperation { get; set; }

    public DateTime ProductionDate { get; set; }
    public string? ShiftCode { get; set; }
    public string? PlannedMachineCode { get; set; }
    public string? ActualMachineCode { get; set; }
    public string? OperatorCode { get; set; }

    public decimal GoodQty { get; set; }
    public decimal ScrapQty { get; set; }
    public decimal RejectQty { get; set; }
    public decimal HoldQty { get; set; }

    public string OutputUom { get; set; } = string.Empty;
    public string OutputItemCode { get; set; } = string.Empty;
    public string? OutputType { get; set; }
    public string OutputLotNo { get; set; } = string.Empty;

    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public string PostingRequestId { get; set; } = string.Empty;

    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? ReversedDate { get; set; }
    public string? ReversedBy { get; set; }

    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
    public bool IsDeleted => DeletedAtUtc.HasValue;

    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ProductionOutputMaterial> Materials { get; set; } = [];
}
