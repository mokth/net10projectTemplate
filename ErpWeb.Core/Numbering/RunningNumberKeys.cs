namespace ErpWeb.Core.Numbering;

public static class RunningNumberKeys
{
    /// <summary>Global inventory batch number, shared by MR / issue / transfer / adj.</summary>
    public const string IvBatch = "IV_BATCH";

    /// <summary>Sales invoice period prefix. Call site appends yyyyMM (e.g. SA_INV_202609).</summary>
    public const string SaInvoice = "SA_INV";
}
