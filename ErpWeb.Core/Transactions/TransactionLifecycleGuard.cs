namespace ErpWeb.Core.Transactions;

public static class TransactionLifecycleGuard
{
    public const string ArchivedMessage =
        "This document was deleted after rollback and is retained for audit/costing history. It cannot be edited or reposted.";

    public static void EnsureNotArchived(DateTime? deletedAtUtc, string documentLabel)
    {
        var error = ArchivedError(deletedAtUtc, documentLabel);
        if (error is not null)
            throw new InvalidOperationException(error);
    }

    public static string? ArchivedError(DateTime? deletedAtUtc, string documentLabel)
    {
        if (deletedAtUtc is null)
            return null;
        var label = string.IsNullOrWhiteSpace(documentLabel) ? "This document" : documentLabel.Trim();
        return $"{label} was deleted after rollback and is retained for audit/costing history. It cannot be edited or reposted.";
    }
}

public static class TransactionArchiveStamp
{
    public static string NormalizeReason(string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason)
            ? TransactionDeleteMessages.DefaultArchiveReason
            : reason.Trim();
        return text.Length <= TransactionDeleteMessages.MaxReasonLength
            ? text
            : text[..TransactionDeleteMessages.MaxReasonLength];
    }

    public static string? TruncateUser(string? userId, int maxLength)
    {
        var user = (userId ?? string.Empty).Trim();
        if (user.Length == 0 || maxLength <= 0)
            return null;
        return user.Length <= maxLength ? user : user[..maxLength];
    }
}
