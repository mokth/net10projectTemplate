namespace ErpWeb.Core.Planning;

public static class PreventiveStatuses
{
    public const string Planned = "PLANNED";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static bool IsKnown(string? status) =>
        string.Equals(status, Planned, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, Cancelled, StringComparison.OrdinalIgnoreCase);

    public static bool BlocksSchedule(string? status) =>
        string.Equals(status, Planned, StringComparison.OrdinalIgnoreCase);
}

public static class PrMaintenanceTypes
{
    public const string Preventive = "PREVENTIVE";
    public const string Breakdown = "BREAKDOWN";
    public const string Repair = "REPAIR";
    public const string Service = "SERVICE";
    public const string Other = "OTHER";

    public static readonly string[] All =
    [
        Preventive, Breakdown, Repair, Service, Other
    ];

    public static bool IsKnown(string? type) =>
        All.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
}

public static class PrMaintenanceStatuses
{
    public const string Open = "OPEN";
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static readonly string[] All =
    [
        Open, InProgress, Completed, Cancelled
    ];

    public static bool IsKnown(string? status) =>
        All.Any(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
}

public static class PrMaintenanceReasonTypes
{
    public const string Preventive = "PREVENTIVE";
    public const string Breakdown = "BREAKDOWN";
    public const string Repair = "REPAIR";
    public const string Service = "SERVICE";
    public const string Other = "OTHER";

    public static readonly string[] All =
    [
        Preventive, Breakdown, Repair, Service, Other
    ];

    public static bool IsKnown(string? type) =>
        All.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
}
