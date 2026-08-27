namespace ErpWeb.Core.Inventory;

public static class IvTrxTypes
{
    public const string MiscellaneousReceipt = "MR";
    public const string CustomerReturn = "CR";
    public const string GoodsReceive = "GR";
    public const string FinishedGoods = "FG";
    public const string VendorReturn = "VR";
    public const string StockTransfer = "TR";
    public const string IssueToProduction = "IP";
    public const string MiscellaneousIssue = "MI";
    public const string SalesOut = "SP";
}

public static class IvBatchStatuses
{
    public const string New = "NEW";
    public const string Posted = "POSTED";
    public const string Cancelled = "CANCELLED";
}

public static class IvPostingLimits
{
    public const int MaxPostSelection = 10;
}

public static class IvQty
{
    public const int Scale = 4;

    public static decimal Round(decimal value) =>
        decimal.Round(value, Scale, MidpointRounding.AwayFromZero);
}


public static class IvTrxReasons
{
    public static readonly IReadOnlyList<string> All = ["ADJ", "FOUND", "SAMPLE", "RETURN", "OTHER"];
}

public static class IvItemStatuses
{
    public const string Active = "ACTIVE";
    public const string Damaged = "DAMAGED";
    public const string QcHold = "QCHOLD";

    public static readonly IReadOnlyList<string> All = [Active, Damaged, QcHold];
}
