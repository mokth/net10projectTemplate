using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Core.Planning;

public enum BomExplosionMode
{
    /// <summary>Full hierarchy for BOM Explorer.</summary>
    StructuralTree = 0,

    /// <summary>Fully exploded BUY leaf extended quantities (planning/MRP).</summary>
    MaterialRequirement = 1,

    /// <summary>
    /// Direct issue lines for a production order of the root item:
    /// BUY + MAKE children after Phantom explode-through; do not issue Make descendants.
    /// </summary>
    ProductionIssueRequirement = 2
}

public sealed class BomExplosionRequest
{
    public string ProdCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
    public DateTime AsOfDate { get; set; } = DateTime.UtcNow.Date;

    /// <summary>When set, use this version even if DRAFT/SUPERSEDED (preview / historical).</summary>
    public int? Version { get; set; }

    public BomExplosionMode Mode { get; set; } = BomExplosionMode.StructuralTree;
}

public sealed class BomExplosionNode
{
    public int Level { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemDesc { get; init; }
    public string? ParentItemCode { get; init; }
    public decimal QtyPerParent { get; init; }
    public decimal ExtendedQty { get; init; }
    public string? Uom { get; init; }
    public string MfgType { get; init; } = PrMfgTypes.Buy;
    public int? BomVersion { get; init; }
    public long? BomHdrId { get; init; }
    public long? SourceLineUid { get; init; }
    public decimal ScrapPercent { get; init; }
    public string? Warehouse { get; init; }
    public string Path { get; init; } = string.Empty;
    public bool IsLeafRequirement { get; init; }
    public bool IsIssueLine { get; init; }
}

public sealed class BomExplosionResult
{
    public string RootProdCode { get; init; } = string.Empty;
    public decimal RootQuantity { get; init; }
    public DateTime AsOfDate { get; init; }
    public BomExplosionMode Mode { get; init; }
    public long? RootBomHdrId { get; init; }
    public int? RootBomVersion { get; init; }
    public decimal? RootBaseQty { get; init; }
    public string? RootBaseUom { get; init; }
    public IReadOnlyList<BomExplosionNode> Nodes { get; init; } = [];
}

public interface IBomExplosionService
{
    Task<IvMasterOperationResult<BomExplosionResult>> ExplodeAsync(
        BomExplosionRequest request,
        CancellationToken cancellationToken = default);
}
