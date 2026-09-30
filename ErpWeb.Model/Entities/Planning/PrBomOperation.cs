namespace ErpWeb.Model.Entities.Planning;

/// <summary>
/// Version-owned route operation for a Product Definition.  The work-centre fields retain
/// the legacy centre/output boundary while the operation fields retain the process step.
/// </summary>
public sealed class PrBomOperation
{
    public long Uid { get; set; }
    public Guid OperationKey { get; set; } = Guid.NewGuid();
    public long BomHdrId { get; set; }
    public PrBomHdr? Header { get; set; }

    /// <summary>
    /// Persistent route occurrence. Nullable only while legacy definitions are being migrated.
    /// Activation validation must require a value.
    /// </summary>
    public long? RouteStepId { get; set; }
    public PrBomRouteStep? RouteStep { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string WorkCentreCode { get; set; } = string.Empty;
    public string OutputItemCode { get; set; } = string.Empty;
    public int CentralSequence { get; set; }
    public decimal OutputBaseQty { get; set; } = 1m;
    public string? OutputUom { get; set; }

    public string OperationCode { get; set; } = string.Empty;
    public int ProcessSequence { get; set; }
    public string ProcessType { get; set; } = PrProcessTypes.Machine;
    public decimal StandardDurationMinutes { get; set; }
    public decimal SetupLossQty { get; set; }
    public decimal OperationLossQty { get; set; }
    public bool IsFinalOperation { get; set; }
    public string? Remark { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PrBomMachineOption> Machines { get; set; } = new List<PrBomMachineOption>();
    public ICollection<PrBomLabourRequirement> LabourRequirements { get; set; } = new List<PrBomLabourRequirement>();
    public ICollection<PrDefBOM> Materials { get; set; } = new List<PrDefBOM>();
}

/// <summary>Machine/resource option owned by a versioned route operation.</summary>
public sealed class PrBomMachineOption
{
    public long Uid { get; set; }
    public long OperationId { get; set; }
    public PrBomOperation? Operation { get; set; }

    public string MachineCode { get; set; } = string.Empty;
    public string? MachineDescription { get; set; }
    public int ResourceSequence { get; set; }
    public bool IsPrimary { get; set; } = true;
    public int Priority { get; set; } = 1;
    public decimal CycleSeconds { get; set; }
    public decimal OutputPerCycle { get; set; } = 1m;
    public decimal ConversionSeconds { get; set; }
    public decimal SetupSeconds { get; set; }
    public decimal QueueSeconds { get; set; }
    public decimal MachineRatePerHour { get; set; }
    public int ParallelMachineCount { get; set; } = 1;

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PrBomLabourStandard> Labours { get; set; } = new List<PrBomLabourStandard>();
    public ICollection<PrBomLabourRequirement> LabourRequirements { get; set; } = new List<PrBomLabourRequirement>();

    /// <summary>
    /// Domain name for the legacy physical <c>IsPrimary</c> column. Kept out of the EF model
    /// until compatibility readers have moved from <see cref="IsPrimary"/>.
    /// </summary>
    public bool IsDefault
    {
        get => IsPrimary;
        set => IsPrimary = value;
    }
}

/// <summary>
/// Legacy-compatible labour standard.  Cost is deliberately stored per output unit; it is
/// not interpreted as an hourly wage during migration or work-order snapshotting.
/// </summary>
public sealed class PrBomLabourStandard
{
    public long Uid { get; set; }
    public long MachineOptionId { get; set; }
    public PrBomMachineOption? MachineOption { get; set; }

    public string LabourCode { get; set; } = string.Empty;
    public string? LabourDescription { get; set; }
    public decimal CostPerOutputUnit { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class PrProcessTypes
{
    public const string Machine = "MACHINE";
    public const string Automated = "AUTOMATED";
    public const string Manual = "MANUAL";
    public const string Inspection = "INSPECTION";
    public const string Wait = "WAIT";
    public const string Packing = "PACKING";
    public const string Subcontract = "SUBCONTRACT";

    public static bool IsValid(string? value) => value is not null && All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Process types whose cycle time is normally taken from a selected machine/resource option.
    /// PACKING is machine-capable but may also be authored as a pure standard-duration step.
    /// </summary>
    public static bool SupportsMachine(string? value) =>
        value is Machine or Automated or Packing;

    /// <summary>
    /// Process types that are always duration-based and must not carry machine options. Their
    /// cycle time comes from <c>StandardDurationMinutes</c>.
    /// </summary>
    public static bool IsDurationBased(string? value) =>
        value is Manual or Inspection or Wait or Subcontract;

    public static IReadOnlyList<string> All { get; } =
        [Machine, Automated, Manual, Inspection, Wait, Packing, Subcontract];
}
