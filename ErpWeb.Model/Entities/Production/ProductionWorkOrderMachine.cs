namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Machine / resource option snapshotted under an operation (<c>dbo.PrWorkOrderMachine</c>).
/// <para>
/// Every eligible Product Definition machine option is snapshotted for traceability, but exactly
/// one carries <see cref="IsSelected"/> = true for a machine-based operation, and only that row
/// drives duration and scheduling (plan §6.4). A machine option being an "alternative" means it
/// is a different way to run the same step — it must never be read as a sequential or parallel
/// required step. <see cref="ParallelMachineCount"/> is capacity <i>within</i> this one option.
/// </para>
/// </summary>
public class ProductionWorkOrderMachine
{
    public long Uid { get; set; }
    public long OperationId { get; set; }
    public ProductionWorkOrderOperation? Operation { get; set; }

    /// <summary>Source <c>PrBomMachineOption.UID</c>.</summary>
    public long? SourceMachineOptionId { get; set; }

    /// <summary>Stable source machine key. Drives the filtered uniqueness index.</summary>
    public Guid? SourceMachineKey { get; set; }

    /// <summary>Selection order among alternatives (source <c>Priority</c>).</summary>
    public int Priority { get; set; } = 1;

    public string MachineCode { get; set; } = string.Empty;
    public string? MachineDescription { get; set; }

    /// <summary>The source's primary/default designation.</summary>
    public bool IsDefault { get; set; }

    /// <summary>True for the single option this snapshot schedules. Set from the source default.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Capacity within this option. Must be &gt;= 1.</summary>
    public int ParallelMachineCount { get; set; } = 1;

    /// <summary>Fixed to DISCRETE in this milestone; see <see cref="ProductionMachineCycleQuantityModes"/>.</summary>
    public string CycleQuantityMode { get; set; } = ProductionMachineCycleQuantityModes.Discrete;

    public decimal CycleSeconds { get; set; }

    /// <summary>Output units produced per cycle.</summary>
    public decimal OutputPerCycle { get; set; } = 1m;

    /// <summary>
    /// UOM that <see cref="OutputPerCycle"/> is expressed in. The current Product Definition
    /// contract defines it in the owning operation's output UOM, so this is snapshotted as the
    /// operation's <c>PlannedOutputUOM</c>.
    /// </summary>
    public string? OutputPerCycleUom { get; set; }

    /// <summary>Machine output required to satisfy the operation's planned output.</summary>
    public decimal RequiredMachineOutputQty { get; set; }
    public string? RequiredMachineOutputUom { get; set; }

    /// <summary>Number of machine cycles needed, derived from required output and output-per-cycle.</summary>
    public decimal PlannedCycleCount { get; set; }

    /// <summary>Calendar slots the planned cycles occupy, after parallel capacity.</summary>
    public decimal PlannedCycleSlots { get; set; }

    /// <summary>Run minutes derived from cycles and cycle time.</summary>
    public decimal PlannedRunMinutes { get; set; }

    public decimal ConversionSeconds { get; set; }
    public decimal SetupSeconds { get; set; }
    public decimal QueueSeconds { get; set; }
    public decimal MachineRatePerHour { get; set; }

    // ── Scheduling provenance ─────────────────────────────────────────────────────────────────

    /// <summary>Machine calendar identifier, when a machine calendar drove the schedule.</summary>
    public long? CalendarSourceId { get; set; }

    public DateTime? CalendarSourceLastModified { get; set; }
    public string? ScheduleSourceHash { get; set; }

    public DateTime? CalendarHorizonStart { get; set; }
    public DateTime? CalendarHorizonEnd { get; set; }

    /// <summary>Plant-local planned start (plan §6.5).</summary>
    public DateTime? PlannedStartDateTime { get; set; }

    /// <summary>Plant-local planned completion (plan §6.5).</summary>
    public DateTime? PlannedCompletionDateTime { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Labour owned by this machine option (Option A1: <c>MachineID</c> set).</summary>
    public ICollection<ProductionWorkOrderLabour> Labours { get; set; } = new List<ProductionWorkOrderLabour>();
}
