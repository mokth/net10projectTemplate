namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// Append-only audit of Stock Master lifecycle transitions. One row per changed item; bulk commands
/// write one row per item inside the same transaction. Deliberately has no foreign key and no cascade
/// so the evidence survives any later item deletion.
/// </summary>
public sealed class IvStockMasterLifecycleEvent
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;
    public string OldStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string SourceAction { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
}

public static class IvStockLifecycleSourceActions
{
    public const string ItemForm = "ITEM_FORM";
    public const string Bulk = "BULK";
    public const string System = "SYSTEM";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ItemForm,
        Bulk,
        System
    };
}
