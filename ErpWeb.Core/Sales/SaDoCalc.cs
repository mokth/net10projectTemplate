namespace ErpWeb.Core.Sales;

public static class SaDoStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
    public const string Closed = "CLOSED";
}

public static class SaDoLimits
{
    public const int MaxPostSelection = 3;
}

/// <summary>
/// Helpers for converting a DO number into the SP batch reference format used by IvTrxBatch.RefNo.
/// </summary>
public static class SaDoSpRefs
{
    public const string Prefix = "DO/";

    /// <summary>Returns "DO/{doNo.Trim()}" — the value stored as IvTrxBatch.RefNo.</summary>
    public static string ToRefNo(string doNo) => Prefix + doNo.Trim();
}

public static class SaDoPostReasonCodes
{
    public const string Concurrency = "POST_CONCURRENCY";
    public const string NoLines = "POST_NO_LINES";
    public const string SpMissing = "POST_SP_MISSING";
    public const string DateMismatch = "POST_DATE_MISMATCH";
    public const string SpIncomplete = "POST_SP_INCOMPLETE";
}
