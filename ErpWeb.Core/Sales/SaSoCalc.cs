using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Core.Sales;

public static class SaSoStatuses
{
    public const string New = "NEW";
    public const string Shipped = "SHIPPED";
    public const string Closed = "CLOSED";
    public const string Superseded = "SUPERSEDED";
}

public static class SaSoClosedReasons
{
    public const string FullyConsumed = "FULLY_CONSUMED";
    public const string ForceClosed = "FORCE_CLOSED";
}

public static class SaSoLimits
{
    public const int MaxDistinctSoHeaders = 20;
    public const int MaxForceCloseSelection = 3;
}

public static class SaSoLockOrder
{
    /// <summary>Deadlock-avoidance sort only — not identity normalization. PK collation owns uniqueness.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
}

public static class SaSoReasonCodes
{
    public const string Concurrency = "SO_CONCURRENCY";
    public const string NotFound = "SO_NOT_FOUND";
    public const string Closed = "SO_CLOSED";
    public const string ForceClosed = "SO_FORCE_CLOSED";
    public const string OverConsume = "SO_OVER_CONSUME";
    public const string TooManyHeaders = "SO_TOO_MANY_HEADERS";
    public const string LinkDoNotSupported = "SO_LINKDO_NOT_SUPPORTED";
    public const string RollbackNoConsume = "SO_ROLLBACK_NO_CONSUME";
    public const string Superseded = "SO_SUPERSEDED";
    public const string Revised = "SO_REVISED";
    public const string RevisionLimit = "SO_REVISION_LIMIT";
    public const string HasReserve = "SO_HAS_RESERVE";
    public const string HasDocument = "SO_HAS_DOCUMENT";
    public const string HasConsumption = "SO_HAS_CONSUMPTION";
    public const string TooManyHeadersMessage = "This document cannot reference more than 20 Sales Orders.";
    public const string RevisionLimitMessage = "This Sales Order has reached the maximum revision number.";
    public const string RevisedMessage = "Sales Order was revised. Reload.";
    public const string SupersededMessage = "This Sales Order revision is historical and cannot be changed.";
}

public static class SaSoRevisionLimits
{
    public const short MaxCustRel = short.MaxValue; // 32767
}

/// <summary>Only mutation path for OrderQty / ShippedQty / BalanceQty on a detail line.</summary>
public static class SaSoQty
{
    public static void SetOrderQty(SaSoDetail detail, decimal orderQty)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var order = RoundQty(orderQty);
        if (order <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(orderQty), "OrderQty must be greater than zero.");
        }

        if (detail.DeliveredQty > order)
        {
            throw new InvalidOperationException("OrderQty cannot be less than DeliveredQty.");
        }

        if (detail.InvoicedQty > order)
        {
            throw new InvalidOperationException("OrderQty cannot be less than InvoicedQty.");
        }

        if (detail.ShippedQty > order)
        {
            throw new InvalidOperationException("OrderQty cannot be less than ShippedQty.");
        }

        detail.OrderQty = order;
        detail.BalanceQty = RoundQty(order - detail.DeliveredQty);
    }

    public static void Consume(SaSoDetail detail, decimal qty)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var amount = RoundQty(qty);
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(qty), "Consume qty must be greater than zero.");
        }

        var next = RoundQty(detail.ShippedQty + amount);
        if (next > detail.OrderQty)
        {
            throw new InvalidOperationException("Consume would exceed OrderQty.");
        }

        detail.ShippedQty = next;
        detail.BalanceQty = RoundQty(detail.OrderQty - next);
    }

    public static void Reverse(SaSoDetail detail, decimal qty)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var amount = RoundQty(qty);
        if (amount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(qty), "Reverse qty cannot be negative.");
        }

        if (amount > detail.ShippedQty)
        {
            throw new InvalidOperationException("Reverse would make ShippedQty negative.");
        }

        var next = RoundQty(detail.ShippedQty - amount);
        detail.ShippedQty = next;
        detail.BalanceQty = RoundQty(detail.OrderQty - next);
    }

    public static decimal RoundQty(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
