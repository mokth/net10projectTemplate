using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Core.Sales;

/// <summary>Sales Quotation statuses. Mirrors the SaSO naming convention.</summary>
public static class SaQtStatuses
{
    public const string New = "NEW";
    public const string Sent = "SENT";
    public const string Accepted = "ACCEPTED";
    public const string Cancelled = "CANCELLED";
    public const string Lost = "LOST";
    public const string Expired = "EXPIRED";
    public const string Closed = "CLOSED";
    public const string Superseded = "SUPERSEDED";

    /// <summary>Statuses a live (IsCurrent = 1) revision may hold.</summary>
    public static readonly IReadOnlySet<string> Live = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        New, Sent, Accepted, Cancelled, Lost, Expired, Closed
    };

    /// <summary>Statuses a quotation may be revised away from without first reverting anything.</summary>
    public static readonly IReadOnlySet<string> Revisable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        New, Sent, Accepted
    };

    /// <summary>Statuses the lazy-expiry sweep may move to EXPIRED. ACCEPTED is never auto-expired.</summary>
    public static readonly IReadOnlySet<string> Expirable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        New, Sent
    };
}

/// <summary>
/// Quotation conversion state. MVP only ever writes <c>NONE</c> or <c>FULL</c>; <c>PARTIAL</c> exists
/// so partial conversion can be added later without a schema migration, and no MVP operation sets it.
/// </summary>
public static class SaQtConversionStatuses
{
    public const string None = "NONE";
    public const string Partial = "PARTIAL";
    public const string Full = "FULL";
}

/// <summary>Quotation close reasons. MVP: conversion is the only way a quotation closes.</summary>
public static class SaQtClosedReasons
{
    public const string Converted = "CONVERTED";
}

public static class SaQtReasonCodes
{
    public const string Concurrency = "QT_CONCURRENCY";
    public const string NotFound = "QT_NOT_FOUND";
    public const string Superseded = "QT_SUPERSEDED";
    public const string NotCurrent = "QT_NOT_CURRENT";
    public const string RevisionLimit = "QT_REVISION_LIMIT";
    public const string LostReason = "QT_LOST_REASON";
    public const string RevisionReason = "QT_REVISION_REASON";
    public const string Expired = "QT_EXPIRED";
    public const string NotAccepted = "QT_NOT_ACCEPTED";
    public const string AlreadyConverted = "QT_ALREADY_CONVERTED";
    public const string HasConvertedLine = "QT_HAS_CONVERTED_LINE";
    public const string SoNumbering = "QT_SO_NUMBERING";
    public const string ConversionFailed = "QT_CONVERSION_FAILED";

    public const string SupersededMessage =
        "This quotation revision is historical. Reload the current revision and try again.";

    public const string NotCurrentMessage =
        "This quotation revision is historical and cannot be changed.";

    public const string RevisionLimitMessage =
        "This quotation has reached the maximum revision number.";

    public const string LostReasonRequired =
        "A reason is required when marking a quotation as lost.";

    public const string RevisionReasonRequired =
        "A revision reason is required once the quotation has been sent or accepted.";

    public const string ExpiredMessage =
        "This quotation has expired. Revise it and accept the new revision before converting.";

    public const string NotAcceptedMessage =
        "Only an accepted quotation can be converted to a Sales Order.";

    public const string AlreadyConvertedMessage =
        "This quotation revision has already been converted to a Sales Order.";

    public const string ConvertedLineMessage =
        "This quotation cannot be revised because a line has already been converted to a Sales Order.";
}

public static class SaQtLimits
{
    public const short MaxCustRel = short.MaxValue; // 32767

    /// <summary>Guard against a single delete request fanning out over an unbounded set of documents.</summary>
    public const int MaxDistinctQtHeaders = 20;
}

public static class SaQtRevisionLimits
{
    public const short MaxCustRel = SaQtLimits.MaxCustRel;
}

/// <summary>
/// Sales Quotation date maths. All comparisons are date-only: the boundary is inclusive, so
/// <c>Today == ValidUntil</c> is still valid and only <c>Today &gt; ValidUntil</c> has expired.
/// </summary>
public static class SaQtValidity
{
    /// <summary>Default offer window applied to a new quotation when no setting resolves.</summary>
    public const int DefaultValidityDays = 30;

    public static DateTime DefaultValidUntil(DateTime qtDate) =>
        qtDate.Date.AddDays(DefaultValidityDays);

    /// <summary>
    /// Default offer window using an explicit day count — normally the company's
    /// <c>SALES.QUOTE_VALID_DAYS</c> setting. Kept pure: the caller resolves the setting.
    ///
    /// <para>
    /// A day count that is zero or negative falls back to <see cref="DefaultValidityDays"/>. A quotation
    /// that is expired the moment it is created is never what an operator meant, so a nonsense
    /// configuration degrades to the shipped default rather than producing an unusable document.
    /// </para>
    /// </summary>
    public static DateTime DefaultValidUntil(DateTime qtDate, int validityDays) =>
        qtDate.Date.AddDays(validityDays > 0 ? validityDays : DefaultValidityDays);

    /// <summary>True when the offer has lapsed. Date-only and strictly greater-than.</summary>
    public static bool IsExpired(DateTime validUntil, DateTime today) =>
        today.Date > validUntil.Date;

    public static DateTime NormalizeDate(DateTime value) => value.Date;
}

/// <summary>Only mutation path for <see cref="SaQtDetail.ConvertedQty"/>.</summary>
public static class SaQtQty
{
    public static decimal RoundQty(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    public static void SetOrderQty(SaQtDetail detail, decimal orderQty)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var order = RoundQty(orderQty);
        if (order <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(orderQty), "OrderQty must be greater than zero.");
        }

        if (detail.ConvertedQty > order)
        {
            throw new InvalidOperationException("OrderQty cannot be less than ConvertedQty.");
        }

        detail.OrderQty = order;
    }

    /// <summary>
    /// Records consumption of a quotation line. Monotonic: the stored value may only grow, and may
    /// never exceed <see cref="SaQtDetail.OrderQty"/>.
    /// </summary>
    public static void Consume(SaQtDetail detail, decimal qty)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var amount = RoundQty(qty);
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(qty), "Consume qty must be greater than zero.");
        }

        var next = RoundQty(detail.ConvertedQty + amount);
        if (next > detail.OrderQty)
        {
            throw new InvalidOperationException("Consume would exceed OrderQty.");
        }

        detail.ConvertedQty = next;
    }

    /// <summary>Remaining, not-yet-ordered quantity. Computed, never stored.</summary>
    public static decimal Remaining(SaQtDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return RoundQty(detail.OrderQty - detail.ConvertedQty);
    }
}
