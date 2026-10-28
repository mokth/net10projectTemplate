namespace ErpWeb.Core.Inventory.Lifecycle;

/// <summary>Why a caller is touching the item. Server-derived, never a browser-supplied boolean.</summary>
public enum ItemDemandIntent
{
    /// <summary>Creates or increases business demand for the item.</summary>
    NewDemand = 0,

    /// <summary>Fulfils or completes an existing authorized commitment.</summary>
    ExistingFulfilment = 1,

    /// <summary>Corrects an already posted/authorized quantity.</summary>
    ExistingCorrection = 2,

    /// <summary>Reads history, valuation, stock card or audit evidence.</summary>
    HistoricalRead = 3,

    /// <summary>Rolls back / reverses / replays an already posted document.</summary>
    RollbackReplay = 4
}

public sealed class ItemRetirementBlocker
{
    public string Category { get; init; } = string.Empty;
    public string SourceModule { get; init; } = string.Empty;
    public string ReferenceType { get; init; } = string.Empty;
    public string? ReferenceNo { get; init; }
    public string? Branch { get; init; }
    public decimal? Quantity { get; init; }
    public string? Uom { get; init; }
    public decimal? Value { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string? QueryRoute { get; init; }
    public string Severity { get; init; } = SeverityBlocker;
    public DateTime EvaluatedAtUtc { get; init; }

    public const string SeverityBlocker = "BLOCKER";
    public const string SeverityWarning = "WARNING";

    public bool IsBlocking => string.Equals(Severity, SeverityBlocker, StringComparison.Ordinal);
}

public sealed class ItemRetirementCheckResult
{
    public bool CanRetire { get; init; }

    /// <summary>Fail-closed marker: the evaluator could not establish an authoritative answer.</summary>
    public bool IsIndeterminate { get; init; }

    public string? Message { get; init; }
    public IReadOnlyList<ItemRetirementBlocker> Blockers { get; init; } = [];
    public IReadOnlyList<ItemRetirementBlocker> Warnings { get; init; } = [];
    public decimal OnHandQuantity { get; init; }
    public decimal InventoryValue { get; init; }
    public DateTime EvaluatedAtUtc { get; init; }

    public bool HasBlockingDependencies =>
        IsIndeterminate || Blockers.Any(x => x.IsBlocking);

    public static ItemRetirementCheckResult Retirable(
        decimal onHandQuantity,
        decimal inventoryValue,
        IReadOnlyList<ItemRetirementBlocker> warnings,
        DateTime evaluatedAtUtc) =>
        new()
        {
            CanRetire = true,
            OnHandQuantity = onHandQuantity,
            InventoryValue = inventoryValue,
            Warnings = warnings,
            EvaluatedAtUtc = evaluatedAtUtc
        };

    public static ItemRetirementCheckResult Blocked(
        IReadOnlyList<ItemRetirementBlocker> blockers,
        IReadOnlyList<ItemRetirementBlocker> warnings,
        decimal onHandQuantity,
        decimal inventoryValue,
        DateTime evaluatedAtUtc,
        string? message = null) =>
        new()
        {
            CanRetire = false,
            Message = message ?? BuildDefaultMessage(blockers),
            Blockers = blockers,
            Warnings = warnings,
            OnHandQuantity = onHandQuantity,
            InventoryValue = inventoryValue,
            EvaluatedAtUtc = evaluatedAtUtc
        };

    public static ItemRetirementCheckResult Indeterminate(
        string message,
        DateTime evaluatedAtUtc) =>
        new()
        {
            CanRetire = false,
            IsIndeterminate = true,
            Message = message,
            EvaluatedAtUtc = evaluatedAtUtc
        };

    private static string? BuildDefaultMessage(IReadOnlyList<ItemRetirementBlocker> blockers)
    {
        var count = blockers.Count(x => x.IsBlocking);
        return count == 0 ? null : $"{count} outstanding obligation(s) must be cleared first.";
    }
}

public sealed class ItemLifecycleTransitionResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public ItemRetirementCheckResult? Retirement { get; init; }
    public IvStockItemLifecycleStatus? NewStatus { get; init; }
}

public sealed class ItemDemandPolicyResult
{
    public bool Allowed { get; init; }
    public ItemDemandIntent Intent { get; init; }
    public string? ErrorMessage { get; init; }

    public static ItemDemandPolicyResult Allow(ItemDemandIntent intent) =>
        new() { Allowed = true, Intent = intent };

    public static ItemDemandPolicyResult Reject(ItemDemandIntent intent, string message) =>
        new() { Allowed = false, Intent = intent, ErrorMessage = message };
}

/// <summary>
/// A command to change one item's lifecycle, carrying the actor, reason and provenance required for
/// the audit row. Company and actor are always supplied by the server-side tenant scope.
/// </summary>
public sealed class ItemLifecycleCommand
{
    public required string ItemCode { get; init; }
    public required IvStockItemLifecycleStatus TargetStatus { get; init; }
    public required string Reason { get; init; }
    public required string SourceAction { get; init; }
    public string? CorrelationId { get; init; }
    public byte[]? ExpectedRowVersion { get; init; }
    public bool TreatSameStatusAsNoChange { get; init; } = true;
}
