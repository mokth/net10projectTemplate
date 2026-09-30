using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Production Work Order header. Product/BOM values are copied into this aggregate so later
/// master-data edits never rewrite historical requirements.
/// </summary>
public class ProductionWorkOrder
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? LocationCode { get; set; }
    public string WorkOrderNo { get; set; } = string.Empty;

    public int SnapshotRevision { get; set; } = 1;
    public string SnapshotHash { get; set; } = string.Empty;

    /// <summary>
    /// The as-of date used to resolve the source Product Definition revision. Physically the
    /// legacy <c>SnapshotAsOfDate</c> column (plan §6.6 compatibility mapping).
    /// </summary>
    public DateTime DefinitionEffectiveDate { get; set; }

    public string ProductCode { get; set; } = string.Empty;
    public string? ProductDescription { get; set; }
    public string? OutputUom { get; set; }

    public long SourceBomHdrId { get; set; }
    public PrBomHdr? SourceBomHeader { get; set; }
    public int SourceBomVersion { get; set; }
    public decimal BomBaseQty { get; set; }
    public string? BomBaseUom { get; set; }

    public decimal PlannedQty { get; set; }
    public decimal GoodQty { get; set; }
    public decimal ScrapQty { get; set; }
    public decimal RejectQty { get; set; }
    public decimal HoldQty { get; set; }
    public decimal ApprovedVarianceQty { get; set; }
    public decimal RemainingQty { get; set; }

    public DateTime PlannedStartDateTime { get; set; }
    public DateTime PlannedCompletionDateTime { get; set; }
    public string SchedulingDirection { get; set; } = ProductionSchedulingDirections.Forward;
    public string Status { get; set; } = ProductionWorkOrderStatuses.Draft;

    // ── Snapshot provenance and format (plan §6.6) ────────────────────────────────────────────

    /// <summary>
    /// Source revision's own <c>EffectiveFrom</c> date. Distinct from
    /// <see cref="DefinitionEffectiveDate"/>, which is the as-of date used to resolve it.
    /// </summary>
    public DateTime? SourceEffectiveFrom { get; set; }

    /// <summary>
    /// Physical <c>PrBomHdr.UID</c> of the Product Definition revision this snapshot was built
    /// from. Refresh re-resolves against <see cref="DefinitionEffectiveDate"/> and compares.
    /// </summary>
    public long? SourceProductDefinitionRevisionId { get; set; }

    /// <summary>
    /// Canonical hash of the Product Definition source payload consumed by the last Refresh.
    /// Lets Refresh confirm detect that the definition changed after preview (plan §4.2).
    /// </summary>
    public string? DefinitionSourceHash { get; set; }

    /// <summary>Canonicalization version for <see cref="DefinitionSourceHash"/>.</summary>
    public int? DefinitionSourceHashVersion { get; set; }

    /// <summary>
    /// <see cref="ProductionSnapshotFormatVersions.Legacy"/> for Phase-1 rows,
    /// <see cref="ProductionSnapshotFormatVersions.Current"/> for the full hierarchy snapshot.
    /// </summary>
    public int SnapshotFormatVersion { get; set; } = ProductionSnapshotFormatVersions.Legacy;

    /// <summary>Canonical hash algorithm version; see <see cref="ProductionSnapshotHashVersions"/>.</summary>
    public int SnapshotHashVersion { get; set; } = ProductionSnapshotHashVersions.Current;

    /// <summary>True while this aggregate carries a frozen version-1 snapshot (plan §6.7).</summary>
    public bool IsLegacySnapshot { get; set; } = true;

    /// <summary>Why this aggregate is still a legacy snapshot; cleared by an explicit refresh.</summary>
    public string? LegacySnapshotReason { get; set; }

    /// <summary>
    /// Anchor for forward/backward scheduling. Separate from the planned window so a recalculate
    /// can move the window without losing the anchor.
    /// </summary>
    public DateTime? ScheduleAnchorDateTime { get; set; }

    /// <summary>Diagnostic trail of the last schedule calculation. Not read by business logic.</summary>
    public string? ScheduleCalculationTrace { get; set; }

    public string SourceType { get; set; } = ProductionSourceTypes.Manual;
    public string? SourceReference { get; set; }
    public string? Remark { get; set; }

    public DateTime? ReleasedDate { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? CancelledDate { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancellationReason { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ProductionWorkOrderRouteStep> RouteSteps { get; set; } = new List<ProductionWorkOrderRouteStep>();
    public ICollection<ProductionWorkOrderMaterial> Materials { get; set; } = new List<ProductionWorkOrderMaterial>();
    public ICollection<ProductionWorkOrderOperation> Operations { get; set; } = new List<ProductionWorkOrderOperation>();
    public ICollection<ProductionAuditEvent> AuditEvents { get; set; } = new List<ProductionAuditEvent>();
    public ICollection<ProductionChangeOrder> ChangeOrders { get; set; } = new List<ProductionChangeOrder>();
    public ICollection<ProductionPostingLink> PostingLinks { get; set; } = new List<ProductionPostingLink>();
}

