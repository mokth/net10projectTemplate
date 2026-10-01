using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Material requirement snapshot (<c>dbo.PrWorkOrderMaterial</c>).
/// <para>
/// Version-2 rows always name their consuming operation through <see cref="WorkOrderOperationId"/>
/// and carry an explicit UOM chain: the Product Definition standard
/// (<see cref="ComponentQtyPerParent"/> / <see cref="StandardUom"/> against
/// <see cref="BomOutputQty"/> / <see cref="BomOutputUom"/>), the issue requirement
/// (<see cref="RequiredQty"/> / <see cref="RequiredUom"/>) and the inventory posting quantity
/// (<see cref="RequiredBaseQty"/> / <see cref="BaseUom"/> via
/// <see cref="ConversionFactorToBase"/>). No step may be inferred from the item code alone.
/// </para>
/// </summary>
public class ProductionWorkOrderMaterial
{
    public long Uid { get; set; }
    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }

    /// <summary>
    /// Consuming operation. Nullable in SQL only for version-1 legacy rows; every
    /// <see cref="ProductionSnapshotFormatVersions.Current"/> material must have one (plan §6.6).
    /// </summary>
    public long? WorkOrderOperationId { get; set; }
    public ProductionWorkOrderOperation? WorkOrderOperation { get; set; }

    /// <summary>Source <c>PrDefBOM.OperationId</c>, for provenance.</summary>
    public long? SourceOperationId { get; set; }

    /// <summary>
    /// Frozen <c>PrDefBOM.AlternateGroupCode</c> from the source revision. Null on legacy snapshots
    /// or when the source line was never grouped.
    /// </summary>
    public string? AlternateGroupCode { get; set; }

    /// <summary>
    /// Legacy line number. Superseded by <see cref="MaterialSequence"/>; retained so version-1
    /// rows keep their identity.
    /// </summary>
    public int LineNo { get; set; }

    /// <summary>Position within the consuming operation.</summary>
    public int MaterialSequence { get; set; }

    public long? SourceBomHdrId { get; set; }
    public PrBomHdr? SourceBomHeader { get; set; }
    public int? SourceBomVersion { get; set; }

    /// <summary>Source material row. Physically the legacy <c>SourceBomLineID</c> column.</summary>
    public long? SourceBomLineId { get; set; }
    public PrDefBOM? SourceBomLine { get; set; }

    /// <summary>Source <c>PrDefBOM.UID</c> stable key for the filtered uniqueness index.</summary>
    public Guid? SourceMaterialKey { get; set; }

    public string? ParentProductCode { get; set; }
    public string BomPath { get; set; } = string.Empty;

    public string ComponentCode { get; set; } = string.Empty;
    public string? ComponentDescription { get; set; }
    public string MfgType { get; set; } = string.Empty;

    /// <summary>Standard quantity to produce <see cref="BomOutputQty"/> of the parent.</summary>
    public decimal ComponentQtyPerParent { get; set; }

    /// <summary>UOM that <see cref="ComponentQtyPerParent"/> is expressed in.</summary>
    public string? StandardUom { get; set; }

    public decimal BomOutputQty { get; set; }
    public string? BomOutputUom { get; set; }

    public decimal ScrapPercent { get; set; }
    public decimal Tolerance { get; set; }

    /// <summary>MANUAL | BACKFLUSH | PICK_LIST; see <see cref="PrMaterialIssueMethods"/>.</summary>
    public string IssueMethod { get; set; } = string.Empty;

    /// <summary>
    /// PURCHASED | INTERNAL_ROUTE_WIP | SEPARATE_PRODUCT_DEFINITION | EXTERNAL_SUPPLY; see
    /// <see cref="PrMaterialSupplySources"/>.
    /// </summary>
    public string SupplySource { get; set; } = string.Empty;

    /// <summary>
    /// Frozen <c>PrDefBOM.ComponentDefinitionCode</c> when supply is SEPARATE_PRODUCT_DEFINITION.
    /// Identifies which child Product Definition produces this component; not a child revision UID.
    /// </summary>
    public string? ComponentDefinitionCode { get; set; }

    /// <summary>
    /// Unique in-house producer for <see cref="PrMaterialSupplySources.InternalRouteWip"/> material.
    /// Required whenever <see cref="SupplySource"/> is INTERNAL_ROUTE_WIP and null otherwise,
    /// enforced by a NULL-safe check constraint (plan §6.3).
    /// </summary>
    public long? ProducingRouteStepId { get; set; }
    public ProductionWorkOrderRouteStep? ProducingRouteStep { get; set; }

    public decimal RequiredQty { get; set; }

    /// <summary>UOM that <see cref="RequiredQty"/> is expressed in.</summary>
    public string? RequiredUom { get; set; }

    /// <summary>Inventory posting quantity, expressed in <see cref="BaseUom"/>.</summary>
    public decimal RequiredBaseQty { get; set; }

    /// <summary>Inventory base UOM for the component.</summary>
    public string? BaseUom { get; set; }

    /// <summary>Base units represented by one required unit. Must be &gt; 0.</summary>
    public decimal ConversionFactorToBase { get; set; } = 1m;

    public string? WarehouseCode { get; set; }
    public string? LocationCode { get; set; }

    // Rebuildable execution projections. They are zero until material execution is implemented and
    // may only be updated from posted source facts.
    public decimal ReservedQty { get; set; }
    public decimal PickedQty { get; set; }
    public decimal IssuedQty { get; set; }
    public decimal ReturnedQty { get; set; }
    public decimal ConsumedQty { get; set; }
    public decimal VarianceQty { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
