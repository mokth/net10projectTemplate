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
    public const string Scrap = "SC";
    public const string StockAdjustment = "ADJ";
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

public static class IvReturnReasons
{
    public const string Return = "RETURN";
    public const string Excess = "EXCESS";
    public const string QcReject = "QC_REJECT";
    public const string WrongItem = "WRONG_ITEM";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All =
        [Return, Excess, QcReject, WrongItem, Other];
}

public static class IvAdjustmentReasons
{
    public const string Count = "COUNT";
    public const string Found = "FOUND";
    public const string Shrinkage = "SHRINKAGE";
    public const string Damage = "DAMAGE";
    public const string Data = "DATA";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All =
        [Count, Found, Shrinkage, Damage, Data, Other];
}

public static class IvScrapReasons
{
    public const string Damaged = "DAMAGED";
    public const string Expired = "EXPIRED";
    public const string QcFail = "QC_FAIL";
    public const string Overstock = "OVERSTOCK";
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All =
        [Damaged, Expired, QcFail, Overstock, Other];
}

public static class IvItemStatuses
{
    public const string Active = "ACTIVE";
    public const string Damaged = "DAMAGED";
    public const string QcHold = "QCHOLD";

    public static readonly IReadOnlyList<string> All = [Active, Damaged, QcHold];
}
