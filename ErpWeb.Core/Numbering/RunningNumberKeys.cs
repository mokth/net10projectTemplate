namespace ErpWeb.Core.Numbering;

public static class RunningNumberKeys
{
    /// <summary>Global inventory batch number, shared by MR / issue / transfer / adj.</summary>
    public const string IvBatch = "IV_BATCH";

    /// <summary>Stock-count sheet number. Formatted <c>CC000001</c> — never an <c>SC…</c> prefix,
    /// which would read as a scrap reference on the ADJ batch the sheet produces.</summary>
    public const string InventoryStockCount = "IV_STOCK_COUNT";

    /// <summary>Sales invoice period prefix. Call site appends yyyyMM (e.g. SA_INV_202609).</summary>
    public const string SaInvoice = "SA_INV";

    /// <summary>Production Work Order number. Formatted <c>WO00000001</c>.</summary>
    public const string ProductionWorkOrder = "PR_WORK_ORDER";

    /// <summary>Daily Production / Production Output document number. Formatted <c>DP00000001</c>.</summary>
    public const string ProductionDailyOutput = "PR_DAILY_OUTPUT";
}
