namespace ErpWeb.Core.Inventory;

/// <summary>
/// Stock movement chronology helpers (INV-04 NEW POST vs INV-05 ROLLBACK).
/// Document dates are date-grain (midnight); same-day rollback order uses history Id.
/// </summary>
internal static class IvStockMovementRules
{
    /// <summary>
    /// NEW POST (INV-04): existing history is "later" only on a later calendar day.
    /// Same-day history does not block a new posting.
    /// </summary>
    public static bool IsLaterDayMovement(DateTime historyTrxDtTime, DateTime documentDate) =>
        historyTrxDtTime.Date > documentDate.Date;

    /// <summary>
    /// ROLLBACK (INV-05): remaining history is later when its date is later, or the same
    /// date with a greater history Id than the rollback batch's max Id on that BalLoc.
    /// </summary>
    public static bool IsLaterRollbackMovement(
        DateTime remainingTrxDtTime,
        int remainingHistoryId,
        DateTime targetTrxDtTime,
        int targetMaxHistoryId) =>
        remainingTrxDtTime.Date > targetTrxDtTime.Date
        || (remainingTrxDtTime.Date == targetTrxDtTime.Date && remainingHistoryId > targetMaxHistoryId);

    public static string LaterDayMovementMessage(
        string? iCode,
        string? whCode,
        string? lotNo,
        DateTime laterDate,
        DateTime documentDate)
    {
        var item = string.IsNullOrWhiteSpace(iCode) ? "This stock balance" : $"Item {iCode.Trim()}";
        var wh = string.IsNullOrWhiteSpace(whCode) ? string.Empty : $" / {whCode.Trim()}";
        var lot = string.IsNullOrWhiteSpace(lotNo) ? string.Empty : $" / {lotNo.Trim()}";
        return $"{item}{wh}{lot} has a later posted stock movement dated {laterDate:dd/MM/yyyy}. " +
               $"A transaction dated {documentDate:dd/MM/yyyy} cannot modify this stock balance.";
    }

    public static string RollbackLaterMovementMessage(int batchNo, int balLocId, DateTime laterDate) =>
        $"Batch {batchNo} cannot be rolled back because stock balance {balLocId} " +
        $"has a later posted movement dated {laterDate:dd/MM/yyyy}. Roll back the later transaction first.";

    public static string EnsureValidMovementDate(DateTime documentDate, DateTime businessDate) =>
        IvStockDateRules.IsFutureMovementDate(documentDate, businessDate)
            ? IvStockDateRules.FutureMovementMessage(documentDate, businessDate)
            : string.Empty;

    /// <summary>
    /// Defaults a missing document date to the business date, then rejects future movement dates.
    /// </summary>
    public static (DateTime Date, string? Error) ResolveMovementDate(
        DateTime requestDate,
        DateTime businessDate)
    {
        var date = requestDate == default ? businessDate.Date : requestDate.Date;
        var error = EnsureValidMovementDate(date, businessDate);
        return (date, string.IsNullOrEmpty(error) ? null : error);
    }
}
