namespace ErpWeb.Model.Entities.Planning;

/// <summary>One occurrence of a work centre in a Product Definition revision.</summary>
public sealed class PrBomRouteStep
{
    public long Uid { get; set; }
    public Guid RouteStepKey { get; set; } = Guid.NewGuid();
    public long BomHdrId { get; set; }
    public PrBomHdr? Header { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string WorkCentreCode { get; set; } = string.Empty;
    public int StageSequence { get; set; }
    public string OutputItemCode { get; set; } = string.Empty;
    public string OutputType { get; set; } = PrRouteOutputTypes.WipStocked;
    public decimal StandardOutputQty { get; set; } = 1m;
    public string OutputUom { get; set; } = string.Empty;
    public decimal YieldPercent { get; set; } = 100m;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PrBomOperation> Operations { get; set; } = new List<PrBomOperation>();
}

public static class PrRouteOutputTypes
{
    public const string WipStocked = "WIP_STOCKED";
    public const string WipNonstock = "WIP_NONSTOCK";
    public const string FinishedGoods = "FINISHED_GOODS";
}

/// <summary>Operation-level labour standard, optionally specialized for one machine option.</summary>
public sealed class PrBomLabourRequirement
{
    public long Uid { get; set; }
    public long OperationId { get; set; }
    public PrBomOperation? Operation { get; set; }
    public long? MachineOptionId { get; set; }
    public PrBomMachineOption? MachineOption { get; set; }
    public string LabourCode { get; set; } = string.Empty;
    public decimal RequiredHeadcount { get; set; } = 1m;
    public decimal SetupMinutes { get; set; }
    public decimal RunMinutes { get; set; }
    public decimal CostRate { get; set; }
    public string CostBasis { get; set; } = PrLabourCostBases.PerHour;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class PrLabourCostBases
{
    public const string PerHour = "PER_HOUR";
    public const string PerOperation = "PER_OPERATION";
    public const string PerOutputUnit = "PER_OUTPUT_UNIT";
}

/// <summary>Branch-owned issue warehouse default for a company-owned material standard.</summary>
public sealed class PrBomMaterialBranchDefault
{
    public long Uid { get; set; }
    public long MaterialId { get; set; }
    public PrDefBOM? Material { get; set; }
    public string BranchCode { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
