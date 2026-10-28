using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Core.Inventory.Lifecycle;

/// <summary>
/// Company-scoped item business lifecycle. Derived from the two persisted flags — there is no stored
/// mutable status string, so <c>IvStockMaster.Active</c> (INACTIVE) and <c>IsDiscontinued</c>
/// (DISCONTINUED) can never contradict a third source of truth.
/// </summary>
public enum IvStockItemLifecycleStatus
{
    Active = 0,
    Discontinued = 1,
    Inactive = 2
}

public static class IvStockItemLifecycleStatusCodes
{
    public const string Active = "ACTIVE";
    public const string Discontinued = "DISCONTINUED";
    public const string Inactive = "INACTIVE";

    public static readonly IReadOnlyList<string> All = [Active, Discontinued, Inactive];
}

/// <summary>
/// The single mapper/validator for the item lifecycle. Every status read and write in the ERP goes
/// through this type; UI-only or module-local interpretations are not permitted.
/// </summary>
public static class IvStockItemLifecycle
{
    public static bool TryResolve(
        bool isActive,
        bool isDiscontinued,
        out IvStockItemLifecycleStatus status)
    {
        if (!isActive && isDiscontinued)
        {
            status = default;
            return false;
        }

        status = !isActive
            ? IvStockItemLifecycleStatus.Inactive
            : isDiscontinued
                ? IvStockItemLifecycleStatus.Discontinued
                : IvStockItemLifecycleStatus.Active;
        return true;
    }

    public static IvStockItemLifecycleStatus Resolve(IvStockMaster item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryResolve(item.IsActive, item.IsDiscontinued, out var status))
        {
            throw new InvalidOperationException(
                $"Item {item.ICode} has an invalid lifecycle state (Active=false, IsDiscontinued=true).");
        }

        return status;
    }

    public static IvStockItemLifecycleStatus ResolveOrInactive(bool isActive, bool isDiscontinued) =>
        TryResolve(isActive, isDiscontinued, out var status)
            ? status
            : IvStockItemLifecycleStatus.Inactive;

    /// <summary>Maps a status to the two persisted flags. The only place that does so.</summary>
    public static (bool IsActive, bool IsDiscontinued) ToFlags(IvStockItemLifecycleStatus status) =>
        status switch
        {
            IvStockItemLifecycleStatus.Active => (true, false),
            IvStockItemLifecycleStatus.Discontinued => (true, true),
            IvStockItemLifecycleStatus.Inactive => (false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown lifecycle status.")
        };

    public static string CodeOf(IvStockItemLifecycleStatus status) =>
        status switch
        {
            IvStockItemLifecycleStatus.Active => IvStockItemLifecycleStatusCodes.Active,
            IvStockItemLifecycleStatus.Discontinued => IvStockItemLifecycleStatusCodes.Discontinued,
            IvStockItemLifecycleStatus.Inactive => IvStockItemLifecycleStatusCodes.Inactive,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown lifecycle status.")
        };

    public static bool TryParseCode(string? code, out IvStockItemLifecycleStatus status)
    {
        switch ((code ?? string.Empty).Trim().ToUpperInvariant())
        {
            case IvStockItemLifecycleStatusCodes.Active:
                status = IvStockItemLifecycleStatus.Active;
                return true;
            case IvStockItemLifecycleStatusCodes.Discontinued:
                status = IvStockItemLifecycleStatus.Discontinued;
                return true;
            case IvStockItemLifecycleStatusCodes.Inactive:
                status = IvStockItemLifecycleStatus.Inactive;
                return true;
            default:
                status = default;
                return false;
        }
    }

    public static string LabelOf(IvStockItemLifecycleStatus status) =>
        status switch
        {
            IvStockItemLifecycleStatus.Active => "Active",
            IvStockItemLifecycleStatus.Discontinued => "Discontinued",
            IvStockItemLifecycleStatus.Inactive => "Inactive",
            _ => "Unknown"
        };

    /// <summary>
    /// Allowed transition graph. ACTIVE→INACTIVE and INACTIVE→DISCONTINUED are rejected so no caller
    /// can skip the outstanding-obligation check in one step.
    /// </summary>
    public static bool IsTransitionAllowed(
        IvStockItemLifecycleStatus from,
        IvStockItemLifecycleStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (IvStockItemLifecycleStatus.Active, IvStockItemLifecycleStatus.Discontinued) => true,
            (IvStockItemLifecycleStatus.Discontinued, IvStockItemLifecycleStatus.Active) => true,
            (IvStockItemLifecycleStatus.Discontinued, IvStockItemLifecycleStatus.Inactive) => true,
            (IvStockItemLifecycleStatus.Inactive, IvStockItemLifecycleStatus.Active) => true,
            _ => false
        };
    }

    /// <summary>User-facing rejection text for a prohibited transition.</summary>
    public static string DescribeTransitionRejection(
        IvStockItemLifecycleStatus from,
        IvStockItemLifecycleStatus to)
    {
        if (from == IvStockItemLifecycleStatus.Active && to == IvStockItemLifecycleStatus.Inactive)
        {
            return "An active item cannot be set to Inactive directly. Discontinue it first so outstanding obligations are reviewed.";
        }

        if (from == IvStockItemLifecycleStatus.Inactive && to == IvStockItemLifecycleStatus.Discontinued)
        {
            return "An inactive item cannot be moved to Discontinued.";
        }

        return $"The status change from {LabelOf(from)} to {LabelOf(to)} is not allowed.";
    }

    public const int MaxReasonLength = 500;

    /// <summary>
    /// Normalises and validates the mandatory transition reason. Returns the stored text, or an error.
    /// </summary>
    public static bool TryNormalizeReason(
        string? reason,
        out string normalized,
        out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(reason))
        {
            error = "A reason is required for an item status change.";
            return false;
        }

        var cleaned = new string(reason
            .Where(ch => !char.IsControl(ch) || ch is '\t' or '\r' or '\n')
            .ToArray())
            .Trim();

        if (cleaned.Length == 0)
        {
            error = "A reason is required for an item status change.";
            return false;
        }

        if (cleaned.Length > MaxReasonLength)
        {
            error = $"Reason must be at most {MaxReasonLength} characters.";
            return false;
        }

        normalized = cleaned;
        return true;
    }

    public static bool IsSelectableForNewDemand(IvStockMaster item) =>
        item.IsActive && !item.IsDiscontinued;

    public static bool IsSelectableForExistingFulfilment(IvStockMaster item) =>
        item.IsActive;
}
