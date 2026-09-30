namespace ErpWeb.Core.Planning;

public enum HierarchyChangeKind
{
    Added = 0,
    Modified = 1,
    Deleted = 2,
    Relocated = 3
}

public enum HierarchyNodeType
{
    WorkCentre = 0,
    Process = 1,
    Machine = 2
}

public sealed class HierarchyNodeEditDto
{
    public string ClientNodeId { get; set; } = string.Empty;
    public HierarchyNodeType NodeType { get; set; }
    public HierarchyChangeKind ChangeKind { get; set; }

    public string? Code { get; set; }
    public string? Description { get; set; }
    public string? ParentCode { get; set; }

    /// <summary>For Relocated machines: previous ProcessCd.</summary>
    public string? OriginalProcessCd { get; set; }

    public string? ClassCode { get; set; }
    public int? Sequence { get; set; }
    public bool? Stock { get; set; }
    public double? ConversionTime { get; set; }
    public double? StartupTime { get; set; }
    public double? QueueTime { get; set; }

    /// <summary>Optimistic concurrency token (Updated ticks or loaded Updated value).</summary>
    public DateTime? OriginalUpdated { get; set; }
}

public sealed class HierarchyNodeVm
{
    public string NodeId { get; init; } = string.Empty;
    public string? ParentNodeId { get; init; }
    public HierarchyNodeType NodeType { get; init; }
    public string Code { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ClassCode { get; init; }
    public int? Sequence { get; init; }
    public double? ConversionTime { get; init; }
    public double? StartupTime { get; init; }
    public double? QueueTime { get; init; }
    public DateTime? Updated { get; init; }
}

public sealed class HierarchySaveRequest
{
    public IReadOnlyList<HierarchyNodeEditDto> Changes { get; init; } = [];
}
