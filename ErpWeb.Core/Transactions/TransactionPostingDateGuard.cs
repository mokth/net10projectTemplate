using System.Globalization;

namespace ErpWeb.Core.Transactions;

internal static class TransactionPostingDateGuard
{
    public static string? FutureDateError(
        DateTime documentDate,
        DateTime businessDate)
    {
        var document = documentDate.Date;
        var business = businessDate.Date;
        var documentText = document.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var businessText = business.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        return document > business
            ? $"Transaction date {documentText} is after the business date {businessText}."
            : null;
    }
}
