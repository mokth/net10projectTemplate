namespace ErpWeb.Model.Entities.Production;

/// <summary>Per-document Daily Production material fact: standard, actual consume, signed variance, reason.</summary>
public class ProductionOutputMaterial
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long ProductionOutputId { get; set; }
    public ProductionOutput? ProductionOutput { get; set; }

    public long? WorkOrderMaterialId { get; set; }
    public ProductionWorkOrderMaterial? WorkOrderMaterial { get; set; }
    public bool IsHandoff { get; set; }
    public long? HandoffFromOperationId { get; set; }

    public string ComponentCode { get; set; } = string.Empty;
    public string RequiredUom { get; set; } = string.Empty;
    public string SupplySource { get; set; } = string.Empty;
    public string IssueMethod { get; set; } = string.Empty;
    public decimal TolerancePercent { get; set; }
    public decimal WoBomRequiredQty { get; set; }
    public decimal ConversionFactorToBase { get; set; } = 1m;
    public string? BaseUom { get; set; }

    public decimal StandardQty { get; set; }
    public decimal ConsumeQty { get; set; }
    public decimal VarianceQty { get; set; }

    public string? VarianceReasonCode { get; set; }
    public string? VarianceReasonText { get; set; }

    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
