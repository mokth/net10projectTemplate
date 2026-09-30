namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Process-step snapshot inside a route step (<c>dbo.PrWorkOrderOperation</c>).
/// <para>
/// Version-2 rows always belong to a <see cref="ProductionWorkOrderRouteStep"/> and carry the
/// process type and the explicit planned input/output quantity and UOM basis. Version-1 legacy
/// rows may keep <see cref="WorkOrderId"/>, <see cref="WorkCentreCode"/> and
/// <see cref="SequenceNo"/> populated and <see cref="RouteStepId"/> null (plan §6.6 nullable
/// compatibility deprecation).
/// </para>
/// </summary>
public class ProductionWorkOrderOperation
{
    public long Uid { get; set; }
    public long? WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }

    /// <summary>Owning route step. Null only for legacy version-1 rows.</summary>
    public long? RouteStepId { get; set; }
    public ProductionWorkOrderRouteStep? RouteStep { get; set; }

    /// <summary>
    /// Legacy flattened sequence. Superseded by <see cref="ProcessSequence"/> within a route step;
    /// retained so version-1 rows keep their identity.
    /// </summary>
    public int SequenceNo { get; set; }

    /// <summary>Source <c>PrBomOperation.UID</c>.</summary>
    public long? SourceOperationId { get; set; }

    /// <summary>
    /// Source <c>PrBomOperation.OperationKey</c>. Stable per-revision identity; drives the
    /// filtered uniqueness index. Nullable for legacy and manual rows.
    /// </summary>
    public Guid? SourceOperationKey { get; set; }

    /// <summary>Position within the route step. Not unique — parallel processes are allowed.</summary>
    public int ProcessSequence { get; set; }

    /// <summary>MACHINE | AUTOMATED | MANUAL | INSPECTION | WAIT | PACKING | SUBCONTRACT.</summary>
    public string ProcessType { get; set; } = string.Empty;

    /// <summary>
    /// Cycle duration for duration-based process types. Machine-based steps derive duration from
    /// the selected machine option instead; see <see cref="CalendarSourceType"/>.
    /// </summary>
    public decimal StandardDurationMinutes { get; set; }

    /// <summary>Consumption quantity for this step, expressed in <see cref="PlannedInputUom"/>.</summary>
    public decimal PlannedInputQty { get; set; }
    public string? PlannedInputUom { get; set; }

    /// <summary>Production quantity for this step, expressed in <see cref="PlannedOutputUom"/>.</summary>
    public decimal PlannedOutputQty { get; set; }
    public string? PlannedOutputUom { get; set; }

    /// <summary>Legacy planned quantity. Retained for version-1 compatibility.</summary>
    public decimal PlannedQty { get; set; }

    // ── Scheduling provenance ─────────────────────────────────────────────────────────────────

    /// <summary>MACHINE | PLANT_DEFAULT; see <see cref="ProductionCalendarSourceTypes"/>.</summary>
    public string? CalendarSourceType { get; set; }

    /// <summary>
    /// Identifier of the calendar used. References either <c>PrMachine</c> or the plant-default
    /// calendar depending on <see cref="CalendarSourceType"/>; not an FK because the target table
    /// varies.
    /// </summary>
    public long? CalendarSourceId { get; set; }

    /// <summary>Last-modified stamp of the calendar when it was read, for freshness checks.</summary>
    public DateTime? CalendarSourceLastModified { get; set; }

    /// <summary>Hash of the schedule inputs used for this row (plan §4.2).</summary>
    public string? ScheduleSourceHash { get; set; }

    public DateTime? CalendarHorizonStart { get; set; }
    public DateTime? CalendarHorizonEnd { get; set; }

    // ── Retained process identity and standards ───────────────────────────────────────────────

    /// <summary>Legacy work centre reference. Version-2 rows read it from the route step.</summary>
    public string? WorkCentreCode { get; set; }
    public string? WorkCentreDescription { get; set; }

    public string OperationCode { get; set; } = string.Empty;
    public string? OperationDescription { get; set; }
    public bool IsFinalOperation { get; set; }

    public decimal SetupLossQty { get; set; }
    public decimal OperationLossQty { get; set; }

    /// <summary>Plant-local planned start (plan §6.5).</summary>
    public DateTime? PlannedStartDateTime { get; set; }

    /// <summary>Plant-local planned completion (plan §6.5).</summary>
    public DateTime? PlannedCompletionDateTime { get; set; }

    // Execution projections. May only be updated from posted source facts.
    public decimal InputQty { get; set; }
    public decimal ProcessedQty { get; set; }
    public decimal GoodQty { get; set; }
    public decimal ScrapQty { get; set; }
    public decimal RejectQty { get; set; }
    public decimal HoldQty { get; set; }
    public decimal ReworkQty { get; set; }
    public decimal TransferredQty { get; set; }
    public decimal RemainingQty { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ProductionWorkOrderMachine> Machines { get; set; } = new List<ProductionWorkOrderMachine>();

    /// <summary>
    /// Direct operation-level labour. Machine-owned labour hangs off
    /// <see cref="ProductionWorkOrderMachine.Labours"/> instead. Option A1 keeps the two sets
    /// mutually exclusive per row (plan §6.4).
    /// </summary>
    public ICollection<ProductionWorkOrderLabour> Labours { get; set; } = new List<ProductionWorkOrderLabour>();

    public ICollection<ProductionWorkOrderMaterial> Materials { get; set; } = new List<ProductionWorkOrderMaterial>();

    /// <summary>
    /// Legacy generic resource rows. Retained only until compatibility readers have moved to the
    /// explicit machine/labour relationships (plan §6.4).
    /// </summary>
    public ICollection<ProductionWorkOrderResource> Resources { get; set; } = new List<ProductionWorkOrderResource>();
}

/// <summary>
/// Legacy generic resource snapshot. Deprecated: new work orders must use
/// <see cref="ProductionWorkOrderMachine"/> and <see cref="ProductionWorkOrderLabour"/>.
/// </summary>
public class ProductionWorkOrderResource
{
    public long Uid { get; set; }
    public long OperationId { get; set; }
    public ProductionWorkOrderOperation? Operation { get; set; }
    public int SequenceNo { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceCode { get; set; } = string.Empty;
    public string? ResourceDescription { get; set; }
    public decimal PlannedUnits { get; set; }
    public decimal SetupMinutes { get; set; }
    public decimal RunMinutes { get; set; }
    public decimal QueueMinutes { get; set; }
    public decimal Rate { get; set; }
    public decimal PlannedAmount { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
}
