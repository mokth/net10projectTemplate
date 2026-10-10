using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Traceability;

/// <summary>Read-only search and trace projection over posted inventory and production evidence.</summary>
public interface ILotGenealogyService
{
    Task<IvMasterOperationResult<LotGenealogySearchPage>> SearchAsync(
        LotGenealogySearchQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<LotGenealogyResult>> TraceInventoryLotAsync(
        int lotId,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<LotGenealogyResult>> TraceProductionLotAsync(
        long productionBalLotId,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<LotGenealogyResult>> TraceWorkOrderAsync(
        long workOrderId,
        CancellationToken cancellationToken = default);
}

public enum LotGenealogyRootKind
{
    InventoryLot,
    ProductionLot,
    WorkOrder
}

public enum LotGenealogyCompleteness
{
    Complete,
    IncompleteLegacyEvidence,
    Truncated
}

public enum LotGenealogyDirection
{
    Backward,
    Forward
}

public sealed class LotGenealogySearchQuery
{
    public string? SearchText { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class LotGenealogySearchPage
{
    public IReadOnlyList<LotGenealogySearchCandidate> Candidates { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>The displayed values help a user choose; RootId is the only value used for tracing.</summary>
public sealed class LotGenealogySearchCandidate
{
    public LotGenealogyRootKind RootKind { get; init; }
    public long RootId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? ItemCode { get; init; }
    public string? LotNo { get; init; }
    public string? WorkOrderNo { get; init; }
    public string? BranchCode { get; init; }
    public string? Detail { get; init; }
}

public sealed class LotGenealogyResult
{
    public LotGenealogyNode Root { get; init; } = new();
    public LotGenealogyCompleteness Completeness { get; init; }
    public bool WasTruncated { get; init; }
    public decimal? CurrentBranchOnHandQty { get; init; }
    public string? CurrentBranch { get; init; }
    public int AffectedFinishedGoodLotCount { get; init; }
    public int CustomersShippedCount { get; init; }
    public IReadOnlyList<LotGenealogyNode> Nodes { get; init; } = [];
    public IReadOnlyList<LotGenealogyEdge> BackwardEdges { get; init; } = [];
    public IReadOnlyList<LotGenealogyEdge> ForwardEdges { get; init; } = [];
    public IReadOnlyList<LotGenealogyCostEvidence> CostEvidence { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class LotGenealogyNode
{
    /// <summary>Stable entity identity, prefixed by entity kind for display and de-duplication.</summary>
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public string? EvidenceState { get; init; }
    public string? BranchCode { get; init; }
    public DateTime? EventDate { get; init; }
    public decimal? BaseQty { get; init; }
    public string? BaseUom { get; init; }
    public bool IsFinishedGoodLot { get; init; }
    public bool IsCustomer { get; init; }
}

public sealed class LotGenealogyEdge
{
    public string FromNodeId { get; init; } = string.Empty;
    public string ToNodeId { get; init; } = string.Empty;
    public string Relationship { get; init; } = string.Empty;
    public string EvidenceState { get; init; } = string.Empty;
    public string? Explanation { get; init; }
    public decimal? ExactBaseQty { get; init; }
    public string? BaseUom { get; init; }
    public DateTime? EventDate { get; init; }
    public bool IsActiveImpact { get; init; }
}

/// <summary>Cost evidence status only; this inquiry does not recalculate or expose cost amounts.</summary>
public sealed class LotGenealogyCostEvidence
{
    public long MovementId { get; init; }
    public string MovementType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Basis { get; init; } = string.Empty;
}
