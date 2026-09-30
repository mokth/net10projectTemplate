namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Labour snapshot row (<c>dbo.PrWorkOrderLabour</c>).
/// <para>
/// <b>Option A1 — exclusive owner with a single cascade path.</b> One table carries both
/// machine-owned and operation-level labour, and every row must have <i>exactly one</i> owner:
/// either <see cref="MachineId"/> (machine-owned) or <see cref="OperationId"/> (operation-level),
/// enforced by <c>CK_PrWorkOrderLabour_ExclusiveOwner</c>.
/// </para>
/// <para>
/// The delete graph deliberately exposes only one structural path from Operation to Labour
/// (<c>Operation → Machine → Labour</c>). The direct <c>Operation → Labour</c> relationship is
/// <c>ON DELETE NO ACTION</c>, because SQL Server validates cascade paths on schema structure
/// rather than per-row, so two structural paths would be rejected with a
/// multiple-cascade-path error even though the CHECK makes them mutually exclusive. The service
/// therefore deletes direct operation-level labour explicitly before deleting an operation.
/// </para>
/// <para>See <c>plans/work-order-plan.md</c> §6.4.</para>
/// </summary>
public class ProductionWorkOrderLabour
{
    public long Uid { get; set; }

    /// <summary>Owning machine option. Mutually exclusive with <see cref="OperationId"/>.</summary>
    public long? MachineId { get; set; }
    public ProductionWorkOrderMachine? Machine { get; set; }

    /// <summary>Owning operation for operation-level labour. Mutually exclusive with <see cref="MachineId"/>.</summary>
    public long? OperationId { get; set; }
    public ProductionWorkOrderOperation? Operation { get; set; }

    /// <summary>Source <c>PrBomLabourStandard.UID</c> or <c>PrBomLabourRequirement.UID</c>.</summary>
    public long? SourceLabourId { get; set; }

    /// <summary>Stable source labour key. Drives the filtered uniqueness indexes.</summary>
    public Guid? SourceLabourKey { get; set; }

    public string LabourCode { get; set; } = string.Empty;
    public string? LabourDescription { get; set; }

    /// <summary>
    /// Informational standard copied only when the source provides it. Does not participate in the
    /// PER_OUTPUT_UNIT equation (plan §6.4).
    /// </summary>
    public decimal? PlannedUnits { get; set; }

    /// <summary>Informational standard copied only when the source provides it.</summary>
    public decimal? PlannedMinutes { get; set; }

    /// <summary>PER_OUTPUT_UNIT; see <see cref="ProductionLabourRateBases"/>.</summary>
    public string RateBasis { get; set; } = ProductionLabourRateBases.PerOutputUnit;

    public decimal Rate { get; set; }

    /// <summary>
    /// True for operation-level labour and for labour linked to the selected machine. Labour on an
    /// unselected machine alternative stays for traceability with this false and an amount of zero.
    /// </summary>
    public bool ContributesToPlan { get; set; }

    /// <summary>Planned cost amount, derived from the consuming operation's planned output quantity.</summary>
    public decimal PlannedAmount { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
