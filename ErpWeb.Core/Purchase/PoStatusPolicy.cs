using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

/// <summary>Only writer of POStat. Pages and other modules must not set status directly.</summary>
public static class PoStatusPolicy
{
    public static void Cancel(PoOrder header)
    {
        ArgumentNullException.ThrowIfNull(header);
        header.Status = PoOrderStatuses.Cancelled;
    }

    public static void ForceClose(PoOrder header, string reason, string userId, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(header);
        var closeReason = (reason ?? string.Empty).Trim();
        if (closeReason.Length == 0)
        {
            throw new ArgumentException("Close reason is required.", nameof(reason));
        }

        header.Status = PoOrderStatuses.Closed;
        header.CloseReason = Truncate(closeReason, 200);
        header.ClosedBy = Truncate((userId ?? string.Empty).Trim(), 20);
        header.ClosedOn = now;
    }

    /// <summary>
    /// Only public way off force-close. Does not persist a fake intermediate status.
    /// Clears close audit fields.
    /// </summary>
    public static string? Reopen(PoOrder header, Func<PoOrderDetail, bool> isServiceLine)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(isServiceLine);

        if (!string.Equals(header.Status, PoOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return "Only CLOSED purchase orders can be reopened.";
        }

        if (!PoOrderCalc.HasRemainingBalance(header.Details, isServiceLine))
        {
            return "Unable to reopen. No available quantity remains for this PO.";
        }

        header.Status = CalculateOperationalStatusSkippingForceClose(header, isServiceLine);
        header.CloseReason = null;
        header.ClosedBy = null;
        header.ClosedOn = null;
        return null;
    }

    public static string CalculateOperationalStatus(PoOrder header, Func<PoOrderDetail, bool> isServiceLine)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(isServiceLine);

        if (string.Equals(header.Status, PoOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return PoOrderStatuses.Cancelled;
        }

        if (string.Equals(header.Status, PoOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase)
            && PoOrderCalc.HasRemainingBalance(header.Details, isServiceLine))
        {
            return PoOrderStatuses.Closed;
        }

        return CalculateOperationalStatusSkippingForceClose(header, isServiceLine);
    }

    public static bool IsForceClosed(PoOrder header, Func<PoOrderDetail, bool> isServiceLine)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(isServiceLine);
        return string.Equals(header.Status, PoOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase)
            && PoOrderCalc.HasRemainingBalance(header.Details, isServiceLine);
    }

    public static bool IsEditableStatus(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToUpperInvariant();
        return s is PoOrderStatuses.New or PoOrderStatuses.Open or PoOrderStatuses.Received;
    }

    public static bool IsGrPickable(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToUpperInvariant();
        // Legacy OPEN stays editable/filterable but is not GR-pickable mid-edit.
        return s is PoOrderStatuses.New or PoOrderStatuses.Received;
    }

    public static bool IsViewOnlyLeftover(string? status)
    {
        var s = (status ?? string.Empty).Trim().ToUpperInvariant();
        return s is PoOrderStatuses.Pending or PoOrderStatuses.Checked;
    }

    private static string CalculateOperationalStatusSkippingForceClose(
        PoOrder header,
        Func<PoOrderDetail, bool> isServiceLine)
    {
        var details = header.Details;
        var nonService = details.Where(d => !isServiceLine(d)).ToList();
        if (nonService.Count > 0 && nonService.All(d => d.BalanceQty <= 0m))
        {
            return PoOrderStatuses.Closed;
        }

        if (PoOrderCalc.AnyReceived(details))
        {
            return PoOrderStatuses.Received;
        }

        return PoOrderStatuses.New;
    }

    private static string? Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
