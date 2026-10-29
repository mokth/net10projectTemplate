namespace ErpWeb.Core.Inventory;

/// <summary>
/// Stock-date eligibility (INV-02 / INV-03). Candidate pickers and locked-row posting
/// revalidation share this contract; same-day stock is allowed.
/// </summary>
internal static class IvStockDateRules
{
    /// <summary>
    /// True when the balance has a known stock date on or before the document date.
    /// Null TransDate is never eligible under the strict rule.
    /// </summary>
    public static bool IsAvailableOn(DateTime? stockDate, DateTime documentDate) =>
        stockDate.HasValue && stockDate.Value.Date <= documentDate.Date;

    /// <summary>
    /// True when the document/movement date is after the company business date.
    /// </summary>
    public static bool IsFutureMovementDate(DateTime documentDate, DateTime businessDate) =>
        documentDate.Date > businessDate.Date;

    public static string FutureStockMessage(
        string? lotNo,
        DateTime stockDate,
        DateTime documentDate)
    {
        var lot = string.IsNullOrWhiteSpace(lotNo) ? "balance" : $"lot {lotNo.Trim()}";
        return $"Stock {lot} is dated {stockDate:dd/MM/yyyy} and cannot be used for transaction date {documentDate:dd/MM/yyyy}.";
    }

    public static string FutureMovementMessage(DateTime documentDate, DateTime businessDate) =>
        $"Transaction date {documentDate:dd/MM/yyyy} is after the business date {businessDate:dd/MM/yyyy}. " +
        $"This document may remain NEW, but it cannot be posted until {documentDate:dd/MM/yyyy}.";

    public static string NullStockDateMessage() =>
        "This stock balance has no valid stock date, so it cannot be used yet. " +
        "Ask an administrator to repair the stock date for this item.";
}
