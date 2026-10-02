namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// One route-step occurrence in a Work Order snapshot (<c>dbo.PrWorkOrderRouteStep</c>).
/// <para>
/// A route step is the persistent occurrence that owns one or more process operations. Unlike the
/// legacy flattened <see cref="ProductionWorkOrderOperation.SequenceNo"/> model, the same work
/// centre may appear more than once and <see cref="StageSequence"/> is deliberately <b>not</b>
/// unique: equal stage sequences represent parallel route steps (plan §6.1).
/// </para>
/// <para>
/// Ownership: <c>WorkOrder → RouteStep → Operation → (Material | Machine → Labour | Labour)</c>.
/// See plan §6.6 for the single cascade ownership path.
/// </para>
/// </summary>
public class ProductionWorkOrderRouteStep
{
    public long Uid { get; set; }
    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }

    /// <summary>Source <c>PrBomRouteStep.UID</c>. Nullable for legacy and manual work orders.</summary>
    public long? SourceRouteStepId { get; set; }

    /// <summary>
    /// Source <c>PrBomRouteStep.RouteStepKey</c>. The stable per-revision identity used by the
    /// filtered uniqueness index and by material producer references. Nullable for legacy rows.
    /// </summary>
    public Guid? SourceRouteStepKey { get; set; }

    /// <summary>Stage position. Not unique — equals another row's value for parallel steps.</summary>
    public int StageSequence { get; set; }

    public string WorkCentreCode { get; set; } = string.Empty;
    public string? WorkCentreDescription { get; set; }

    public string OutputItemCode { get; set; } = string.Empty;
    public string? OutputItemDescription { get; set; }

    /// <summary>
    /// Frozen <c>PrBomRouteStep.OutputType</c>: WIP_STOCKED | WIP_NONSTOCK | FINISHED_GOODS.
    /// Null only on pre-V3 released snapshots; Daily Production rejects null.
    /// </summary>
    public string? OutputType { get; set; }

    /// <summary>
    /// Frozen route yield percent. New snapshots stamp 100. Non-100 execution is deferred.
    /// </summary>
    public decimal? YieldPercent { get; set; }

    /// <summary>The source route step's declared output quantity basis.</summary>
    public decimal OutputBaseQty { get; set; } = 1m;

    public string? OutputUom { get; set; }

    /// <summary>
    /// Frozen inventory base UOM of <see cref="OutputItemCode"/> (item StdUom).
    /// Set by the snapshot builder from the item master; not authored on PrBomRouteStep.
    /// </summary>
    public string? OutputBaseUom { get; set; }

    /// <summary>
    /// Frozen conversion: one <see cref="OutputUom"/> unit equals this many
    /// <see cref="OutputBaseUom"/> units. Resolved by the quantity calculator.
    /// </summary>
    public decimal? OutputConversionFactorToBase { get; set; }

    /// <summary>Planned output for this occurrence, in <see cref="OutputUom"/>.</summary>
    public decimal PlannedQty { get; set; }

    /// <summary>Plant-local (<see cref="DateTimeKind.Unspecified"/>) planned start (plan §6.5).</summary>
    public DateTime? PlannedStartDateTime { get; set; }

    /// <summary>Plant-local (<see cref="DateTimeKind.Unspecified"/>) planned completion (plan §6.5).</summary>
    public DateTime? PlannedCompletionDateTime { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ProductionWorkOrderOperation> Operations { get; set; } = new List<ProductionWorkOrderOperation>();
}
